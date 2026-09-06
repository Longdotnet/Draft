using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VolleyDraft.Api.Services.Zalo.AI;

/// <summary>
/// Provider-neutral boundary for OpenAI-compatible chat APIs.
/// Domain features choose a workload, not an endpoint/model. Provider/model selection,
/// timeout, retry and optional fallback live here so model changes do not touch feature code.
/// </summary>
public sealed class OpenAiCompatibleZaloAiGateway : IZaloAiGateway
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleZaloAiGateway> _logger;
    private readonly ZaloAiProviderProfile? _primary;
    private readonly ZaloAiProviderProfile? _fallback;
    private readonly int _retryCount;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleZaloAiGateway(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenAiCompatibleZaloAiGateway> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _primary = LoadProfile(configuration, "Ai", configuration["Ai:Provider"] ?? "primary");
        _fallback = LoadFallbackProfile(configuration);
        _retryCount = Math.Clamp(configuration.GetValue("Ai:RetryCount", 1), 0, 2);
        _timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Ai:TimeoutSeconds", 15), 3, 60));
    }

    public bool IsConfigured => _primary is not null;

    public async Task<ZaloAiCompletionResult> CompleteAsync(
        ZaloAiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_primary is null)
            return ZaloAiCompletionResult.NotConfigured(request.Workload);

        var started = Stopwatch.StartNew();
        var primaryResult = await ExecuteProfileAsync(
            _primary,
            request,
            usedFallback: false,
            cancellationToken);

        if (primaryResult.Success || cancellationToken.IsCancellationRequested || _fallback is null)
            return primaryResult with { Duration = started.Elapsed };

        if (!ShouldFallback(primaryResult.FailureKind))
            return primaryResult with { Duration = started.Elapsed };

        _logger.LogWarning(
            "Zalo AI workload {Workload} falling back from {PrimaryProvider}/{PrimaryModel} after {FailureKind}",
            request.Workload,
            primaryResult.Provider,
            primaryResult.Model,
            primaryResult.FailureKind);

        var fallbackResult = await ExecuteProfileAsync(
            _fallback,
            request,
            usedFallback: true,
            cancellationToken);

        return fallbackResult with
        {
            Attempts = primaryResult.Attempts + fallbackResult.Attempts,
            Duration = started.Elapsed
        };
    }

    private async Task<ZaloAiCompletionResult> ExecuteProfileAsync(
        ZaloAiProviderProfile profile,
        ZaloAiCompletionRequest request,
        bool usedFallback,
        CancellationToken cancellationToken)
    {
        var model = profile.ResolveModel(request);
        ZaloAiCompletionResult? lastFailure = null;

        for (var attempt = 1; attempt <= _retryCount + 1; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    new AiProviderFailure(AiProviderFailureKind.Cancelled),
                    profile,
                    model,
                    attempt - 1,
                    usedFallback);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint)
                {
                    Content = JsonContent.Create(new
                    {
                        model,
                        temperature = request.Temperature,
                        max_tokens = request.MaxTokens,
                        messages = request.Messages.Select(item => new
                        {
                            role = item.Role,
                            content = item.Content
                        })
                    })
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);

                using var response = await _httpClient.SendAsync(message, timeoutCts.Token);
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var providerFailure = AiProviderFailure.FromHttp(response.StatusCode, body);
                    lastFailure = Failure(providerFailure, profile, model, attempt, usedFallback);
                    LogFailure(request.Workload, profile, model, attempt, providerFailure);

                    if (attempt <= _retryCount && providerFailure.Retryable)
                    {
                        await DelayBeforeRetryAsync(attempt, cancellationToken);
                        continue;
                    }

                    return lastFailure;
                }

                var completion = ExtractCompletion(body);
                if (string.IsNullOrWhiteSpace(completion.Content))
                {
                    var invalidResponse = new AiProviderFailure(
                        AiProviderFailureKind.InvalidResponse,
                        (int)response.StatusCode);
                    lastFailure = Failure(invalidResponse, profile, model, attempt, usedFallback);
                    LogFailure(request.Workload, profile, model, attempt, invalidResponse);
                    return lastFailure;
                }

                return new ZaloAiCompletionResult(
                    true,
                    completion.Content.Trim(),
                    ZaloAiFailureKind.None,
                    profile.Name,
                    model,
                    attempt,
                    (int)response.StatusCode,
                    TimeSpan.Zero,
                    usedFallback,
                    completion.FinishReason);
            }
            catch (OperationCanceledException exception)
            {
                var providerFailure = AiProviderFailure.FromException(exception, cancellationToken);
                lastFailure = Failure(providerFailure, profile, model, attempt, usedFallback);
                LogFailure(request.Workload, profile, model, attempt, providerFailure, exception);

                if (providerFailure.Kind == AiProviderFailureKind.Cancelled)
                    return lastFailure;

                if (attempt <= _retryCount && providerFailure.Retryable)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                return lastFailure;
            }
            catch (HttpRequestException exception)
            {
                var providerFailure = AiProviderFailure.FromException(exception, cancellationToken);
                lastFailure = Failure(providerFailure, profile, model, attempt, usedFallback);
                LogFailure(request.Workload, profile, model, attempt, providerFailure, exception);

                if (attempt <= _retryCount && providerFailure.Retryable)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                return lastFailure;
            }
            catch (JsonException exception)
            {
                var providerFailure = AiProviderFailure.FromException(exception, cancellationToken);
                LogFailure(request.Workload, profile, model, attempt, providerFailure, exception);
                return Failure(providerFailure, profile, model, attempt, usedFallback);
            }
        }

        return lastFailure ?? Failure(
            new AiProviderFailure(AiProviderFailureKind.Unknown),
            profile,
            model,
            0,
            usedFallback);
    }

    private static ZaloAiProviderProfile? LoadProfile(
        IConfiguration configuration,
        string prefix,
        string defaultName)
    {
        var endpoint = configuration[$"{prefix}:Endpoint"];
        var apiKey = configuration[$"{prefix}:ApiKey"];
        var model = configuration[$"{prefix}:Model"];
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
            return null;

        var models = Enum.GetValues<ZaloAiWorkload>()
            .Select(workload => new
            {
                Workload = workload,
                Model = configuration[$"{prefix}:Models:{workload}"]
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Model))
            .ToDictionary(item => item.Workload, item => item.Model!.Trim());

        return new ZaloAiProviderProfile(
            configuration[$"{prefix}:Provider"] ?? defaultName,
            endpoint.Trim(),
            apiKey.Trim(),
            model.Trim(),
            models);
    }

    private static ZaloAiProviderProfile? LoadFallbackProfile(IConfiguration configuration)
    {
        var nested = LoadProfile(configuration, "Ai:Fallback", "fallback");
        if (nested is not null)
            return nested;

        var endpoint = configuration["Ai:FallbackEndpoint"];
        var apiKey = configuration["Ai:FallbackApiKey"];
        var model = configuration["Ai:FallbackModel"];
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
            return null;

        return new ZaloAiProviderProfile(
            configuration["Ai:FallbackProvider"] ?? "fallback",
            endpoint.Trim(),
            apiKey.Trim(),
            model.Trim(),
            new Dictionary<ZaloAiWorkload, string>());
    }

    private static bool ShouldFallback(ZaloAiFailureKind kind) => kind is
        ZaloAiFailureKind.AuthenticationFailed or
        ZaloAiFailureKind.QuotaExceeded or
        ZaloAiFailureKind.RateLimited or
        ZaloAiFailureKind.Timeout or
        ZaloAiFailureKind.ProviderUnavailable or
        ZaloAiFailureKind.ModelOrEndpointUnavailable or
        ZaloAiFailureKind.InvalidResponse or
        ZaloAiFailureKind.NetworkFailure or
        ZaloAiFailureKind.Unknown;

    private static async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(150 * attempt);
        await Task.Delay(delay, cancellationToken);
    }

    private static (string? Content, string? FinishReason) ExtractCompletion(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            string? finishReason = null;
            if (first.TryGetProperty("finish_reason", out var finishReasonNode) &&
                finishReasonNode.ValueKind == JsonValueKind.String)
                finishReason = finishReasonNode.GetString();

            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
                return (content.GetString(), finishReason);
        }

        if (root.TryGetProperty("output_text", out var outputText))
            return (outputText.GetString(), null);

        return (null, null);
    }

    private static ZaloAiCompletionResult Failure(
        AiProviderFailure failure,
        ZaloAiProviderProfile profile,
        string model,
        int attempts,
        bool usedFallback) =>
        new(
            false,
            null,
            MapFailureKind(failure.Kind),
            profile.Name,
            model,
            attempts,
            failure.StatusCode,
            TimeSpan.Zero,
            usedFallback,
            ProviderCode: failure.ProviderCode,
            Retryable: failure.Retryable);

    private static ZaloAiFailureKind MapFailureKind(AiProviderFailureKind kind) => kind switch
    {
        AiProviderFailureKind.NotConfigured => ZaloAiFailureKind.NotConfigured,
        AiProviderFailureKind.AuthenticationFailed => ZaloAiFailureKind.AuthenticationFailed,
        AiProviderFailureKind.QuotaExceeded => ZaloAiFailureKind.QuotaExceeded,
        AiProviderFailureKind.RateLimited => ZaloAiFailureKind.RateLimited,
        AiProviderFailureKind.Timeout => ZaloAiFailureKind.Timeout,
        AiProviderFailureKind.ProviderUnavailable => ZaloAiFailureKind.ProviderUnavailable,
        AiProviderFailureKind.ModelOrEndpointUnavailable => ZaloAiFailureKind.ModelOrEndpointUnavailable,
        AiProviderFailureKind.InvalidRequest => ZaloAiFailureKind.InvalidRequest,
        AiProviderFailureKind.InvalidResponse => ZaloAiFailureKind.InvalidResponse,
        AiProviderFailureKind.NetworkFailure => ZaloAiFailureKind.NetworkFailure,
        AiProviderFailureKind.Cancelled => ZaloAiFailureKind.Cancelled,
        _ => ZaloAiFailureKind.Unknown
    };

    private void LogFailure(
        ZaloAiWorkload workload,
        ZaloAiProviderProfile profile,
        string model,
        int attempt,
        AiProviderFailure failure,
        Exception? exception = null)
    {
        _logger.LogWarning(
            "Zalo AI request failed. Workload={Workload} Provider={Provider} Model={Model} Attempt={Attempt} FailureKind={FailureKind} StatusCode={StatusCode} ProviderCode={ProviderCode} Retryable={Retryable} ExceptionType={ExceptionType}",
            workload,
            profile.Name,
            model,
            attempt,
            failure.Kind,
            failure.StatusCode,
            failure.ProviderCode,
            failure.Retryable,
            exception?.GetType().Name);
    }
}
