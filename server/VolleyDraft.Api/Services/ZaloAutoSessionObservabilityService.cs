using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloAutoSessionObservabilityService(VolleyDraftDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ZaloAutoSessionSettingsStore settingsStore = new(db);
    private readonly ZaloAutoSessionObservabilityStore store = new(db);

    public async Task<ServiceResult<ZaloAutoSessionActivityResponse>> GetActivityAsync(
        string adminUserId,
        string trackedGroupId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var tracked = await settingsStore.GetForAdminAsync(adminUserId, trackedGroupId, cancellationToken);
        if (tracked is null)
            return ServiceResult<ZaloAutoSessionActivityResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy group Auto Session của admin này.");

        var proposals = await store.GetProposalsAsync(adminUserId, trackedGroupId, Math.Clamp(limit, 1, 50), cancellationToken);
        var links = await store.GetLinksAsync(adminUserId, trackedGroupId, cancellationToken);
        var pollIds = proposals.Select(item => item.PollId).ToHashSet(StringComparer.Ordinal);
        links = links.Where(item => pollIds.Contains(item.PollId)).ToList();
        var sessionIds = links.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).ToList();

        var sessionFacts = await LoadSessionFactsAsync(adminUserId, sessionIds, cancellationToken);
        var lastSyncs = await LoadLastSyncsAsync(sessionIds, pollIds.ToList(), cancellationToken);
        var linkMap = links
            .GroupBy(item => Key(item.PollId, item.OptionId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First(), StringComparer.Ordinal);

        var items = proposals.Select(proposal => ToProposal(proposal, linkMap, sessionFacts, lastSyncs)).ToList();
        return ServiceResult<ZaloAutoSessionActivityResponse>.Success(new ZaloAutoSessionActivityResponse(
            tracked.Id,
            tracked.GroupId,
            tracked.GroupName,
            tracked.AutoSessionEnabled,
            items.Count,
            items.Count(item => item.Status == "AwaitingApproval"),
            items.Count(item => item.Status == "Created"),
            items.Count(item => item.Status == "Failed"),
            items));
    }

    private async Task<Dictionary<string, SessionFact>> LoadSessionFactsAsync(
        string adminUserId,
        IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0) return new(StringComparer.Ordinal);
        var rows = await db.MatchSessions
            .AsNoTracking()
            .Where(session => sessionIds.Contains(session.Id) && session.AdminUserId == adminUserId)
            .Select(session => new SessionFact(
                session.Id,
                session.Name,
                session.Status,
                session.TeamCount * session.TeamSize,
                session.Players.Count(player => player.IsPresent)))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, DateTimeOffset>> LoadLastSyncsAsync(
        IReadOnlyList<string> sessionIds,
        IReadOnlyList<string> pollIds,
        CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0 || pollIds.Count == 0) return new(StringComparer.Ordinal);
        var imports = await db.PollImports
            .AsNoTracking()
            .Where(import => sessionIds.Contains(import.SessionId) && pollIds.Contains(import.PollId))
            .Select(import => new { import.SessionId, import.PollId, import.ImportedAt })
            .ToListAsync(cancellationToken);
        return imports
            .GroupBy(item => Key(item.SessionId, item.PollId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.ImportedAt), StringComparer.Ordinal);
    }

    private static ZaloAutoSessionProposalActivityResponse ToProposal(
        ZaloPollSessionProposalData proposal,
        IReadOnlyDictionary<string, ZaloAutoSessionLinkData> linkMap,
        IReadOnlyDictionary<string, SessionFact> sessionFacts,
        IReadOnlyDictionary<string, DateTimeOffset> lastSyncs)
    {
        var candidates = DeserializeCandidates(proposal.CandidatesJson)
            .Select(candidate => ToCandidate(proposal.PollId, candidate, linkMap, sessionFacts, lastSyncs))
            .OrderBy(item => item.StartTime)
            .ToList();
        return new ZaloAutoSessionProposalActivityResponse(
            proposal.Id,
            proposal.PollId,
            proposal.PollQuestion,
            proposal.PollCreatorId,
            proposal.Status.ToString(),
            proposal.ClassifierConfidence,
            proposal.ClassifierReason,
            proposal.ProposalMessageId,
            proposal.ApprovedByZaloUserId,
            proposal.ApprovedAt,
            proposal.LastError,
            proposal.CreatedAt,
            proposal.UpdatedAt,
            candidates);
    }

    private static ZaloAutoSessionCandidateActivityResponse ToCandidate(
        string pollId,
        ZaloAutoSessionCandidate candidate,
        IReadOnlyDictionary<string, ZaloAutoSessionLinkData> linkMap,
        IReadOnlyDictionary<string, SessionFact> sessionFacts,
        IReadOnlyDictionary<string, DateTimeOffset> lastSyncs)
    {
        linkMap.TryGetValue(Key(pollId, candidate.OptionId), out var link);
        SessionFact? session = null;
        DateTimeOffset? lastSync = null;
        if (link is not null)
        {
            sessionFacts.TryGetValue(link.SessionId, out session);
            if (lastSyncs.TryGetValue(Key(link.SessionId, pollId), out var value)) lastSync = value;
        }
        return new ZaloAutoSessionCandidateActivityResponse(
            candidate.OptionId,
            candidate.OptionContent,
            candidate.DayKey,
            candidate.StartTime,
            candidate.VoteCount,
            link?.SessionId,
            session?.Name,
            session?.Status,
            session?.PresentPlayerCount,
            session?.Capacity,
            lastSync);
    }

    private static IReadOnlyList<ZaloAutoSessionCandidate> DeserializeCandidates(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ZaloAutoSessionCandidate>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Key(string left, string right) => left + "\n" + right;

    private sealed record SessionFact(
        string Id,
        string Name,
        SessionStatus Status,
        int Capacity,
        int PresentPlayerCount);
}
