using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VolleyDraft.Api.Services.Zalo.AI;

/// <summary>
/// Provider-neutral boundary for OpenAI-compatible chat APIs.
/// Domain features choose a workload, not an endpoint/model. Provider/model selection,
/// timeout, retry and ordered failover live here so model changes do not touch feature code.
/// </summary>
public sealed class OpenAiCompatibleZaloAiGateway : IZaloAiGateway
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProviderCooldowns = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> PoolCursors = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleZaloAiGateway> _logger;
    private readonly IReadOnlyList<ZaloAiProviderProfile> _providers;
    private readonly int _retryCount;
    private readonly TimeSpan _defaultTimeout;
    private readonly IReadOnlyDictionary<ZaloAiWorkload, TimeSpan> _workloadTimeouts;
    private readonly TimeSpan _rateLimitCooldown;
    private readonly TimeSpan _quotaCooldown;
    private readonly int _maxRateLimitProviderAttempts;
    private readonly string _healthScope;
    private readonly string _poolKey;

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
        _rateLimitCooldown = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Ai:RateLimitCooldownSeconds", 60), 5, 900));
        _quotaCooldown = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Ai:QuotaCooldownSeconds", 900), 60, 86400));
        _maxRateLimitProviderAttempts = Math.Clamp(configuration.GetValue("Ai:MaxRateLimitProviderAttempts", 2), 1, 2);
        // IConfiguration is a singleton in production, while tests normally build a fresh
        // root per case. Scoping health this way preserves process-wide production cooldown
        // without leaking one test's simulated 429 into unrelated gateway tests.
        _healthScope = configuration["Ai:ProviderHealthScope"] ??
                       RuntimeHelpers.GetHashCode(configuration).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _poolKey = BuildPoolKey(_healthScope, _providers);
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
        var rateLimitProviderAttempts = 0;
        ZaloAiCompletionResult? lastResult = null;
        var attemptedProviderIndexes = new HashSet<int>();
        var providerIndex = FindRoundRobinProviderIndex(request, attemptedProviderIndexes);

        if (providerIndex < 0)
            return BuildCoolingDownResult(request, started.Elapsed);

        while (providerIndex >= 0)
        {
            attemptedProviderIndexes.Add(providerIndex);
            var profile = _providers[providerIndex];
            var result = await ExecuteProfileAsync(
                profile,
                request,
                usedFallback: attemptedProviderIndexes.Count > 1,
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

            // The entire inbound turn has already consumed its external-call budget.
            // Do not walk more provider slots locally and emit misleading failover logs.
            if (string.Equals(result.ProviderCode, "local_request_budget_exhausted", StringComparison.Ordinal))
                return result;

            if (!ShouldFallback(result.FailureKind))
                return result;

            if (result.FailureKind is ZaloAiFailureKind.RateLimited or ZaloAiFailureKind.QuotaExceeded)
            {
                rateLimitProviderAttempts += 1;
                if (rateLimitProviderAttempts >= _maxRateLimitProviderAttempts)
                    return result;
            }

            var nextProviderIndex = FindNextProviderIndex(
                providerIndex,
                request,
                result.FailureKind,
                attemptedProviderIndexes);
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
            providerIndex = nextProviderIndex;
        }

        return lastResult ?? BuildCoolingDownResult(request, started.Elapsed);
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

            if (!ZaloAiRequestBudget.TryConsume())
            {
                var budgetFailure = new AiProviderFailure(
                    AiProviderFailureKind.RateLimited,
                    429,
                    "local_request_budget_exhausted");
                LogFailure(request.Workload, profile, model, 0, budgetFailure);
                return Failure(budgetFailure, profile, model, 0, usedFallback);
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
                    if (GetEquivalentPrimaryIndexes(request).Length > 1)
                    {
                        if (providerFailure.Kind == AiProviderFailureKind.RateLimited)
                            MarkProviderCooldown(profile, ResolveRateLimitCooldown(response));
                        else if (providerFailure.Kind == AiProviderFailureKind.QuotaExceeded)
                            MarkProviderCooldown(profile, _quotaCooldown);
                    }

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

                if (GetEquivalentPrimaryIndexes(request).Length > 1)
                    ClearProviderCooldown(profile);
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

    private int FindRoundRobinProviderIndex(
        ZaloAiCompletionRequest request,
        ISet<int> attemptedProviderIndexes)
    {
        if (_providers.Count == 0) return -1;

        var rotationIndexes = GetEquivalentPrimaryIndexes(request);

        // A normal primary + heterogeneous fallback pool keeps the historical contract:
        // every fresh completion starts at the primary. Cooldown/rotation is only a
        // credential-pool policy when 2+ accounts target the same endpoint + model.
        if (rotationIndexes.Length <= 1)
            return attemptedProviderIndexes.Contains(0) ? -1 : 0;

        var primaryModel = _providers[0].ResolveModel(request);
        var cursorKey = $"{_poolKey}\u001f{request.Workload}\u001f{primaryModel}";
        var ticket = PoolCursors.AddOrUpdate(
            cursorKey,
            _ => 0,
            (_, current) => current == long.MaxValue ? 0 : current + 1);
        var rotationStart = (int)(ticket % rotationIndexes.Length);

        for (var offset = 0; offset < rotationIndexes.Length; offset++)
        {
            var index = rotationIndexes[(rotationStart + offset) % rotationIndexes.Length];
            if (attemptedProviderIndexes.Contains(index)) continue;
            if (IsProviderCoolingDown(_providers[index])) continue;
            return index;
        }

        // Every equivalent account is cooling down. A genuinely different configured
        // fallback may still be used, preserving its original provider/model order.
        for (var index = 0; index < _providers.Count; index++)
        {
            if (rotationIndexes.Contains(index)) continue;
            if (attemptedProviderIndexes.Contains(index)) continue;
            return index;
        }

        return -1;
    }

    private int FindNextProviderIndex(
        int currentIndex,
        ZaloAiCompletionRequest request,
        ZaloAiFailureKind failureKind,
        ISet<int> attemptedProviderIndexes)
    {
        var rotationIndexes = GetEquivalentPrimaryIndexes(request);
        var usesCredentialRotation = rotationIndexes.Length > 1;
        var current = _providers[currentIndex];
        var currentModel = current.ResolveModel(request);

        if (usesCredentialRotation && rotationIndexes.Contains(currentIndex))
        {
            // Equivalent account slots are a ring: if slot 5 was selected first,
            // slot 1 is still a legitimate one-shot alternate unless cooling down.
            for (var offset = 1; offset < rotationIndexes.Length; offset++)
            {
                var position = Array.IndexOf(rotationIndexes, currentIndex);
                var index = rotationIndexes[(position + offset) % rotationIndexes.Length];
                if (attemptedProviderIndexes.Contains(index)) continue;
                if (IsProviderCoolingDown(_providers[index])) continue;
                return index;
            }

            // Only after the account ring is unavailable do we walk truly different
            // fallbacks, in their configured forward order.
            for (var index = 0; index < _providers.Count; index++)
            {
                if (rotationIndexes.Contains(index)) continue;
                if (attemptedProviderIndexes.Contains(index)) continue;
                return index;
            }

            return -1;
        }

        // Heterogeneous primary/fallback pools retain the pre-rotation ordering and do
        // not inherit historical cooldown from another completion.
        for (var index = currentIndex + 1; index < _providers.Count; index++)
        {
            if (attemptedProviderIndexes.Contains(index)) continue;

            var candidate = _providers[index];
            if (ShouldSkipEquivalentModelFailure(failureKind, current, candidate) &&
                candidate.Endpoint.Equals(current.Endpoint, StringComparison.OrdinalIgnoreCase) &&
                candidate.ResolveModel(request).Equals(currentModel, StringComparison.OrdinalIgnoreCase))
                continue;
            return index;
        }

        return -1;
    }

    private int[] GetEquivalentPrimaryIndexes(ZaloAiCompletionRequest request)
    {
        var primary = _providers[0];
        var primaryModel = primary.ResolveModel(request);
        return Enumerable.Range(0, _providers.Count)
            .Where(index =>
                _providers[index].Endpoint.Equals(primary.Endpoint, StringComparison.OrdinalIgnoreCase) &&
                _providers[index].ResolveModel(request).Equals(primaryModel, StringComparison.OrdinalIgnoreCase))
            .GroupBy(index => _providers[index].ApiKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private bool IsProviderCoolingDown(ZaloAiProviderProfile profile)
    {
        var key = BuildProviderHealthKey(_healthScope, profile);
        if (!ProviderCooldowns.TryGetValue(key, out var until))
            return false;

        if (until > DateTimeOffset.UtcNow)
            return true;

        ProviderCooldowns.TryRemove(key, out _);
        return false;
    }

    private void MarkProviderCooldown(ZaloAiProviderProfile profile, TimeSpan cooldown)
    {
        var safeCooldown = cooldown <= TimeSpan.Zero ? _rateLimitCooldown : cooldown;
        var until = DateTimeOffset.UtcNow.Add(safeCooldown);
        ProviderCooldowns[BuildProviderHealthKey(_healthScope, profile)] = until;
        _logger.LogWarning(
            "Zalo AI provider cooldown. Provider={Provider} CooldownSeconds={CooldownSeconds}",
            profile.Name,
            Math.Ceiling(safeCooldown.TotalSeconds));
    }

    private void ClearProviderCooldown(ZaloAiProviderProfile profile) =>
        ProviderCooldowns.TryRemove(BuildProviderHealthKey(_healthScope, profile), out _);

    private TimeSpan ResolveRateLimitCooldown(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return ClampCooldown(delta);

        if (retryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
                return ClampCooldown(until);
        }

        return _rateLimitCooldown;
    }

    private static TimeSpan ClampCooldown(TimeSpan value) =>
        TimeSpan.FromSeconds(Math.Clamp((int)Math.Ceiling(value.TotalSeconds), 5, 900));

    private ZaloAiCompletionResult BuildCoolingDownResult(
        ZaloAiCompletionRequest request,
        TimeSpan duration)
    {
        var profile = _providers[0];
        return new ZaloAiCompletionResult(
            false,
            null,
            ZaloAiFailureKind.RateLimited,
            "provider-pool-cooldown",
            profile.ResolveModel(request),
            0,
            429,
            duration,
            false,
            ProviderCode: "provider_pool_cooling_down",
            Retryable: true);
    }

    private static string BuildProviderHealthKey(string healthScope, ZaloAiProviderProfile profile)
    {
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ApiKey)));
        return $"{healthScope}\u001f{profile.Endpoint}\u001f{keyHash}";
    }

    private static string BuildPoolKey(
        string healthScope,
        IReadOnlyList<ZaloAiProviderProfile> providers) =>
        string.Join(
            "\u001e",
            providers.Select(profile =>
                $"{profile.Name}\u001f{profile.Endpoint}\u001f{profile.DefaultModel}\u001f{BuildProviderHealthKey(healthScope, profile)}"));

    internal static void ResetProviderHealthForTests()
    {
        ProviderCooldowns.Clear();
        PoolCursors.Clear();
    }

    private static bool ShouldSkipEquivalentModelFailure(
        ZaloAiFailureKind failureKind,
        ZaloAiProviderProfile current,
        ZaloAiProviderProfile candidate) =>
        failureKind is
            ZaloAiFailureKind.Timeout or
            ZaloAiFailureKind.InvalidResponse or
            ZaloAiFailureKind.ModelOrEndpointUnavailable ||
        // A 429 can be credential-scoped, so a different API key is a real failover slot.
        failureKind == ZaloAiFailureKind.RateLimited &&
        string.Equals(candidate.ApiKey, current.ApiKey, StringComparison.Ordinal);

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
