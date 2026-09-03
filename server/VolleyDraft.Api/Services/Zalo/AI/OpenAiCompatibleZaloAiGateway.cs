using System.Diagnostics;
using System.Net;
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
                    ZaloAiFailureKind.Cancelled,
                    profile,
                    model,
                    attempt - 1,
                    null,
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
                    var failureKind = MapStatus(response.StatusCode);
                    lastFailure = Failure(
                        failureKind,
                        profile,
                        model,
                        attempt,
                        (int)response.StatusCode,
                        usedFallback);

                    _logger.LogWarning(
                        "Zalo AI {Workload} provider {Provider} model {Model} returned {StatusCode} on attempt {Attempt}: {Body}",
                        request.Workload,
                        profile.Name,
                        model,
                        (int)response.StatusCode,
                        attempt,
                        Truncate(body, 500));

                    if (attempt <= _retryCount && IsRetryable(failureKind))
                    {
                        await DelayBeforeRetryAsync(attempt, cancellationToken);
                        continue;
                    }

                    return lastFailure;
                }

                var content = ExtractContent(body);
                if (string.IsNullOrWhiteSpace(content))
                {
                    lastFailure = Failure(
                        ZaloAiFailureKind.InvalidResponse,
                        profile,
                        model,
                        attempt,
                        (int)response.StatusCode,
                        usedFallback);

                    _logger.LogWarning(
                        "Zalo AI {Workload} provider {Provider} model {Model} returned an unsupported payload shape",
                        request.Workload,
                        profile.Name,
                        model);

                    return lastFailure;
                }

                return new ZaloAiCompletionResult(
                    true,
                    content.Trim(),
                    ZaloAiFailureKind.None,
                    profile.Name,
                    model,
                    attempt,
                    (int)response.StatusCode,
                    TimeSpan.Zero,
                    usedFallback);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = Failure(
                    ZaloAiFailureKind.Timeout,
                    profile,
                    model,
                    attempt,
                    null,
                    usedFallback);

                _logger.LogWarning(
                    "Zalo AI {Workload} provider {Provider} model {Model} timed out on attempt {Attempt}",
                    request.Workload,
                    profile.Name,
                    model,
                    attempt);

                if (attempt <= _retryCount)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                return lastFailure;
            }
            catch (HttpRequestException exception)
            {
                lastFailure = Failure(
                    ZaloAiFailureKind.TransientProvider,
                    profile,
                    model,
                    attempt,
                    null,
                    usedFallback);

                _logger.LogWarning(
                    exception,
                    "Zalo AI {Workload} provider {Provider} model {Model} transport failure on attempt {Attempt}",
                    request.Workload,
                    profile.Name,
                    model,
                    attempt);

                if (attempt <= _retryCount)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                return lastFailure;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Zalo AI {Workload} provider {Provider} model {Model} returned invalid JSON",
                    request.Workload,
                    profile.Name,
                    model);

                return Failure(
                    ZaloAiFailureKind.InvalidResponse,
                    profile,
                    model,
                    attempt,
                    null,
                    usedFallback);
            }
        }

        return lastFailure ?? Failure(
            ZaloAiFailureKind.ProviderError,
            profile,
            model,
            0,
            null,
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

    private static ZaloAiFailureKind MapStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return ZaloAiFailureKind.Unauthorized;
        if (code == 408)
            return ZaloAiFailureKind.Timeout;
        if (code == 429)
            return ZaloAiFailureKind.RateLimited;
        if (code >= 500)
            return ZaloAiFailureKind.TransientProvider;
        return ZaloAiFailureKind.ProviderError;
    }

    private static bool IsRetryable(ZaloAiFailureKind kind) => kind is
        ZaloAiFailureKind.Timeout or
        ZaloAiFailureKind.RateLimited or
        ZaloAiFailureKind.TransientProvider;

    private static bool ShouldFallback(ZaloAiFailureKind kind) => kind is
        ZaloAiFailureKind.Unauthorized or
        ZaloAiFailureKind.Timeout or
        ZaloAiFailureKind.RateLimited or
        ZaloAiFailureKind.TransientProvider or
        ZaloAiFailureKind.ProviderError or
        ZaloAiFailureKind.InvalidResponse;

    private static async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(150 * attempt);
        await Task.Delay(delay, cancellationToken);
    }

    private static string? ExtractContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content))
            return content.GetString();

        if (root.TryGetProperty("output_text", out var outputText))
            return outputText.GetString();

        return null;
    }

    private static ZaloAiCompletionResult Failure(
        ZaloAiFailureKind kind,
        ZaloAiProviderProfile profile,
        string model,
        int attempts,
        int? statusCode,
        bool usedFallback) =>
        new(
            false,
            null,
            kind,
            profile.Name,
            model,
            attempts,
            statusCode,
            TimeSpan.Zero,
            usedFallback);

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
}
