using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBridgeClient
{
    private readonly HttpClient httpClient;
    private readonly IServiceScopeFactory? scopeFactory;
    private readonly ILogger<ZaloBridgeClient> logger;

    // Keep exactly one public constructor. AddHttpClient<TClient> creates typed clients
    // through ActivatorUtilities with HttpClient as an explicit argument; two public
    // constructors that both accept HttpClient can become ambiguous at activation time.
    // Optional dependencies preserve the lightweight new ZaloBridgeClient(httpClient)
    // construction used by unit tests while production DI still supplies both services.
    public ZaloBridgeClient(
        HttpClient httpClient,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<ZaloBridgeClient>? logger = null)
    {
        this.httpClient = httpClient;
        this.scopeFactory = scopeFactory;
        this.logger = logger ?? NullLogger<ZaloBridgeClient>.Instance;
    }

    public async Task<BridgeStartQrResponse> StartQrLoginAsync()
    {
        using var response = await httpClient.PostAsJsonAsync("v1/qr-logins", new { });
        return await ReadAsync<BridgeStartQrResponse>(response);
    }

    public async Task<BridgeQrStatusResponse> GetQrLoginAsync(string loginId)
    {
        using var response = await httpClient.GetAsync($"v1/qr-logins/{Uri.EscapeDataString(loginId)}");
        return await ReadAsync<BridgeQrStatusResponse>(response);
    }

    public async Task<IReadOnlyList<BridgeGroup>> GetGroupsAsync(JsonElement credentials)
    {
        using var response = await httpClient.PostAsJsonAsync("v1/groups", new { credentials });
        return (await ReadAsync<BridgeGroupsResponse>(response)).Groups;
    }

    public async Task<IReadOnlyList<BridgePoll>> GetPollsAsync(JsonElement credentials, string groupId)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"v1/groups/{Uri.EscapeDataString(groupId)}/polls",
            new { credentials });
        return (await ReadAsync<BridgePollsResponse>(response)).Polls;
    }

    public async Task<BridgeGroupMemberDirectory> GetGroupMemberDirectoryAsync(
        JsonElement credentials,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"v1/groups/{Uri.EscapeDataString(groupId)}/members",
            new { credentials },
            cancellationToken);
        return await ReadAsync<BridgeGroupMemberDirectory>(response);
    }

    public async Task<BridgeBoardPage> GetBoardPageAsync(
        JsonElement credentials,
        string groupId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"v1/groups/{Uri.EscapeDataString(groupId)}/board-pages",
            new { credentials, page, pageSize },
            cancellationToken);
        return await ReadAsync<BridgeBoardPage>(response);
    }

    public async Task<BridgeMessageHistoryProbe> GetGroupMessageHistoryAsync(
        JsonElement credentials,
        string groupId,
        int count,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"v1/groups/{Uri.EscapeDataString(groupId)}/message-history",
            new { credentials, count },
            cancellationToken);
        return await ReadAsync<BridgeMessageHistoryProbe>(response);
    }

    public async Task<BridgeGroupRoles> GetGroupRolesAsync(JsonElement credentials, string groupId)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"v1/groups/{Uri.EscapeDataString(groupId)}/roles",
            new { credentials });
        return await ReadAsync<BridgeGroupRoles>(response);
    }

    public async Task<BridgePoll> GetPollAsync(JsonElement credentials, string pollId)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"v1/polls/{Uri.EscapeDataString(pollId)}",
            new { credentials });
        return await ReadAsync<BridgePoll>(response);
    }

    public async Task<IReadOnlyList<BridgeMember>> GetMembersAsync(
        JsonElement credentials,
        IReadOnlyList<string> memberIds)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "v1/group-members",
            new { credentials, memberIds });
        return (await ReadAsync<BridgeMembersResponse>(response)).Members;
    }

    public async Task<BridgeListenerResponse> StartListenerAsync(
        string accountId,
        JsonElement credentials,
        IReadOnlyList<string> groupIds,
        string webhookUrl,
        string webhookKey)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"v1/listeners/{Uri.EscapeDataString(accountId)}",
            new { credentials, groupIds, webhookUrl, webhookKey });
        return await ReadAsync<BridgeListenerResponse>(response);
    }

    public async Task StopListenerAsync(string accountId)
    {
        using var response = await httpClient.DeleteAsync($"v1/listeners/{Uri.EscapeDataString(accountId)}");
        await ReadAsync<BridgeStopListenerResponse>(response);
    }

    public async Task<BridgeSendMessageResponse> SendGroupMessageAsync(
        string accountId,
        string groupId,
        string message,
        IReadOnlyList<BridgeOutgoingMention> mentions,
        string? imageUrl = null,
        string? idempotencyKey = null)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "v1/group-messages",
            new { accountId, groupId, message, mentions, imageUrl, idempotencyKey });
        var result = await ReadAsync<BridgeSendMessageResponse>(response);

        // Once the provider has accepted the message, observability/persistence must
        // never turn the operation into a retryable send failure. Record the real
        // provider ID best-effort in an independent scope and only log if that fails.
        if (result.Sent && !result.Mock && !string.IsNullOrWhiteSpace(result.MessageId))
        {
            await TryRememberProviderOutboundIdAsync(
                accountId,
                groupId,
                result.MessageId!,
                message,
                ParseParentMessageId(accountId, idempotencyKey));
        }

        // Expressive stickers are intentionally best-effort and happen only after the
        // text send has succeeded. A sticker lookup/provider failure must never turn a
        // successful conversational reply into a retryable send failure.
        if (result.Sent && !result.Mock)
        {
            await TrySendExpressiveStickerAsync(
                accountId,
                groupId,
                message,
                imageUrl,
                idempotencyKey);
        }
        return result;
    }

    private async Task TryRememberProviderOutboundIdAsync(
        string accountId,
        string groupId,
        string providerMessageId,
        string message,
        string? parentMessageId)
    {
        if (scopeFactory is null) return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
            var connectionId = await db.ZaloConnections
                .AsNoTracking()
                .Where(item => item.AccountZaloId == accountId &&
                               item.MatchSessions.Any(session => session.ZaloGroupId == groupId))
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => item.Id)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(connectionId)) return;

            await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                connectionId,
                groupId,
                providerMessageId,
                parentMessageId);
            await new ZaloOutboundReceiptStore(db).RememberAsync(
                connectionId,
                groupId,
                providerMessageId,
                parentMessageId,
                message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Zalo provider message was sent but V2 provider-ID persistence failed Account={AccountId} Group={GroupId} MessageId={MessageId}",
                accountId,
                groupId,
                providerMessageId);
        }
    }

    private async Task TrySendExpressiveStickerAsync(
        string accountId,
        string groupId,
        string message,
        string? imageUrl,
        string? idempotencyKey)
    {
        if (scopeFactory is null) return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            if (!ZaloStickerPolicy.TryPlan(
                    accountId,
                    groupId,
                    message,
                    imageUrl,
                    idempotencyKey,
                    configuration,
                    DateTimeOffset.UtcNow,
                    out var reaction))
                return;

            var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
            var encryptedCredentials = await db.ZaloConnections
                .AsNoTracking()
                .Where(item => item.AccountZaloId == accountId &&
                               item.MatchSessions.Any(session => session.ZaloGroupId == groupId && session.BotEnabled))
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => item.EncryptedCredentials)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(encryptedCredentials)) return;

            var plaintext = new ZaloCredentialProtector(configuration).Unprotect(encryptedCredentials);
            using var credentialsDocument = JsonDocument.Parse(plaintext);
            var credentials = credentialsDocument.RootElement.Clone();
            var stickerIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? null
                : $"{idempotencyKey}:sticker";
            using var stickerResponse = await httpClient.PostAsJsonAsync(
                "v1/group-stickers",
                new
                {
                    accountId,
                    groupId,
                    credentials,
                    reaction = ZaloStickerPolicy.ToWireValue(reaction),
                    idempotencyKey = stickerIdempotencyKey
                });
            var stickerResult = await ReadAsync<BridgeSendStickerResponse>(stickerResponse);
            if (stickerResult.Sent)
            {
                logger.LogInformation(
                    "Zalo expressive sticker sent Account={AccountId} Group={GroupId} Reaction={Reaction}",
                    accountId,
                    groupId,
                    reaction);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Zalo expressive sticker failed after text send Account={AccountId} Group={GroupId}",
                accountId,
                groupId);
        }
    }

    internal static string? ParseParentMessageId(string accountId, string? idempotencyKey)
    {
        var account = (accountId ?? string.Empty).Trim();
        var key = (idempotencyKey ?? string.Empty).Trim();
        if (account.Length == 0 || key.Length == 0) return null;
        var prefix = account + ":";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parent = key[prefix.Length..].Trim();
        return parent.Length == 0 ? null : parent.Length <= 160 ? parent : parent[..160];
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Zalo bridge returned an empty response.");
            }
            catch (JsonException exception)
            {
                throw new HttpRequestException(
                    $"Zalo bridge returned invalid JSON for HTTP {(int)response.StatusCode}.",
                    exception,
                    response.StatusCode);
            }
        }

        BridgeErrorResponse? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<BridgeErrorResponse>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            // Render's proxy can return an HTML error page during cold start.
            // Preserve the HTTP status instead of masking it with a JSON error.
        }

        var detail = string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : $" Response: {body[..Math.Min(body.Length, 240)].Replace("\r", " ").Replace("\n", " ")}";
        throw new HttpRequestException(
            payload?.Error ?? $"Zalo bridge returned HTTP {(int)response.StatusCode}.{detail}",
            null,
            response.StatusCode);
    }
}

