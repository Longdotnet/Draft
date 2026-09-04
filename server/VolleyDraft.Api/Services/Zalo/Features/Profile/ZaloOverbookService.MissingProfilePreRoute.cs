using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Small semantic supplement for profile answers that mean the member is comfortable
/// in any volleyball position. Keep this constrained to an explicit position phrase;
/// a generic "cái nào cũng được" must never mutate a profile.
/// </summary>
internal static class ZaloFlexibleProfileReplySemantics
{
    internal static bool AcceptsAnyPosition(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var chars = normalized.Select(character =>
                char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)
                    ? character
                    : ' ')
            .ToArray();
        var text = string.Join(' ',
            new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!ContainsPhrase(text, "vi tri")) return false;

        var asksAny = ContainsToken(text, "nao") || ContainsToken(text, "gi");
        var inclusive = ContainsToken(text, "cung") || ContainsToken(text, "deu");
        var acceptable = ContainsToken(text, "duoc") || ContainsToken(text, "dc") || ContainsToken(text, "ok");
        return asksAny && inclusive && acceptable;
    }

    private static bool ContainsPhrase(string text, string phrase) =>
        $" {text} ".Contains($" {phrase} ", StringComparison.Ordinal);

    private static bool ContainsToken(string text, string token) =>
        $" {text} ".Contains($" {token} ", StringComparison.Ordinal);
}

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Production pre-routing entry point. A targeted missing-profile answer owns the
    /// turn before generic bot classification; otherwise preserve the existing
    /// overbook/semantic pre-routing chain unchanged.
    /// </summary>
    public async Task<bool> TryHandleZaloPreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (await TryHandleTargetedMissingProfileReplyAsync(incoming, cancellationToken))
            return true;

        return await TryHandleZaloConfirmationAsync(incoming, cancellationToken);
    }

    internal async Task<bool> TryHandleTargetedMissingProfileReplyAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session =>
                               session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new { item.Id, item.UpdatedAt })
            .ToListAsync(cancellationToken);
        var connectionId = connectionRows
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.Id)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(connectionId)) return false;

        var promptStore = new ZaloMissingProfilePromptStore(db);
        var senderPrompts = (await promptStore.GetActiveAsync(now, 100, cancellationToken))
            .Where(prompt =>
                string.Equals(prompt.ZaloConnectionId, connectionId, StringComparison.Ordinal) &&
                string.Equals(prompt.GroupId, groupId, StringComparison.Ordinal) &&
                string.Equals(prompt.ZaloUserId, senderId, StringComparison.Ordinal))
            .OrderBy(prompt => prompt.PromptedAt)
            .ThenBy(prompt => prompt.Id)
            .ToList();
        if (senderPrompts.Count == 0) return false;

        var quote = ZaloQuotedContextResolver.Resolve(incoming);
        ZaloMissingProfilePromptContext? prompt = null;
        if (quote.RepliesToBot && !string.IsNullOrWhiteSpace(quote.MessageId))
        {
            var quotedMatches = senderPrompts
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.PromptMessageId) &&
                    string.Equals(item.PromptMessageId, quote.MessageId, StringComparison.Ordinal))
                .ToList();
            if (quotedMatches.Count == 1)
                prompt = quotedMatches[0];
            else if (quotedMatches.Count > 1)
                return false;
        }

        // The prompt explicitly says a member may answer naturally without @bot. A
        // single active prompt is therefore enough context. With multiple prompts we
        // require an exact provider quote and fail closed rather than guessing a match.
        if (prompt is null)
        {
            if (senderPrompts.Count != 1) return false;
            prompt = senderPrompts[0];
        }

        var sentAt = ProfileReplyTimestamp(incoming.SentAtUnixMs, now);
        if (sentAt < prompt.PromptedAt || sentAt > prompt.ExpiresAt)
            return false;
        var processedAt = sentAt > prompt.LastProcessedAt
            ? sentAt
            : prompt.LastProcessedAt.AddTicks(1);

        var session = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .SingleOrDefaultAsync(item =>
                item.Id == prompt.SessionId &&
                item.ZaloConnectionId == prompt.ZaloConnectionId &&
                item.ZaloGroupId == prompt.GroupId,
                cancellationToken);
        if (session is null ||
            session.ZaloConnection is null ||
            !session.BotEnabled ||
            session.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
        {
            await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
            return false;
        }

        var player = await LoadProfilePromptPlayerAsync(prompt, cancellationToken);
        if (!PlayerStillMatchesPrompt(player, prompt))
        {
            await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
            return false;
        }

        var missing = GetMissingProfileFlags(player!);
        if (!missing.Gender && !missing.Role && !missing.Level)
        {
            await SendProfileConversationReplyAsync(
                session,
                prompt,
                incoming.MessageId,
                $"Hồ sơ {prompt.DisplayName} vừa đủ dữ liệu rồi 👌 Tui không ghi đè thêm nha.",
                cancellationToken);
            await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
            return true;
        }

        var parsed = ParseTargetedProfileReply(incoming.Content, missing);
        if (parsed.WantsToSkip)
        {
            var response = parsed.WantsToDismiss
                ? $"Ok {prompt.DisplayName}, tui bỏ qua lượt hỏi hồ sơ này nha 👌 Dữ liệu hiện tại giữ nguyên."
                : $"Ok {prompt.DisplayName}, để sau cũng được 👌 Tui dừng hỏi ở lượt này; gần chốt nếu vẫn thiếu tui mới nhắc lại nhẹ.";
            await SendProfileConversationReplyAsync(
                session,
                prompt,
                incoming.MessageId,
                response,
                cancellationToken);
            await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
            return true;
        }

        // An unrelated command may happen to be sent while a single prompt is active.
        // Do not steal it from the normal bot lane unless it is actually a profile turn.
        if (!parsed.LooksLikeProfileAnswer)
            return false;

        // Close the read/write race: another safe lane may have filled fields after the
        // first prompt lookup but before this webhook turn reached mutation.
        var freshPlayer = await LoadProfilePromptPlayerAsync(prompt, cancellationToken);
        if (!PlayerStillMatchesPrompt(freshPlayer, prompt))
        {
            await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
            return true;
        }

        var freshMissing = GetMissingProfileFlags(freshPlayer!);
        if (!freshMissing.Gender && !freshMissing.Role && !freshMissing.Level)
        {
            await SendProfileConversationReplyAsync(
                session,
                prompt,
                incoming.MessageId,
                $"Hồ sơ {prompt.DisplayName} vừa đủ dữ liệu rồi 👌 Tui không ghi đè thêm nha.",
                cancellationToken);
            await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
            return true;
        }

        parsed = ParseTargetedProfileReply(incoming.Content, freshMissing);
        var hasRequestedValue = parsed.Gender is not null || parsed.Role is not null || parsed.Level is not null;
        if (parsed.HasConflict || !hasRequestedValue)
        {
            var hint = BuildProfileMissingHint(freshMissing.Gender, freshMissing.Role, freshMissing.Level);
            await SendProfileConversationReplyAsync(
                session,
                prompt,
                incoming.MessageId,
                parsed.HasConflict
                    ? $"Tui thấy câu này có hơn một giá trị cùng loại nên chưa dám ghi 😅 {hint}"
                    : $"Phần đó tui có rồi; hiện còn thiếu chỗ này thôi: {hint}",
                cancellationToken);
            await promptStore.UpdateProgressAsync(
                prompt.Id,
                freshMissing.Gender,
                freshMissing.Role,
                freshMissing.Level,
                processedAt,
                false,
                cancellationToken);
            return true;
        }

        var history = new ZaloBotActionHistoryService(
            db,
            NullLogger<ZaloBotActionHistoryService>.Instance);
        var before = await history.CaptureAsync(session.Id, cancellationToken);
        var updated = await UpdatePromptProfileFieldsAsync(
            session,
            freshPlayer!,
            prompt,
            parsed,
            cancellationToken);
        if (!updated.IsSuccess || updated.Value is null)
        {
            await SendProfileConversationReplyAsync(
                session,
                prompt,
                incoming.MessageId,
                "Tui hiểu ý rồi nhưng backend vừa chặn cập nhật để giữ an toàn dữ liệu. Ông không cần nhập lại; tui giữ nguyên hồ sơ hiện tại nha.",
                cancellationToken);
            await promptStore.UpdateProgressAsync(
                prompt.Id,
                freshMissing.Gender,
                freshMissing.Role,
                freshMissing.Level,
                processedAt,
                false,
                cancellationToken);
            return true;
        }

        await history.RecordAsync(
            session.Id,
            prompt.ZaloUserId,
            prompt.DisplayName,
            "UpdateOwnProfile",
            $"{prompt.DisplayName} tự bổ sung hồ sơ qua hội thoại Zalo",
            before,
            cancellationToken);

        var refreshed = await LoadProfilePromptPlayerAsync(prompt, cancellationToken);
        if (refreshed is null)
        {
            await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
            return true;
        }

        var remaining = GetMissingProfileFlags(refreshed);
        var completed = !remaining.Gender && !remaining.Role && !remaining.Level;
        var accepted = DescribeAcceptedProfileValues(parsed);
        var reply = completed
            ? $"Ok {prompt.DisplayName} 😎 tui ghi {string.Join(" · ", accepted)} rồi. Hồ sơ kèo {session.Name} xong, không cần làm gì thêm."
            : $"Ok {prompt.DisplayName}, tui ghi {string.Join(" · ", accepted)} rồi 👌 Còn {BuildProfileMissingHint(remaining.Gender, remaining.Role, remaining.Level)} Cứ trả lời tiếp bình thường, không cần @bot.";
        await SendProfileConversationReplyAsync(
            session,
            prompt,
            incoming.MessageId,
            reply,
            cancellationToken);
        await promptStore.UpdateProgressAsync(
            prompt.Id,
            remaining.Gender,
            remaining.Role,
            remaining.Level,
            processedAt,
            completed,
            cancellationToken);
        return true;
    }

    internal static ZaloNaturalProfileValues ParseTargetedProfileReply(
        string? content,
        (bool Gender, bool Role, bool Level) missing)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(
            content,
            missing.Gender,
            missing.Role,
            missing.Level,
            repliedToPrompt: true);

        if (missing.Role &&
            parsed.Role is null &&
            !parsed.HasConflict &&
            ZaloFlexibleProfileReplySemantics.AcceptsAnyPosition(content))
        {
            parsed = parsed with
            {
                Role = PlayerRole.FullStack,
                HasRecognizedValue = true,
                LooksLikeProfileAnswer = true
            };
        }

        return parsed;
    }

    /// <summary>
    /// SessionDraftService intentionally updates only supplied session fields, but its
    /// legacy gender path also seeds null profile role/level from current session values.
    /// A conversational prompt is a strict field allow-list: answering only "nam" must
    /// not silently claim that an old/default session role or level was user-confirmed.
    /// Keep those null profile facts null unless this turn explicitly supplied them.
    /// </summary>
    private async Task<ServiceResult<SessionPlayerResponse>> UpdatePromptProfileFieldsAsync(
        MatchSession session,
        SessionPlayer player,
        ZaloMissingProfilePromptContext prompt,
        ZaloNaturalProfileValues parsed,
        CancellationToken cancellationToken)
    {
        var preserveMissingRole = parsed.Role is null && player.PlayerProfile?.DefaultRole is null;
        var preserveMissingLevel = parsed.Level is null && player.PlayerProfile?.DefaultLevel is null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await new SessionDraftService(db).UpdatePlayerProfileFromBotAsync(
            session.AdminUserId,
            session.Id,
            player.DisplayName,
            parsed.Gender,
            parsed.Role,
            parsed.Level,
            prompt.ZaloUserId,
            prompt.SessionPlayerId);
        if (!updated.IsSuccess || updated.Value is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return updated;
        }

        if ((preserveMissingRole || preserveMissingLevel) && player.PlayerProfileId is not null)
        {
            var profile = await db.PlayerProfiles
                .SingleAsync(item => item.Id == player.PlayerProfileId, cancellationToken);
            if (preserveMissingRole) profile.DefaultRole = null;
            if (preserveMissingLevel) profile.DefaultLevel = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static IReadOnlyList<string> DescribeAcceptedProfileValues(ZaloNaturalProfileValues parsed)
    {
        var accepted = new List<string>();
        if (parsed.Gender is not null)
            accepted.Add(parsed.Gender == PlayerGender.Female ? "nữ" : "nam");
        if (parsed.Role is not null)
        {
            accepted.Add(parsed.Role switch
            {
                PlayerRole.Attack => "công",
                PlayerRole.Defense => "thủ",
                PlayerRole.Setter => "chuyền 2",
                PlayerRole.FullStack => "toàn diện",
                _ => "vị trí"
            });
        }
        if (parsed.Level is not null)
        {
            accepted.Add(parsed.Level switch
            {
                PlayerLevel.Good => "tốt",
                PlayerLevel.Average => "trung bình",
                _ => "mới chơi"
            });
        }
        return accepted;
    }

    private static DateTimeOffset ProfileReplyTimestamp(long unixMs, DateTimeOffset fallback)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }
}
