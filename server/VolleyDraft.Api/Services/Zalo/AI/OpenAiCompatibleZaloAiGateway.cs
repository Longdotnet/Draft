using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VolleyDraft.Api.Services.Zalo.AI;

/// <summary>
/// Provider-neutral boundary for OpenAI-compatible chat APIs.
/// Domain features choose a workload, not an endpoint/model. Provider/model selection,
/// timeout, retry and ordered failover live here so model changes do not touch feature code.
/// </summary>
public sealed class OpenAiCompatibleZaloAiGateway : IZaloAiGateway
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleZaloAiGateway> _logger;
    private readonly IReadOnlyList<ZaloAiProviderProfile> _providers;
    private readonly int _retryCount;
    private readonly TimeSpan _defaultTimeout;
    private readonly IReadOnlyDictionary<ZaloAiWorkload, TimeSpan> _workloadTimeouts;

    public OpenAiCompatibleZaloAiGateway(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenAiCompatibleZaloAiGateway> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _providers = LoadProfiles(configuration);
        _retryCount = Math.Clamp(configuration.GetValue("Ai:RetryCount", 0), 0, 2);
        _defaultTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Ai:TimeoutSeconds", 6), 3, 60));
        _workloadTimeouts = LoadWorkloadTimeouts(configuration, _defaultTimeout);
    }

    public bool IsConfigured => _providers.Count > 0;

    public async Task<ZaloAiCompletionResult> CompleteAsync(
        ZaloAiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0)
            return ZaloAiCompletionResult.NotConfigured(request.Workload);

        var started = Stopwatch.StartNew();
        var totalAttempts = 0;
        ZaloAiCompletionResult? lastResult = null;

        for (var providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
        {
            var profile = _providers[providerIndex];
            var result = await ExecuteProfileAsync(
                profile,
                request,
                usedFallback: providerIndex > 0,
                cancellationToken);

            totalAttempts += result.Attempts;
            result = result with
            {
                Attempts = totalAttempts,
                Duration = started.Elapsed
            };
            lastResult = result;

            if (result.Success || cancellationToken.IsCancellationRequested)
                return result;

            if (!ShouldFallback(result.FailureKind) || providerIndex == _providers.Count - 1)
                return result;

            var nextProviderIndex = FindNextProviderIndex(providerIndex, request, result.FailureKind);
            if (nextProviderIndex < 0)
                return result;

            var next = _providers[nextProviderIndex];
            _logger.LogWarning(
                "Zalo AI workload {Workload} failing over from {Provider}/{Model} to {NextProvider}/{NextModel} after {FailureKind}",
                request.Workload,
                result.Provider,
                result.Model,
                next.Name,
                next.ResolveModel(request),
                result.FailureKind);
            providerIndex = nextProviderIndex - 1;
        }

        return lastResult ?? ZaloAiCompletionResult.NotConfigured(request.Workload);
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
            timeoutCts.CancelAfter(ResolveTimeout(request.Workload));

            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["temperature"] = request.Temperature,
                    ["max_tokens"] = request.MaxTokens,
                    ["messages"] = request.Messages.Select(item => new
                    {
                        role = item.Role,
                        content = item.Content
                    }).ToArray()
                };
                if (RequiresJsonObject(request.Workload))
                    payload["response_format"] = new { type = "json_object" };
                if (ShouldDisableReasoning(model, request.Workload))
                    payload["reasoning"] = new { enabled = false };

                using var message = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint)
                {
                    Content = JsonContent.Create(payload)
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

    private static IReadOnlyList<ZaloAiProviderProfile> LoadProfiles(IConfiguration configuration)
    {
        var configuredPool = LoadConfiguredProviderPool(configuration);
        if (configuredPool.Count > 0)
            return configuredPool;

        var legacyProfiles = new List<ZaloAiProviderProfile>(2);
        var primary = LoadProfile(configuration, "Ai", configuration["Ai:Provider"] ?? "primary");
        if (primary is not null)
            legacyProfiles.Add(primary);

        var fallback = LoadFallbackProfile(configuration);
        if (fallback is not null)
            legacyProfiles.Add(fallback);

        return legacyProfiles;
    }

    private static IReadOnlyList<ZaloAiProviderProfile> LoadConfiguredProviderPool(IConfiguration configuration)
    {
        var sections = configuration.GetSection("Ai:Providers").GetChildren().ToArray();
        if (sections.Length == 0)
            return [];

        var inheritedEndpoint = configuration["Ai:Endpoint"];
        var inheritedApiKey = configuration["Ai:ApiKey"];
        var inheritedModel = configuration["Ai:Model"];
        var inheritedModels = LoadWorkloadModels(configuration, "Ai:Models");
        var profiles = new List<ZaloAiProviderProfile>(sections.Length);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            var endpoint = FirstNonEmpty(section["Endpoint"], inheritedEndpoint);
            var apiKey = FirstNonEmpty(section["ApiKey"], inheritedApiKey);
            var model = FirstNonEmpty(section["Model"], inheritedModel);
            if (endpoint is null || apiKey is null || model is null)
                continue;

            var workloadModels = new Dictionary<ZaloAiWorkload, string>(inheritedModels);
            foreach (var (workload, workloadModel) in LoadWorkloadModels(configuration, $"{section.Path}:Models"))
                workloadModels[workload] = workloadModel;

            var profile = new ZaloAiProviderProfile(
                FirstNonEmpty(section["Provider"], section["Name"]) ?? $"provider-{index + 1}",
                endpoint,
                apiKey,
                model,
                workloadModels);

            // When only the legacy Render Ai__ApiKey is configured, repeated model slots would
            // otherwise call the exact same endpoint/key/model twice. Separate per-provider keys
            // make those slots distinct automatically.
            var fingerprint = BuildProfileFingerprint(profile);
            if (fingerprints.Add(fingerprint))
                profiles.Add(profile);
        }

        return profiles;
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

        return new ZaloAiProviderProfile(
            configuration[$"{prefix}:Provider"] ?? defaultName,
            endpoint.Trim(),
            apiKey.Trim(),
            model.Trim(),
            LoadWorkloadModels(configuration, $"{prefix}:Models"));
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

    private static Dictionary<ZaloAiWorkload, string> LoadWorkloadModels(
        IConfiguration configuration,
        string prefix) =>
        Enum.GetValues<ZaloAiWorkload>()
            .Select(workload => new
            {
                Workload = workload,
                Model = configuration[$"{prefix}:{workload}"]
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Model))
            .ToDictionary(item => item.Workload, item => item.Model!.Trim());

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildProfileFingerprint(ZaloAiProviderProfile profile)
    {
        var workloadModels = string.Join(
            "|",
            Enum.GetValues<ZaloAiWorkload>().Select(workload =>
                profile.WorkloadModels.TryGetValue(workload, out var model)
                    ? $"{workload}={model}"
                    : $"{workload}="));
        return $"{profile.Endpoint}\u001f{profile.ApiKey}\u001f{profile.DefaultModel}\u001f{workloadModels}";
    }

    private static IReadOnlyDictionary<ZaloAiWorkload, TimeSpan> LoadWorkloadTimeouts(
        IConfiguration configuration,
        TimeSpan defaultTimeout)
    {
        var defaults = new Dictionary<ZaloAiWorkload, int>
        {
            [ZaloAiWorkload.IntentClassification] = 8,
            [ZaloAiWorkload.StructuredExtraction] = 20,
            [ZaloAiWorkload.DomainNarration] = 15
        };
        return Enum.GetValues<ZaloAiWorkload>().ToDictionary(
            workload => workload,
            workload =>
            {
                var fallbackSeconds = defaults.TryGetValue(workload, out var configuredDefault)
                    ? configuredDefault
                    : (int)Math.Ceiling(defaultTimeout.TotalSeconds);
                var seconds = configuration.GetValue<int?>($"Ai:TimeoutSecondsByWorkload:{workload}") ?? fallbackSeconds;
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 60));
            });
    }

    private TimeSpan ResolveTimeout(ZaloAiWorkload workload) =>
        _workloadTimeouts.TryGetValue(workload, out var timeout) ? timeout : _defaultTimeout;

    private int FindNextProviderIndex(
        int currentIndex,
        ZaloAiCompletionRequest request,
        ZaloAiFailureKind failureKind)
    {
        var current = _providers[currentIndex];
        var currentModel = current.ResolveModel(request);
        for (var index = currentIndex + 1; index < _providers.Count; index++)
        {
            var candidate = _providers[index];
            if (ShouldSkipEquivalentModelFailure(failureKind) &&
                candidate.Endpoint.Equals(current.Endpoint, StringComparison.OrdinalIgnoreCase) &&
                candidate.ResolveModel(request).Equals(currentModel, StringComparison.OrdinalIgnoreCase))
                continue;
            return index;
        }
        return -1;
    }

    private static bool ShouldSkipEquivalentModelFailure(ZaloAiFailureKind failureKind) => failureKind is
        ZaloAiFailureKind.Timeout or
        ZaloAiFailureKind.InvalidResponse or
        ZaloAiFailureKind.ModelOrEndpointUnavailable or
        ZaloAiFailureKind.RateLimited;

    private static bool RequiresJsonObject(ZaloAiWorkload workload) => workload is
        ZaloAiWorkload.IntentClassification or
        ZaloAiWorkload.StructuredExtraction or
        ZaloAiWorkload.DomainNarration;

    private static bool ShouldDisableReasoning(string model, ZaloAiWorkload workload) =>
        model.Contains("xing4.0", StringComparison.OrdinalIgnoreCase) &&
        workload is ZaloAiWorkload.StructuredExtraction or ZaloAiWorkload.DomainNarration;

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
                return (ReadTextContent(content), finishReason);

            if (first.TryGetProperty("text", out var textNode))
                return (ReadTextContent(textNode), finishReason);
        }

        if (root.TryGetProperty("output_text", out var outputText))
            return (outputText.GetString(), null);

        return (null, null);
    }

    private static string? ReadTextContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
            }
        }
        return parts.Count == 0 ? null : string.Join("", parts);
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
