using System.Net.Http.Json;
using System.Text.Json;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBridgeClient(HttpClient httpClient)
{
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

    public async Task SendGroupMessageAsync(
        string accountId,
        string groupId,
        string message,
        IReadOnlyList<BridgeOutgoingMention> mentions,
        string? imageUrl = null)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "v1/group-messages",
            new { accountId, groupId, message, mentions, imageUrl });
        await ReadAsync<BridgeSendMessageResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new InvalidOperationException("Zalo bridge returned an empty response.");
        }

        var payload = await response.Content.ReadFromJsonAsync<BridgeErrorResponse>();
        throw new HttpRequestException(
            payload?.Error ?? $"Zalo bridge returned HTTP {(int)response.StatusCode}.",
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
public sealed record BridgePollsResponse(IReadOnlyList<BridgePoll> Polls);
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
public sealed record BridgeListenerResponse(string AccountId, string BotId, long StartedAt, int GroupCount);
public sealed record BridgeStopListenerResponse(bool Stopped);
public sealed record BridgeOutgoingMention(string Uid, int Pos, int Len);
public sealed record BridgeSendMessageResponse(bool Sent, bool Mock);
public sealed record BridgeErrorResponse(string? Error);
