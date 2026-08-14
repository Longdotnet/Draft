using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public enum ZaloIdentityResolutionStatus
{
    Resolved,
    Ambiguous,
    NotFound
}

public sealed record ZaloIdentityCandidate(
    string PersonKey,
    string ZaloUserId,
    string DisplayName,
    string? PlayerProfileId,
    IReadOnlyList<string> Aliases);

public sealed record ZaloIdentityResolution(
    ZaloIdentityResolutionStatus Status,
    string? PersonKey,
    string? ZaloUserId,
    string? DisplayName,
    string? PlayerProfileId,
    double Confidence,
    string Source,
    IReadOnlyList<ZaloIdentityCandidate> Candidates)
{
    public static ZaloIdentityResolution NotFound(string source = "not_found") =>
        new(ZaloIdentityResolutionStatus.NotFound, null, null, null, null, 0, source, []);
}

/// <summary>
/// Resolves human references to a stable Zalo identity before domain handlers run.
/// Zalo UID is the primary identity key; display names and user-approved aliases are
/// discovery signals only. Ambiguity is returned explicitly instead of guessing.
/// </summary>
public sealed class ZaloIdentityResolver(VolleyDraftDbContext db)
{
    public async Task<ZaloIdentityResolution> ResolveAsync(
        string groupId,
        string reference,
        string? explicitZaloUserId = null,
        string? currentSenderZaloUserId = null,
        ZaloQuotedSemanticContext? quotedContext = null,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        var explicitId = Clean(explicitZaloUserId, 100);
        var senderId = Clean(currentSenderZaloUserId, 100);
        var normalizedReference = Normalize(reference);

        if (explicitId.Length > 0)
            return await ResolveByUidAsync(groupId, explicitId, "explicit_uid", 1, cancellationToken);

        if (IsSelfReference(normalizedReference) && senderId.Length > 0)
            return await ResolveByUidAsync(groupId, senderId, "self_uid", 1, cancellationToken);

        if (quotedContext is { RefersToQuotedPerson: true } && !string.IsNullOrWhiteSpace(quotedContext.SenderId))
            return await ResolveByUidAsync(groupId, quotedContext.SenderId!, "quoted_sender_uid", .99, cancellationToken);

        if (normalizedReference.Length == 0) return ZaloIdentityResolution.NotFound("empty_reference");
        var candidates = await LoadCandidatesAsync(groupId, cancellationToken);
        if (candidates.Count == 0) return ZaloIdentityResolution.NotFound("no_candidates");

        var exact = candidates
            .Where(candidate => Normalize(candidate.DisplayName) == normalizedReference ||
                                candidate.Aliases.Any(alias => Normalize(alias) == normalizedReference))
            .ToList();
        if (exact.Count == 1)
            return Resolved(exact[0], .98, "exact_name_or_alias");
        if (exact.Count > 1)
            return Ambiguous(exact, "ambiguous_exact_name_or_alias");

        var partial = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = BestTextScore(candidate, normalizedReference)
            })
            .Where(item => item.Score >= .82)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (partial.Count == 0) return ZaloIdentityResolution.NotFound();

        var best = partial[0];
        var tied = partial.Where(item => Math.Abs(item.Score - best.Score) < .03).Select(item => item.Candidate).ToList();
        return tied.Count == 1
            ? Resolved(best.Candidate, best.Score, "unique_fuzzy_name_or_alias")
            : Ambiguous(tied, "ambiguous_fuzzy_name_or_alias");
    }

    internal static ZaloIdentityResolution ResolveCandidates(
        string reference,
        IReadOnlyList<ZaloIdentityCandidate> candidates,
        string? explicitZaloUserId = null,
        string? currentSenderZaloUserId = null,
        ZaloQuotedSemanticContext? quotedContext = null)
    {
        var explicitId = Clean(explicitZaloUserId, 100);
        if (explicitId.Length > 0)
        {
            var candidate = candidates.SingleOrDefault(item => item.ZaloUserId == explicitId);
            return candidate is null ? ZaloIdentityResolution.NotFound("explicit_uid_not_found") : Resolved(candidate, 1, "explicit_uid");
        }

        var normalized = Normalize(reference);
        if (IsSelfReference(normalized) && !string.IsNullOrWhiteSpace(currentSenderZaloUserId))
        {
            var candidate = candidates.SingleOrDefault(item => item.ZaloUserId == currentSenderZaloUserId.Trim());
            return candidate is null ? ZaloIdentityResolution.NotFound("self_uid_not_found") : Resolved(candidate, 1, "self_uid");
        }
        if (quotedContext is { RefersToQuotedPerson: true } && !string.IsNullOrWhiteSpace(quotedContext.SenderId))
        {
            var candidate = candidates.SingleOrDefault(item => item.ZaloUserId == quotedContext.SenderId);
            return candidate is null ? ZaloIdentityResolution.NotFound("quoted_uid_not_found") : Resolved(candidate, .99, "quoted_sender_uid");
        }

        var exact = candidates.Where(candidate => Normalize(candidate.DisplayName) == normalized ||
                                                  candidate.Aliases.Any(alias => Normalize(alias) == normalized)).ToList();
        if (exact.Count == 1) return Resolved(exact[0], .98, "exact_name_or_alias");
        if (exact.Count > 1) return Ambiguous(exact, "ambiguous_exact_name_or_alias");
        return ZaloIdentityResolution.NotFound();
    }

    private async Task<ZaloIdentityResolution> ResolveByUidAsync(
        string groupId,
        string zaloUserId,
        string source,
        double confidence,
        CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(groupId, cancellationToken);
        var candidate = candidates.SingleOrDefault(item => string.Equals(item.ZaloUserId, zaloUserId, StringComparison.Ordinal));
        if (candidate is not null) return Resolved(candidate, confidence, source);

        var profile = await db.PlayerProfiles.AsNoTracking()
            .Where(item => item.ZaloUserId == zaloUserId)
            .Select(item => new { item.Id, item.ZaloUserId, item.DisplayName })
            .SingleOrDefaultAsync(cancellationToken);
        if (profile is null) return ZaloIdentityResolution.NotFound(source + "_not_found");
        return Resolved(new ZaloIdentityCandidate(
            $"zalo:{profile.ZaloUserId}", profile.ZaloUserId, profile.DisplayName, profile.Id, []), confidence, source);
    }

    private async Task<IReadOnlyList<ZaloIdentityCandidate>> LoadCandidatesAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var members = await db.ZaloGroupMembers.AsNoTracking()
            .Where(item => item.GroupId == groupId && item.IsCurrentMember)
            .OrderByDescending(item => item.LastSeenAt)
            .Select(item => new { item.ZaloUserId, item.DisplayName })
            .ToListAsync(cancellationToken);
        var uniqueMembers = members
            .Where(item => !string.IsNullOrWhiteSpace(item.ZaloUserId))
            .GroupBy(item => item.ZaloUserId.Trim(), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(300)
            .ToList();
        var ids = uniqueMembers.Select(item => item.ZaloUserId.Trim()).ToList();
        var profiles = await db.PlayerProfiles.AsNoTracking()
            .Where(item => ids.Contains(item.ZaloUserId))
            .Select(item => new { item.Id, item.ZaloUserId, item.DisplayName })
            .ToListAsync(cancellationToken);
        var profileByUid = profiles
            .GroupBy(item => item.ZaloUserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var conceptStore = new ZaloUserConceptStore(db);
        var result = new List<ZaloIdentityCandidate>(uniqueMembers.Count);
        foreach (var member in uniqueMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uid = member.ZaloUserId.Trim();
            var concepts = await conceptStore.LoadActiveAsync(groupId, uid, 20, cancellationToken);
            var aliases = concepts
                .Where(item => item.ConceptType == "Alias" && item.Key == "preferred_name")
                .Select(ReadAlias)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Cast<string>()
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            profileByUid.TryGetValue(uid, out var profile);
            result.Add(new ZaloIdentityCandidate(
                $"zalo:{uid}",
                uid,
                string.IsNullOrWhiteSpace(member.DisplayName) ? profile?.DisplayName ?? uid : member.DisplayName,
                profile?.Id,
                aliases));
        }
        return result;
    }

    private static string? ReadAlias(ZaloUserConceptSnapshot concept)
    {
        try
        {
            using var document = JsonDocument.Parse(concept.ValueJson);
            return document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double BestTextScore(ZaloIdentityCandidate candidate, string normalizedReference)
    {
        var values = new[] { candidate.DisplayName }.Concat(candidate.Aliases);
        return values.Select(value => Similarity(Normalize(value), normalizedReference)).DefaultIfEmpty(0).Max();
    }

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            var ratio = Math.Min(left.Length, right.Length) / (double)Math.Max(left.Length, right.Length);
            return .8 + .18 * ratio;
        }
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0 : leftTokens.Intersect(rightTokens).Count() / (double)union;
    }

    private static ZaloIdentityResolution Resolved(ZaloIdentityCandidate candidate, double confidence, string source) =>
        new(ZaloIdentityResolutionStatus.Resolved, candidate.PersonKey, candidate.ZaloUserId, candidate.DisplayName,
            candidate.PlayerProfileId, Math.Clamp(confidence, 0, 1), source, [candidate]);

    private static ZaloIdentityResolution Ambiguous(IReadOnlyList<ZaloIdentityCandidate> candidates, string source) =>
        new(ZaloIdentityResolutionStatus.Ambiguous, null, null, null, null, 0, source, candidates);

    private static bool IsSelfReference(string value) =>
        value is "tui" or "toi" or "minh" or "em" or "anh" or "chi" or "ban than";

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character == 'đ' ? 'd' : character);
        }
        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
