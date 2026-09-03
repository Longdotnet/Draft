namespace VolleyDraft.Api.Services.Zalo.AI;

public enum ZaloAiWorkload
{
    IntentClassification,
    StructuredExtraction,
    GeneralChat,
    SocialReply,
    SafeRewrite,
    DomainNarration
}

public enum ZaloAiFailureKind
{
    None,
    NotConfigured,
    Unauthorized,
    RateLimited,
    Timeout,
    TransientProvider,
    ProviderError,
    InvalidResponse,
    Cancelled
}

public sealed record ZaloAiChatMessage(string Role, string Content);

public sealed record ZaloAiCompletionRequest(
    ZaloAiWorkload Workload,
    IReadOnlyList<ZaloAiChatMessage> Messages,
    double Temperature = 0.2,
    int MaxTokens = 300,
    string? ModelOverride = null,
    string? CorrelationId = null);

public sealed record ZaloAiCompletionResult(
    bool Success,
    string? Content,
    ZaloAiFailureKind FailureKind,
    string Provider,
    string Model,
    int Attempts,
    int? StatusCode,
    TimeSpan Duration,
    bool UsedFallback,
    string? FinishReason = null)
{
    public static ZaloAiCompletionResult NotConfigured(ZaloAiWorkload workload) =>
        new(false, null, ZaloAiFailureKind.NotConfigured, "none", workload.ToString(), 0, null, TimeSpan.Zero, false);
}

public interface IZaloAiGateway
{
    bool IsConfigured { get; }

    Task<ZaloAiCompletionResult> CompleteAsync(
        ZaloAiCompletionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ZaloAiProviderProfile(
    string Name,
    string Endpoint,
    string ApiKey,
    string DefaultModel,
    IReadOnlyDictionary<ZaloAiWorkload, string> WorkloadModels)
{
    public string ResolveModel(ZaloAiCompletionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ModelOverride))
            return request.ModelOverride.Trim();

        return WorkloadModels.TryGetValue(request.Workload, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : DefaultModel;
    }
}