public sealed record BridgeStartQrResponse(string Id, string Status, DateTimeOffset ExpiresAt);

public sealed record BridgeQrStatusResponse(
    string Id,
    string Status,
    string? QrImageBase64,
    string? DisplayName,
    string? AvatarUrl,
    string? AccountZaloId,
    JsonElement? Credentials,
    string? Error,
    DateTimeOffset ExpiresAt);

public sealed record BridgeGroupsResponse(IReadOnlyList<BridgeGroup> Groups);
public sealed record BridgeGroup(string Id, string Name, string? AvatarUrl, int TotalMembers);
public sealed record BridgeGroupMemberDirectory(
    string GroupId,
    string GroupName,
    long GroupCreatedAtUnixMs,
    int ExpectedMemberCount,
    bool IsComplete,
    IReadOnlyList<BridgeMember> Members);
public sealed record BridgeGroupRoles(string GroupId, string CreatorId, IReadOnlyList<string> AdminIds);
public sealed record BridgePollsResponse(IReadOnlyList<BridgePoll> Polls);
public sealed record BridgeBoardPage(
    string GroupId,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<BridgeBoardItem> Items);
public sealed record BridgeBoardItem(
    string StableId,
    int BoardType,
    bool IsPoll,
    string? PollId,
    BridgePoll? Poll);
