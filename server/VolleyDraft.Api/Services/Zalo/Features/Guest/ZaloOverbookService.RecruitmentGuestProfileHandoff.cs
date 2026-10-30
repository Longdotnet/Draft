using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<bool> TryHandleRecruitmentGuestProfileHandoffAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var quote = ZaloQuotedContextResolver.Resolve(incoming);
        if (!quote.RepliesToBot || string.IsNullOrWhiteSpace(quote.MessageId)) return false;

        var anchor = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.MessageId == quote.MessageId &&
                item.IsFromBot,
                cancellationToken);
        if (anchor is null) return false;

        var context = ZaloRecruitmentGuestGatePolicy.TryReadGuestProfileContext(anchor.SelectedIntent);
        if (context is null) return false;

        var now = DateTimeOffset.UtcNow;
        var session = await db.MatchSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == context.Value.SessionId &&
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.BotEnabled &&
                item.StartTime != null &&
                item.StartTime > now &&
                (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection),
                cancellationToken);
        if (session is null) return false;

        if (anchor.ReplyOutcome == ZaloRecruitmentGuestGatePolicy.GuestProfileChoiceReplyOutcome)
        {
            return await HandleRecruitmentGuestProfileChoiceAsync(
                connectionId, accountId, botName, groupId, session,
                context.Value.SourceMessageId, incoming, cancellationToken);
        }

        if (anchor.ReplyOutcome is
            ZaloRecruitmentGuestGatePolicy.GuestProfileAdminPromptReplyOutcome or
            ZaloRecruitmentGuestGatePolicy.GuestProfileSponsorPromptReplyOutcome)
        {
            return await HandleRecruitmentGuestProfileInputAsync(
                connectionId, accountId, botName, groupId, session,
                context.Value.SourceMessageId, anchor.ReplyOutcome!, incoming, cancellationToken);
        }

        return false;
    }

    private async Task<bool> HandleRecruitmentGuestProfileChoiceAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        MatchSession session,
        string sourceMessageId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var choice = TryParseGuestProfileHandoffChoice(incoming.Content);
        if (choice is null) return false;

        var source = await db.ZaloGroupMessages.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.MessageId == sourceMessageId &&
            !item.IsFromBot,
            cancellationToken);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (source is null || !string.Equals(source.SenderId, senderId, StringComparison.Ordinal))
            return false;

        var guests = await LoadRecruitmentGuestProfileSourceGuestsAsync(
            session.Id, sourceMessageId, cancellationToken);
        if (guests.Count == 0)
        {
            await SendGuestProfileHandoffReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                senderId, incoming.SenderName,
                "Tui không còn thấy guest của lượt + này để cập nhật nữa.",
                "RecruitmentGuestProfileChoice",
                ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(session.Id),
                ZaloRecruitmentGuestGatePolicy.GuestConversationReplyOutcome,
                cancellationToken);
            return true;
        }

        var contextIntent = ZaloRecruitmentGuestGatePolicy
            .GuestProfileContextSelectedIntent(session.Id, sourceMessageId);
        if (choice == 1)
        {
            if (!await CanOperateGuestProfileOnBehalfAsync(session, senderId, cancellationToken))
            {
                await SendGuestProfileHandoffReplyAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    senderId, incoming.SenderName,
                    "Nhánh `1` là cập nhật hộ nên chỉ trưởng/phó hoặc operator của bot dùng được. Reply `2` để tui tag đúng người bảo lãnh vào tự khai hồ sơ nha.",
                    "RecruitmentGuestProfileChoice",
                    contextIntent,
                    ZaloRecruitmentGuestGatePolicy.GuestProfileChoiceReplyOutcome,
                    cancellationToken);
                return true;
            }

            await SendGuestProfileHandoffReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                senderId, incoming.SenderName,
                BuildGuestProfileInputPrompt(guests, "Ông cập nhật giúp nha."),
                "RecruitmentGuestProfileAdminPrompt",
                contextIntent,
                ZaloRecruitmentGuestGatePolicy.GuestProfileAdminPromptReplyOutcome,
                cancellationToken);
            return true;
        }

        var sponsorIds = guests.Select(item => item.SponsorZaloUserId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sponsorIds.Length != 1)
        {
            await SendGuestProfileHandoffReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                senderId, incoming.SenderName,
                "Lượt + này không còn xác định duy nhất người bảo lãnh nên tui chưa tag bừa. Chọn `1` để admin cập nhật hộ nha.",
                "RecruitmentGuestProfileChoice",
                contextIntent,
                ZaloRecruitmentGuestGatePolicy.GuestProfileChoiceReplyOutcome,
                cancellationToken);
            return true;
        }

        var sponsorId = sponsorIds[0];
        var sponsorMember = await db.ZaloGroupMembers.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.ZaloUserId == sponsorId &&
            item.IsCurrentMember,
            cancellationToken);
        if (sponsorMember is null)
        {
            await SendGuestProfileHandoffReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                senderId, incoming.SenderName,
                "Người bảo lãnh hiện không còn là thành viên group nên tui không tag giao hồ sơ được. Chọn `1` để admin cập nhật hộ nha.",
                "RecruitmentGuestProfileChoice",
                contextIntent,
                ZaloRecruitmentGuestGatePolicy.GuestProfileChoiceReplyOutcome,
                cancellationToken);
            return true;
        }

        await SendGuestProfileHandoffReplyAsync(
            connectionId, accountId, botName, groupId, incoming,
            sponsorMember.ZaloUserId, sponsorMember.DisplayName,
            BuildGuestProfileInputPrompt(guests, "Bạn cập nhật thông tin người bạn vừa được + giúp nha."),
            "RecruitmentGuestProfileSponsorPrompt",
            contextIntent,
            ZaloRecruitmentGuestGatePolicy.GuestProfileSponsorPromptReplyOutcome,
            cancellationToken);
        return true;
    }

    private async Task<bool> HandleRecruitmentGuestProfileInputAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        MatchSession session,
        string sourceMessageId,
        string promptOutcome,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var guests = await LoadRecruitmentGuestProfileSourceGuestsAsync(
            session.Id, sourceMessageId, cancellationToken);
        if (guests.Count == 0) return false;

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (promptOutcome == ZaloRecruitmentGuestGatePolicy.GuestProfileAdminPromptReplyOutcome)
        {
            var source = await db.ZaloGroupMessages.AsNoTracking().SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.MessageId == sourceMessageId &&
                !item.IsFromBot,
                cancellationToken);
            if (source is null ||
                !string.Equals(source.SenderId, senderId, StringComparison.Ordinal) ||
                !await CanOperateGuestProfileOnBehalfAsync(session, senderId, cancellationToken))
                return false;
        }
        else
        {
            var sponsorIds = guests.Select(item => item.SponsorZaloUserId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sponsorIds.Length != 1 || !string.Equals(sponsorIds[0], senderId, StringComparison.Ordinal))
                return false;
        }

        var parsed = ParseGuestProfilePromptInput(incoming.Content, guests);
        var contextIntent = ZaloRecruitmentGuestGatePolicy
            .GuestProfileContextSelectedIntent(session.Id, sourceMessageId);
        if (parsed.WantsToSkip)
        {
            await SendGuestProfileHandoffReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                senderId, incoming.SenderName,
                "Ok, để sau cũng được. Guest vẫn giữ slot; khi có thông tin thì nói lại tên/giới tính/vị trí/trình độ giúp tui nha.",
                "RecruitmentGuestProfileDeferred",
                ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(session.Id),
                ZaloRecruitmentGuestGatePolicy.GuestConversationReplyOutcome,
                cancellationToken);
            return true;
        }

        if (parsed.Command is null)
        {
            await SendGuestProfileHandoffReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                senderId, incoming.SenderName,
                parsed.Error ?? BuildGuestProfileInputPrompt(guests, "Tui chưa đọc chắc thông tin đó."),
                "RecruitmentGuestProfileInput",
                contextIntent,
                promptOutcome,
                cancellationToken);
            return true;
        }

        var result = await new ZaloGuestReservationService(db).UpdateProfileForSourceAsync(
            session,
            sourceMessageId,
            parsed.Command,
            cancellationToken);
        if (result.NeedsClarification)
        {
            await SendGuestProfileHandoffReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                senderId, incoming.SenderName,
                result.Clarification ?? "Nói rõ guest #1/#2 cần cập nhật giúp tui nha.",
                "RecruitmentGuestProfileInput",
                contextIntent,
                promptOutcome,
                cancellationToken);
            return true;
        }

        var refreshed = await LoadRecruitmentGuestProfileSourceGuestsAsync(
            session.Id, sourceMessageId, cancellationToken);
        var unfinished = refreshed.Where(GuestNeedsProfile).ToList();
        var changed = string.Join(", ", result.Changed.Select(item =>
            $"#{item.SponsorSequence} {item.DisplayName}"));
        var complete = unfinished.Count == 0;
        var body = complete
            ? $"Đã cập nhật {changed} cho {session.Name} 👌 Hồ sơ guest của lượt + này đủ rồi."
            : $"Đã cập nhật {changed} 👌 {BuildGuestProfileInputPrompt(unfinished, "Còn thiếu vài phần.")}";

        await SendGuestProfileHandoffReplyAsync(
            connectionId, accountId, botName, groupId, incoming,
            senderId, incoming.SenderName,
            body,
            "RecruitmentGuestProfileUpdated",
            complete
                ? ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(session.Id)
                : contextIntent,
            complete
                ? ZaloRecruitmentGuestGatePolicy.GuestConversationReplyOutcome
                : promptOutcome,
            cancellationToken);
        return true;
    }

    private async Task<List<ZaloGuestReservation>> LoadRecruitmentGuestProfileSourceGuestsAsync(
        string sessionId,
        string sourceMessageId,
        CancellationToken cancellationToken) =>
        await db.ZaloGuestReservations.AsNoTracking()
            .Where(item => item.SessionId == sessionId &&
                           item.SourceMessageId == sourceMessageId &&
                           (item.Status == ZaloGuestReservationStatus.Active ||
                            item.Status == ZaloGuestReservationStatus.Waitlisted))
            .OrderBy(item => item.SponsorSequence)
            .ToListAsync(cancellationToken);

    private async Task<bool> CanOperateGuestProfileOnBehalfAsync(
        MatchSession session,
        string senderId,
        CancellationToken cancellationToken)
    {
        var normalized = ZaloOverbookLogic.NormalizeId(senderId);
        if (normalized.Length == 0) return false;
        if (ParseStringList(session.BotOperatorZaloUserIdsJson)
            .Contains(normalized, StringComparer.Ordinal))
            return true;
        if (integration is null) return false;
        var role = await integration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId, session.Id, normalized);
        return role.IsSuccess && role.Value?.CanOperateBot == true;
    }

    private async Task SendGuestProfileHandoffReplyAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string targetUserId,
        string targetDisplayName,
        string body,
        string intent,
        string outboundSelectedIntent,
        string outboundReplyOutcome,
        CancellationToken cancellationToken)
    {
        var cleanTarget = ZaloOverbookLogic.NormalizeId(targetUserId);
        var cleanName = FriendlySponsorName(targetDisplayName, cleanTarget);
        var label = $"@{cleanName}";
        var response = $"{label} {body}";
        IReadOnlyList<BridgeOutgoingMention> mentions = cleanTarget.Length == 0
            ? []
            : [new BridgeOutgoingMention(cleanTarget, 0, label.Length)];
        await SendDeterministicPreRouteResponseAsync(
            connectionId,
            accountId,
            botName,
            groupId,
            incoming,
            response,
            intent,
            cancellationToken,
            outboundSelectedIntent,
            outboundReplyOutcome,
            mentions);
    }

    private static int? TryParseGuestProfileHandoffChoice(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty)
            .Replace("@bott", string.Empty, StringComparison.Ordinal)
            .Replace("@npc", string.Empty, StringComparison.Ordinal)
            .Trim();
        return normalized switch
        {
            "1" or "chon 1" or "1 nha" or "1 nhe" or "phuong an 1" => 1,
            "2" or "chon 2" or "2 nha" or "2 nhe" or "phuong an 2" => 2,
            _ => null
        };
    }

    private sealed record GuestProfilePromptParse(
        ZaloRecruitmentGuestCommand? Command,
        string? Error,
        bool WantsToSkip = false);

    private static GuestProfilePromptParse ParseGuestProfilePromptInput(
        string? content,
        IReadOnlyList<ZaloGuestReservation> guests)
    {
        var raw = (content ?? string.Empty).Trim();
        var natural = ZaloNaturalProfileReplyParser.Parse(
            raw,
            missingGender: true,
            missingRole: true,
            missingLevel: true,
            repliedToPrompt: true);
        if (natural.WantsToSkip)
            return new(null, null, true);
        if (natural.HasConflict)
            return new(null, "Tui thấy câu này có thông tin mâu thuẫn nên chưa ghi. Nói lại một giá trị cho mỗi phần giúp tui nha.");

        var sequence = TryReadGuestProfileSequence(raw);
        var renameTo = TryReadGuestProfileName(raw);
        if (sequence is null && guests.Count > 1)
            return new(null, $"Lượt này có {guests.Count} guest; ghi rõ `#1`/`#2` giúp tui nha.");
        if (!natural.HasRecognizedValue && string.IsNullOrWhiteSpace(renameTo))
            return new(null, "Gửi kiểu `#1 tên Minh, nam, chủ công, khá` giúp tui; có thể bổ sung từng phần.");

        return new(new ZaloRecruitmentGuestCommand(
            ZaloRecruitmentGuestCommandKind.UpdateProfile,
            SponsorSequence: sequence,
            RenameTo: renameTo,
            Gender: natural.Gender,
            Role: natural.Role,
            Level: natural.Level), null);
    }

    private static int? TryReadGuestProfileSequence(string content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content);
        var match = Regex.Match(
            normalized,
            @"(?:#\s*(?<seq>\d{1,2})|(?:ban|dua|nguoi)\s*#?\s*(?<seq2>\d{1,2}))(?:\s|$)",
            RegexOptions.CultureInvariant);
        var raw = match.Groups["seq"].Success
            ? match.Groups["seq"].Value
            : match.Groups["seq2"].Value;
        return int.TryParse(raw, out var value) && value > 0 ? value : null;
    }

    private static string? TryReadGuestProfileName(string content)
    {
        var match = Regex.Match(
            content,
            @"(?:tên|ten)\s+(?<name>[^,.;]{2,80})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var value = match.Groups["name"].Value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static string BuildGuestProfileInputPrompt(
        IReadOnlyList<ZaloGuestReservation> guests,
        string prefix)
    {
        var missing = guests.Select(item =>
        {
            var parts = new List<string>();
            if (IsGeneratedGuestName(item)) parts.Add("tên");
            if (item.Gender is null or PlayerGender.Unknown) parts.Add("giới tính");
            if (item.Role is null) parts.Add("vị trí");
            if (item.Level is null) parts.Add("trình độ");
            return $"#{item.SponsorSequence} thiếu {string.Join("/", parts)}";
        }).ToArray();
        return $"{prefix} {string.Join("; ", missing)}. Reply tin này kiểu `#1 tên Minh, nam, chủ công, khá`; có thể bổ sung từng phần.";
    }
}
