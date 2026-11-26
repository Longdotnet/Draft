using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VolleyDraft.Api.Services.Zalo.AI;

// Opt-in, single-user text pilot. Only a grant obtained for this approved integration
// may be provisioned; the browser, COS tunnel and unrelated OAuth clients are not token sources.
public sealed class ChatGptPlanTextGateway(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger logger,
    IZaloAiGateway fallback) : IZaloAiGateway
{
    private const string Provider = "ChatGPTPlan";
    private const string Prefix = "Ai:ChatGptPlan:";
    private static readonly ConcurrentDictionary<ZaloAiCallerScope, DateTimeOffset> PausedUntil = new();
    private const int MaxReplyChars = 65536;

    public bool IsConfigured => fallback.IsConfigured || ReadGrant() is not null;

    public async Task<ZaloAiCompletionResult> CompleteAsync(
        ZaloAiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var grant = request.Workload == ZaloAiWorkload.GeneralChat ? ReadGrant() : null;
        if (grant is null || request.Caller != grant.Caller ||
            (PausedUntil.TryGetValue(grant.Caller, out var until) && until > DateTimeOffset.UtcNow))
            return await fallback.CompleteAsync(request, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
            return Failure(grant.Model, ZaloAiFailureKind.Cancelled, 0, TimeSpan.Zero);
        if (!ZaloAiRequestBudget.TryConsume(request))
            return Failure(grant.Model, ZaloAiFailureKind.RateLimited, 0, TimeSpan.Zero,
                code: "local_request_budget_exhausted");

        var primary = await RequestAsync(grant, request, cancellationToken);
        if (primary.Success || cancellationToken.IsCancellationRequested) return primary;

        logger.LogWarning("ChatGPT plan text request failed: {Kind}, HTTP {Status}, code {Code}",
            primary.FailureKind, primary.StatusCode, primary.ProviderCode);
        if (primary.FailureKind == ZaloAiFailureKind.QuotaExceeded)
        {
            // Local probe delay, not an assertion about when the user's plan resets.
            var pause = Math.Clamp(configuration.GetValue(Prefix + "QuotaProbeSeconds", 300), 60, 3600);
            PausedUntil[grant.Caller] = DateTimeOffset.UtcNow.AddSeconds(pause);
        }

        // An admission/permission failure, a refusal, or an invalid request is never a reason
        // to try another model. Only quota and transient infrastructure failures may fall back.
        if (!fallback.IsConfigured || primary.FailureKind is not (
            ZaloAiFailureKind.QuotaExceeded or ZaloAiFailureKind.RateLimited or
            ZaloAiFailureKind.Timeout or ZaloAiFailureKind.NetworkFailure or ZaloAiFailureKind.ProviderUnavailable))
            return primary;

        var secondary = await fallback.CompleteAsync(request, cancellationToken);
        return secondary with
        {
            UsedFallback = true,
            Attempts = primary.Attempts + secondary.Attempts,
            Duration = primary.Duration + secondary.Duration
        };
    }

    private Grant? ReadGrant()
    {
        if (!configuration.GetValue(Prefix + "Enabled", false) ||
            !configuration.GetValue(Prefix + "HostedAccessApproved", false)) return null;

        var connectionId = configuration[Prefix + "ConnectionId"];
        var groupId = configuration[Prefix + "GroupId"];
        var senderId = configuration[Prefix + "SenderId"];
        var token = configuration[Prefix + "AccessToken"];
        var model = configuration[Prefix + "Model"];
        var scopes = (configuration[Prefix + "GrantedScopes"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(groupId) ||
            string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(model) || !scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal) ||
            !scopes.Contains("resource.invoke", StringComparer.Ordinal) ||
            !DateTimeOffset.TryParse(configuration[Prefix + "AccessTokenExpiresAtUtc"], out var expiresAt) ||
            expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)) return null;

        return new Grant(new ZaloAiCallerScope(connectionId, groupId, senderId), token, model);
    }

    private async Task<ZaloAiCompletionResult> RequestAsync(
        Grant grant, ZaloAiCompletionRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue(Prefix + "TimeoutSeconds", 30), 5, 60)));
        var refusal = false;
        try
        {
            // SIWC Responses requires streaming + store=false and does not support
            // temperature, max_output_tokens or a system-role input message.
            var payload = new
            {
                model = grant.Model,
                input = request.Messages.Select(m => new { role = m.Role == "system" ? "developer" : m.Role, content = m.Content }).ToArray(),
                store = false,
                stream = true
            };
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
            {
                Content = JsonContent.Create(payload)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                return FromProviderError(grant.Model, (int)response.StatusCode,
                    AiProviderFailure.ExtractProviderCode(body), started.Elapsed);
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var text = new StringBuilder();
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length != 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                    if (data.Length > 2_000_000)
                        return Failure(grant.Model, ZaloAiFailureKind.InvalidResponse, 1, started.Elapsed);
                    continue;
                }
                if (line.Length != 0 || data.Length == 0) continue;
                var json = data.ToString();
                data.Clear();
                if (json == "[DONE]") break;
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                switch (ReadString(root, "type"))
                {
                    case "response.refusal.delta":
                        if (!refusal) text.Clear();
                        refusal = true;
                        text.Append(ReadString(root, "delta"));
                        break;
                    case "response.output_text.delta":
                        if (!refusal) text.Append(ReadString(root, "delta"));
                        break;
                    case "response.completed":
                        if (string.IsNullOrWhiteSpace(text.ToString()))
                            return Failure(grant.Model, ZaloAiFailureKind.InvalidResponse, 1, started.Elapsed);
                        return new ZaloAiCompletionResult(true, text.ToString().Trim(), ZaloAiFailureKind.None,
                            Provider, grant.Model, 1, 200, started.Elapsed, false, FinishReason: "stop");
                    case "response.failed":
                    case "error":
                        var error = root.TryGetProperty("response", out var failed) && failed.TryGetProperty("error", out var nested)
                            ? nested : root;
                        return refusal
                            ? Failure(grant.Model, ZaloAiFailureKind.InvalidRequest, 1, started.Elapsed, code: "refusal")
                            : FromProviderError(grant.Model, null, ReadString(error, "code"), started.Elapsed);
                    case "response.incomplete":
                        return Failure(grant.Model, ZaloAiFailureKind.InvalidResponse, 1, started.Elapsed, code: "incomplete_response");
                }
                if (text.Length > MaxReplyChars)
                    return Failure(grant.Model, ZaloAiFailureKind.InvalidResponse, 1, started.Elapsed, code: "reply_too_large");
            }
            // Never publish deltas from an interrupted or incomplete stream.
            return Failure(grant.Model, ZaloAiFailureKind.InvalidResponse, 1, started.Elapsed, code: "missing_completed_event");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            var kind = refusal ? ZaloAiFailureKind.InvalidRequest : exception switch
            {
                OperationCanceledException => cancellationToken.IsCancellationRequested ? ZaloAiFailureKind.Cancelled : ZaloAiFailureKind.Timeout,
                HttpRequestException or IOException => ZaloAiFailureKind.NetworkFailure,
                _ => ZaloAiFailureKind.InvalidResponse
            };
            return Failure(grant.Model, kind, 1, started.Elapsed);
        }
    }

    private static string? ReadString(JsonElement node, string property) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ZaloAiCompletionResult FromProviderError(string model, int? status, string? code, TimeSpan duration)
    {
        var kind = code switch
        {
            "subscription_sharing_usage_limit_exceeded" => ZaloAiFailureKind.QuotaExceeded,
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => ZaloAiFailureKind.ProviderUnavailable,
            "rate_limit_exceeded" => ZaloAiFailureKind.RateLimited,
            "server_error" => ZaloAiFailureKind.ProviderUnavailable,
            "subscription_sharing_user_not_eligible" or "subscription_sharing_route_not_supported" or
            "subscription_sharing_invalid_user" or "chatpass_v2_scope_not_authorized" or
            "chatpass_v2_invalid_authorization_context" => ZaloAiFailureKind.AuthenticationFailed,
            "subscription_sharing_unsupported_capability" => ZaloAiFailureKind.InvalidRequest,
            _ when status is 401 or 403 => ZaloAiFailureKind.AuthenticationFailed,
            _ when code is null && status is 500 or 502 or 503 or 504 => ZaloAiFailureKind.ProviderUnavailable,
            _ => ZaloAiFailureKind.InvalidRequest
        };
        // Permission checks take precedence even if a server reports a contradictory code.
        if (status is 401 or 403) kind = ZaloAiFailureKind.AuthenticationFailed;
        var safeCode = code is null ? null : new string(code.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').Take(80).ToArray());
        return Failure(model, kind, 1, duration, status, safeCode);
    }

    private static ZaloAiCompletionResult Failure(string model, ZaloAiFailureKind kind, int attempts,
        TimeSpan duration, int? status = null, string? code = null) =>
        new(false, null, kind, Provider, model, attempts, status, duration, false, ProviderCode: code);

    private sealed record Grant(ZaloAiCallerScope Caller, string AccessToken, string Model);
}
