using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public enum ZaloOperatorPermissionCommandKind
{
    Grant,
    Revoke,
    List
}

public sealed record ZaloOperatorPermissionCommand(
    ZaloOperatorPermissionCommandKind Kind,
    IReadOnlyList<string> TargetZaloUserIds);

public sealed record ZaloOperatorPermissionResult(
    bool Handled,
    string? Response,
    string Intent = "OperatorPermission");

/// <summary>
/// Keeps Zalo-side operator management on the existing per-session
/// BotOperatorZaloUserIdsJson source of truth. Grant/revoke authority is supplied by
/// the caller after checking the live Zalo group owner/deputy role; ordinary bot
/// operators cannot create more operators.
/// </summary>
public sealed class ZaloOperatorPermissionCommandService(VolleyDraftDbContext db)
{
    private static readonly Regex GrantPattern = new(
        @"(?<![a-z0-9])(?:cap\s+quyen|them\s+quyen|cap\s+operator|them\s+operator|cho\s+quyen)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RevokePattern = new(
        @"(?<![a-z0-9])(?:thu\s+quyen|go\s+quyen|xoa\s+quyen|bo\s+quyen|xoa\s+operator|remove\s+operator)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ListPattern = new(
        @"(?<![a-z0-9])(?:ai\s+(?:dang\s+)?co\s+quyen|danh\s+sach\s+(?:nguoi\s+)?co\s+quyen|danh\s+sach\s+operator|operator\s+nao|xem\s+(?:danh\s+sach\s+)?quyen)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ZaloOperatorPermissionCommand? TryParse(ZaloIncomingMessageEvent incoming)
    {
        var normalized = ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty);
        ZaloOperatorPermissionCommandKind? kind = GrantPattern.IsMatch(normalized)
            ? ZaloOperatorPermissionCommandKind.Grant
            : RevokePattern.IsMatch(normalized)
                ? ZaloOperatorPermissionCommandKind.Revoke
                : ListPattern.IsMatch(normalized)
                    ? ZaloOperatorPermissionCommandKind.List
                    : null;
        if (kind is null) return null;

        var targets = incoming.Mentions
            .Where(mention => CleanId(mention.Uid).Length > 0 &&
                              CleanId(mention.Uid) != CleanId(incoming.BotId))
            .Select(mention => CleanId(mention.Uid))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
        return new ZaloOperatorPermissionCommand(kind.Value, targets);
    }

    public async Task<ZaloOperatorPermissionResult> ApplyAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloOperatorPermissionCommand command,
        bool? canManagePermissions,
        CancellationToken cancellationToken = default)
    {
        connectionId = CleanId(connectionId);
        groupId = CleanId(groupId);
        var sessions = await db.MatchSessions
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled)
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0)
            return new(true, "Tui chưa thấy kèo nào của nhóm đang bật bot để gắn quyền.");

        if (command.Kind == ZaloOperatorPermissionCommandKind.List)
        {
            var operatorIds = sessions
                .SelectMany(session => ParseOperatorIds(session.BotOperatorZaloUserIdsJson))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (operatorIds.Count == 0)
                return new(true, "Nhóm chưa cấp operator riêng cho ai á. Trưởng/phó nhóm Zalo vẫn thao tác được nha.");

            var names = await ResolveNamesAsync(groupId, operatorIds, cancellationToken);
            var display = operatorIds
                .Select(id => names.GetValueOrDefault(id, id))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new(true, $"Operator hiện tại: {string.Join(", ", display)}.");
        }

        if (command.TargetZaloUserIds.Count == 0)
            return new(true, command.Kind == ZaloOperatorPermissionCommandKind.Grant
                ? "Tag người cần cấp quyền giúp tui nha 😆"
                : "Tag người cần thu quyền giúp tui nha.");

        if (canManagePermissions is null)
            return new(true, "Tui chưa check được quyền trưởng/phó nhóm từ Zalo lúc này, thử lại xíu nha.");
        if (canManagePermissions != true)
            return new(true, "Quyền này chỉ trưởng/phó nhóm Zalo mới cấp hoặc thu được nha; operator thường không tự cấp thêm người khác.");

        var targets = command.TargetZaloUserIds
            .Select(CleanId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
        foreach (var session in sessions)
        {
            var current = ParseOperatorIds(session.BotOperatorZaloUserIdsJson);
            if (command.Kind == ZaloOperatorPermissionCommandKind.Grant)
                current.UnionWith(targets);
            else
                current.ExceptWith(targets);

            session.BotOperatorZaloUserIdsJson = JsonSerializer.Serialize(current
                .Where(id => id.Length > 0)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Take(20));
            session.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);

        var targetNames = await ResolveNamesAsync(groupId, targets, cancellationToken);
        var namesText = string.Join(", ", targets.Select(id => targetNames.GetValueOrDefault(id, id)));
        return command.Kind == ZaloOperatorPermissionCommandKind.Grant
            ? new(true, $"Oke, đã cấp quyền bot cho {namesText} rồi nha 👌")
            : new(true, $"Đã thu quyền bot của {namesText} rồi nha.");
    }

    internal static HashSet<string> ParseOperatorIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Select(CleanId)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(
        string groupId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var rows = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(member => member.GroupId == groupId && ids.Contains(member.ZaloUserId))
            .Select(member => new { member.ZaloUserId, member.DisplayName, member.LastSeenAt })
            .ToListAsync(cancellationToken);
        return rows
            .OrderByDescending(row => row.LastSeenAt)
            .GroupBy(row => CleanId(row.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => string.IsNullOrWhiteSpace(group.First().DisplayName)
                    ? group.Key
                    : group.First().DisplayName.Trim(),
                StringComparer.Ordinal);
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