public sealed record BridgePoll(
    string Id,
    string Question,
    string CreatorId,
    IReadOnlyList<BridgePollOption> Options,
    bool AllowMultipleChoices,
    bool IsAnonymous,
    bool IsClosed,
    bool HideVotePreview,
    int UniqueVoteCount,
    long CreatedAtUnixMs,
    long UpdatedAtUnixMs,
    long ExpiredAtUnixMs);
public sealed record BridgePollOption(string Id, string Content, int VoteCount, IReadOnlyList<string> VoterIds);
public sealed record BridgeMembersResponse(IReadOnlyList<BridgeMember> Members);
public sealed record BridgeMember(string ZaloUserId, string DisplayName, string? ZaloName, string? AvatarUrl);
public sealed record BridgeMessageQuote(
    string? MessageId,
    string? SenderId,
    string? SenderName,
    string Content,
    string? MessageType,
    long? SentAtUnixMs,
    string? Attachment);
public sealed record BridgeMessageHistoryProbe(
    string GroupId,
    int RequestedCount,
    int ReturnedCount,
    int More,
    string? LastActionId,
    string? LastActionIdOther,
    long? OldestMessageAtUnixMs,
    long? NewestMessageAtUnixMs,
    IReadOnlyList<BridgeHistoricalMessage> Messages,
    bool IsSupported = true,
    string? LimitationCode = null);
public sealed record BridgeHistoricalMessage(
    string MessageId,
    string SenderId,
    string SenderName,
    string Content,
    string MessageType,
    bool IsFromBot,
    long SentAtUnixMs,
    BridgeMessageQuote? Quote = null);
public sealed record BridgeListenerResponse(string AccountId, string BotId, long StartedAt, int GroupCount);
public sealed record BridgeStopListenerResponse(bool Stopped);
public sealed record BridgeOutgoingMention(string Uid, int Pos, int Len);
public sealed record BridgeSendMessageResponse(bool Sent, bool Mock, string? MessageId = null);
public sealed record BridgeSendStickerResponse(bool Sent, bool Mock, string? MessageId = null);
public sealed record BridgeErrorResponse(string? Error);
