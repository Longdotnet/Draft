using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public enum ZaloSelfServiceIdentityLinkResult
{
    NotApplicable,
    AlreadyLinked,
    Linked,
    Ambiguous,
    Conflict
}

/// <summary>
/// Reconciles a realtime Zalo sender UID with an existing player profile only when
/// there is one unique exact display-name match in the same bot-enabled group and
/// the stored profile has no UID yet. It never overwrites a different UID and never
/// guesses between duplicate names. This lets legacy self-service handlers rely on
/// stable UID identity instead of incorrectly treating "tui" as a delegated action.
/// </summary>
public sealed class ZaloSelfServiceIdentityLinker(VolleyDraftDbContext db)
{
    public async Task<ZaloSelfServiceIdentityLinkResult> TryLinkAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        connectionId = CleanId(connectionId);
        groupId = CleanId(groupId);
        var senderId = CleanId(incoming.SenderId);
        var senderName = NormalizeName(incoming.SenderName);
        if (connectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 || senderName.Length == 0)
            return ZaloSelfServiceIdentityLinkResult.NotApplicable;

        var existingForUid = await db.PlayerProfiles
            .AsNoTracking()
            .Where(profile => profile.ZaloUserId == senderId)
            .Select(profile => profile.Id)
            .ToListAsync(cancellationToken);
        if (existingForUid.Count == 1)
            return ZaloSelfServiceIdentityLinkResult.AlreadyLinked;
        if (existingForUid.Count > 1)
            return ZaloSelfServiceIdentityLinkResult.Conflict;

        var rows = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.IsPresent &&
                             player.PlayerProfileId != null &&
                             player.Session.ZaloConnectionId == connectionId &&
                             player.Session.ZaloGroupId == groupId &&
                             player.Session.BotEnabled)
            .Select(player => new
            {
                player.PlayerProfileId,
                player.DisplayName,
                ProfileDisplayName = player.PlayerProfile!.DisplayName,
                ProfileZaloUserId = player.PlayerProfile.ZaloUserId
            })
            .ToListAsync(cancellationToken);

        var candidates = rows
            .Where(row =>
                (NormalizeName(row.DisplayName) == senderName || NormalizeName(row.ProfileDisplayName) == senderName) &&
                string.IsNullOrWhiteSpace(row.ProfileZaloUserId))
            .Select(row => row.PlayerProfileId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            var conflicting = rows.Any(row =>
                (NormalizeName(row.DisplayName) == senderName || NormalizeName(row.ProfileDisplayName) == senderName) &&
                !string.IsNullOrWhiteSpace(row.ProfileZaloUserId) &&
                CleanId(row.ProfileZaloUserId) != senderId);
            return conflicting
                ? ZaloSelfServiceIdentityLinkResult.Conflict
                : ZaloSelfServiceIdentityLinkResult.NotApplicable;
        }
        if (candidates.Count != 1)
            return ZaloSelfServiceIdentityLinkResult.Ambiguous;

        var profile = await db.PlayerProfiles.SingleOrDefaultAsync(
            item => item.Id == candidates[0],
            cancellationToken);
        if (profile is null) return ZaloSelfServiceIdentityLinkResult.NotApplicable;
        if (!string.IsNullOrWhiteSpace(profile.ZaloUserId))
            return CleanId(profile.ZaloUserId) == senderId
                ? ZaloSelfServiceIdentityLinkResult.AlreadyLinked
                : ZaloSelfServiceIdentityLinkResult.Conflict;

        // Re-check immediately before the write so concurrent webhook deliveries do
        // not attach the same Zalo UID to two profiles.
        if (await db.PlayerProfiles.AsNoTracking().AnyAsync(
                item => item.ZaloUserId == senderId && item.Id != profile.Id,
                cancellationToken))
            return ZaloSelfServiceIdentityLinkResult.Conflict;

        profile.ZaloUserId = senderId;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        profile.LastSyncedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ZaloSelfServiceIdentityLinkResult.Linked;
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private static string NormalizeName(string? value)
    {
        var decomposed = (value ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch == 'đ' ? 'd' : ch);
        }
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }
}
