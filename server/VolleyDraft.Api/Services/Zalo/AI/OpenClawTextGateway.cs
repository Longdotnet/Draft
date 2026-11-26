using System.Collections.Concurrent;

namespace VolleyDraft.Api.Services.Zalo.AI;

// OpenClaw owns the subscription login and model; Draft only calls its official
// Chat Completions endpoint. Never reuse an owner's subscription for other senders.
internal sealed class OpenClawTextGateway : IZaloAiGateway
{
    private const string Prefix = "Ai:OpenClaw:";
    private const string AgentModel = "openclaw/default";
    private static readonly ConcurrentDictionary<(string Endpoint, ZaloAiCallerScope Caller), DateTimeOffset> Cooldowns = new();
    private readonly IZaloAiGateway fallback;
    private readonly IZaloAiGateway? primary;
    private readonly ZaloAiCallerScope? caller;
    private readonly string? endpoint;
    private readonly ILogger logger;
    private readonly int cooldownSeconds;

    public OpenClawTextGateway(HttpClient httpClient, IConfiguration configuration,
        ILogger<OpenAiCompatibleZaloAiGateway> logger, IZaloAiGateway fallback)
    {
        this.fallback = fallback;
        this.logger = logger;
        cooldownSeconds = Math.Clamp(configuration.GetValue(Prefix + "QuotaProbeSeconds", 300), 60, 3600);
        if (!configuration.GetValue(Prefix + "Enabled", false)) return;

        var connectionId = configuration[Prefix + "ConnectionId"];
        var groupId = configuration[Prefix + "GroupId"];
        var senderId = configuration[Prefix + "SenderId"];
        var token = configuration[Prefix + "GatewayToken"];
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(groupId) ||
            string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(token) ||
            !Uri.TryCreate(configuration[Prefix + "Endpoint"], UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.AbsolutePath != "/v1/chat/completions" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return;

        caller = new ZaloAiCallerScope(connectionId, groupId, senderId);
        endpoint = uri.AbsoluteUri;
        // Separate profile: no inherited API keys, model overrides, account rotation or retries.
        // Reuse the same HTTP transport, parser and per-turn budget as all other providers.
        var profile = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "OpenClaw",
            ["Ai:Endpoint"] = endpoint,
            ["Ai:ApiKey"] = token,
            ["Ai:Model"] = AgentModel,
            ["Ai:RetryCount"] = "0",
            ["Ai:TimeoutSeconds"] = Math.Clamp(configuration.GetValue(Prefix + "TimeoutSeconds", 40), 5, 60).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        primary = new OpenAiCompatibleZaloAiGateway(httpClient, profile, logger);
    }

    public bool IsConfigured => fallback.IsConfigured || primary?.IsConfigured == true;

    public async Task<ZaloAiCompletionResult> CompleteAsync(ZaloAiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (primary is null || caller is null || endpoint is null ||
            request.Workload != ZaloAiWorkload.GeneralChat || request.Caller != caller)
            return await fallback.CompleteAsync(request, cancellationToken);

        var key = (endpoint, caller);
        if (Cooldowns.TryGetValue(key, out var until))
        {
            if (until > DateTimeOffset.UtcNow && fallback.IsConfigured)
                return (await fallback.CompleteAsync(request, cancellationToken)) with { UsedFallback = true };
            if (until <= DateTimeOffset.UtcNow) Cooldowns.TryRemove(key, out _);
        }

        var result = await primary.CompleteAsync(request with { ModelOverride = null }, cancellationToken);
        if (result.Success)
        {
            // A partial or tool-call response is not a completed text answer.
            if (result.FinishReason is not ("stop" or "content_filter"))
                return result with { Success = false, Content = null, FailureKind = ZaloAiFailureKind.InvalidResponse };
            return result;
        }

        if (cancellationToken.IsCancellationRequested || !CanFallback(result)) return result;
        if (result.FailureKind is ZaloAiFailureKind.QuotaExceeded or ZaloAiFailureKind.RateLimited)
            Cooldowns[key] = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
        if (!fallback.IsConfigured) return result;

        logger.LogWarning("OpenClaw text gateway falling back after {FailureKind} (HTTP {StatusCode})",
            result.FailureKind, result.StatusCode);
        var next = await fallback.CompleteAsync(request, cancellationToken);
        return next with { UsedFallback = true, Attempts = result.Attempts + next.Attempts,
            Duration = result.Duration + next.Duration };
    }

    private static bool CanFallback(ZaloAiCompletionResult result) =>
        result.FailureKind switch
        {
            ZaloAiFailureKind.QuotaExceeded or ZaloAiFailureKind.RateLimited => result.ProviderCode is
                null or "insufficient_quota" or "quota_exceeded" or "rate_limit_error" or "rate_limit_exceeded" or
                "subscription_sharing_usage_limit_exceeded" or "usage_limit_reached" or "too_many_requests",
            ZaloAiFailureKind.NetworkFailure => true,
            ZaloAiFailureKind.Timeout => result.StatusCode is null or 408 or 504,
            ZaloAiFailureKind.ProviderUnavailable => result.StatusCode is 502 or 503 or 504 &&
                result.ProviderCode is null or "api_error" or "server_error",
            // Permission errors, refusals, malformed responses and opaque 500s fail closed.
            _ => false
        };
}
