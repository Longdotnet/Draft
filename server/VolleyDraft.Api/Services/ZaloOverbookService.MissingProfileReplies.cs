using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloNaturalProfileValues(
    PlayerGender? Gender,
    PlayerRole? Role,
    PlayerLevel? Level,
    bool HasConflict,
    bool HasRecognizedValue,
    bool LooksLikeProfileAnswer,
    bool WantsToDefer,
    bool WantsToDismiss)
{
    internal bool WantsToSkip => WantsToDefer || WantsToDismiss;
}

internal static class ZaloNaturalProfileReplyParser
{
    private static readonly string[] UnrelatedDomainSignals =
    [
        "draft", "pass slot", "huy slot", "huy keo", "kiem them", "nhan slot",
        "waitlist", "cho team", "doi team", "share slot", "+1", "+2", "qr", "chuyen khoan",
        "cong ty", "cong viec", "cong nghe", "cong an", "cong nhan",
        "thu mon", "tui thay", "toi thay", "minh thay", "em thay"
    ];

    private static readonly HashSet<string> DirectProfileTokens = new(StringComparer.Ordinal)
    {
        "tui", "toi", "minh", "em", "anh", "chi", "tao", "to", "la", "danh", "choi",
        "nam", "nu", "con", "trai", "gai", "male", "female", "boy", "girl",
        "cong", "thu", "tan", "phong", "chu", "phu", "chuyen", "hai", "2", "setter",
        "libero", "attack", "toan", "dien", "fullstack", "full", "stack", "all", "round",
        "moi", "newbie", "beginner", "trung", "binh", "tam", "tb", "tot", "kha", "good",
        "average", "level", "trinh", "do", "nha", "nhe", "a", "aa", "ne", "do", "thoi",
        "voi", "luon", "hen", "he"
    };

    internal static ZaloNaturalProfileValues Parse(
        string? content,
        bool missingGender,
        bool missingRole,
        bool missingLevel,
        bool repliedToPrompt = false)
    {
        var raw = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var text = NormalizeConversationalSeparators(raw);
        if (text.Length == 0)
            return new(null, null, null, false, false, false, false, false);

        var wantsToDismiss = HasAny(text,
            "bo qua", "skip", "thoi khoi", "khong can hoi", "khoi hoi", "dung hoi");
        var wantsToDefer = !wantsToDismiss && HasAny(text,
            "khong biet", "chua biet", "de sau", "khong ro", "lat noi", "ti nua", "mot lat", "mai noi");
        if (wantsToDismiss || wantsToDefer)
            return new(null, null, null, false, false, true, wantsToDefer, wantsToDismiss);

        var directAnswer = IsDirectProfileAnswer(text);
        var selfScoped = HasSelfSubject(text);

        var temporalMaleWord = HasAny(text, "nam nay", "nam sau", "nam truoc", "nam ngoai");
        var male = (!temporalMaleWord && ContainsToken(text, "nam")) ||
                   HasAny(text, "con trai", "male", "boy");
        var female = ContainsToken(text, "nu") || HasAny(text, "con gai", "female", "girl");
        var genderConflict = male && female;
        PlayerGender? gender = genderConflict
            ? null
            : female ? PlayerGender.Female
            : male ? PlayerGender.Male
            : null;

        var setter = HasAny(text, "chuyen 2", "chuyen hai", "setter");
        var fullStack = HasAny(text, "toan dien", "fullstack", "full stack", "all round", "allround");
        var bareDefense = ContainsToken(text, "thu") &&
                          !HasAny(text, "thu mon", "thu 2", "thu 3", "thu 4", "thu 5", "thu 6", "thu 7",
                              "thu hai", "thu ba", "thu tu", "thu nam", "thu sau", "thu bay") &&
                          (directAnswer || selfScoped || repliedToPrompt);
        var defense = HasAny(text, "phong thu", "danh thu", "choi thu", "libero") || bareDefense;
        var bareAttack = ContainsToken(text, "cong") &&
                         !HasAny(text, "cong ty", "cong viec", "cong nghe", "cong an", "cong nhan", "cong chua") &&
                         (directAnswer || selfScoped || repliedToPrompt);
        var attack = HasAny(text, "tan cong", "danh cong", "choi cong", "chu cong", "phu cong", "doi chuyen", "attack") ||
                     bareAttack;
        var roleCount = CountTrue(setter, fullStack, defense, attack);
        var roleConflict = roleCount > 1;
        PlayerRole? role = roleConflict
            ? null
            : setter ? PlayerRole.Setter
            : fullStack ? PlayerRole.FullStack
            : defense ? PlayerRole.Defense
            : attack ? PlayerRole.Attack
            : null;

        var average = HasAny(text, "trung binh", "tam trung", "average") || ContainsToken(text, "tb");
        var good = HasAny(text, "choi tot", "danh tot", "trinh do tot", "level tot", "good") ||
                   ((ContainsToken(text, "tot") || ContainsToken(text, "kha")) &&
                    !HasAny(text, "tot nghiep", "tot hon", "tot qua", "tot roi", "tot nhat", "kha ban", "kha vui", "kha met") &&
                    (directAnswer || repliedToPrompt));
        var newbie = (HasAny(text, "moi choi", "newbie", "beginner", "trinh do moi", "level moi") &&
                      !HasAny(text, "moi choi lai")) ||
                     (missingLevel && ContainsToken(text, "moi") && directAnswer);
        var levelCount = CountTrue(average, good, newbie);
        var levelConflict = levelCount > 1;
        PlayerLevel? level = levelConflict
            ? null
            : average ? PlayerLevel.Average
            : good ? PlayerLevel.Good
            : newbie ? PlayerLevel.New
            : null;

        // Only fields that the current prompt actually asks for may mutate. If a member
        // volunteers an already-known value, keep the canonical profile unchanged.
        if (!missingGender) gender = null;
        if (!missingRole) role = null;
        if (!missingLevel) level = null;

        var hasConflict = genderConflict || roleConflict || levelConflict;
        var rawRecognized = male || female || setter || fullStack || defense || attack || average || good || newbie;
        var compact = text.Length <= 180 && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 30;
        var unrelated = UnrelatedDomainSignals.Any(signal =>
            raw.Contains(signal, StringComparison.Ordinal) || text.Contains(signal, StringComparison.Ordinal));
        var contextGrounded = directAnswer || selfScoped || repliedToPrompt;
        var looksLikeProfileAnswer = rawRecognized && compact && !unrelated && contextGrounded;

        return new(
            gender,
            role,
            level,
            hasConflict,
            rawRecognized,
            looksLikeProfileAnswer,
            false,
            false);
    }

    private static int CountTrue(params bool[] values) => values.Count(value => value);

    private static bool HasAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static bool ContainsToken(string text, string token)
    {
        var padded = $" {text} ";
        return padded.Contains($" {token} ", StringComparison.Ordinal);
    }

    private static bool HasSelfSubject(string text) =>
        new[] { "tui", "toi", "minh", "em", "tao", "to" }.Any(token => ContainsToken(text, token));

    private static bool IsDirectProfileAnswer(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length is > 0 and <= 14 &&
               tokens.All(token => DirectProfileTokens.Contains(token));
    }

    private static string NormalizeConversationalSeparators(string text)
    {
        if (text.Length == 0) return string.Empty;
        var chars = text.Select(character =>
                char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)
                    ? character
                    : ' ')
            .ToArray();
        return string.Join(' ',
            new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Fast conversational lane for members whom the bot has just asked for missing
    /// profile fields. It intentionally accepts ordinary short answers, so members do
    /// not need a command template. Safety comes from exact UID/session context, prompt
    /// correlation, current missing-field revalidation and a message-processing lease.
    /// </summary>
    public async Task<int> ProcessMissingProfileRepliesDueAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var promptStore = new ZaloMissingProfilePromptStore(db);
        var prompts = await promptStore.GetActiveAsync(now, 100, cancellationToken);
        if (prompts.Count == 0) return 0;

        var promptGroups = prompts
            .GroupBy(ProfileIdentityKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.PromptedAt).ThenBy(item => item.Id).ToList(),
                StringComparer.Ordinal);
        var sessionNames = await db.MatchSessions
            .AsNoTracking()
            .Where(session => prompts.Select(prompt => prompt.SessionId).Contains(session.Id))
            .Select(session => new { session.Id, session.Name })
            .ToDictionaryAsync(item => item.Id, item => item.Name, StringComparer.Ordinal, cancellationToken);
        var graphStore = new ZaloMessageGraphStore(db);
        var handled = 0;

        foreach (var initialPrompt in prompts)
        {
            var prompt = initialPrompt;
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
                await promptStore.CompleteAsync(prompt.Id, now, cancellationToken);
                continue;
            }

            var player = await db.SessionPlayers
                .AsNoTracking()
                .Include(item => item.PlayerProfile)
                .SingleOrDefaultAsync(item =>
                    item.Id == prompt.SessionPlayerId &&
                    item.SessionId == prompt.SessionId &&
                    item.IsPresent,
                    cancellationToken);
            var playerUid = ZaloOverbookLogic.NormalizeId(player?.PlayerProfile?.ZaloUserId);
            if (player is null || playerUid.Length == 0 ||
                !string.Equals(playerUid, prompt.ZaloUserId, StringComparison.Ordinal))
            {
                await promptStore.CompleteAsync(prompt.Id, now, cancellationToken);
                continue;
            }

            // Revalidate the prompt against canonical state before reading chat. An
            // admin or another safe flow may have filled a field since this prompt was
            // created; stale prompt flags must never overwrite newer profile data.
            var currentMissing = GetMissingProfileFlags(player);
            if (!currentMissing.Gender && !currentMissing.Role && !currentMissing.Level)
            {
                await promptStore.CompleteAsync(prompt.Id, now, cancellationToken);
                continue;
            }
            if (currentMissing.Gender != prompt.MissingGender ||
                currentMissing.Role != prompt.MissingRole ||
                currentMissing.Level != prompt.MissingLevel)
            {
                await promptStore.UpdateProgressAsync(
                    prompt.Id,
                    currentMissing.Gender,
                    currentMissing.Role,
                    currentMissing.Level,
                    prompt.LastProcessedAt,
                    false,
                    cancellationToken);
                prompt = prompt with
                {
                    MissingGender = currentMissing.Gender,
                    MissingRole = currentMissing.Role,
                    MissingLevel = currentMissing.Level
                };
            }

            // Keep provider-specific DateTimeOffset ordering out of SQL. A prompt is
            // short-lived and scoped to one sender/group, so reading this narrow sender
            // stream and ordering in memory is deterministic on SQLite and PostgreSQL.
            var senderMessages = await db.ZaloGroupMessages
                .AsNoTracking()
                .Where(message =>
                    message.ZaloConnectionId == prompt.ZaloConnectionId &&
                    message.GroupId == prompt.GroupId &&
                    !message.IsFromBot &&
                    message.SenderId == prompt.ZaloUserId)
                .Select(message => new
                {
                    message.Id,
                    message.MessageId,
                    message.Content,
                    message.SentAt,
                    message.ReceivedAt,
                    message.BotReplySentAt,
                    message.ReplyAttemptCount,
                    message.ProcessingToken,
                    message.ProcessingStartedAt
                })
                .ToListAsync(cancellationToken);

            var candidates = senderMessages
                .Where(message =>
                    message.BotReplySentAt is null &&
                    message.SentAt > prompt.LastProcessedAt &&
                    message.SentAt >= prompt.PromptedAt &&
                    message.SentAt <= prompt.ExpiresAt)
                .OrderBy(message => message.SentAt)
                .ThenBy(message => message.ReceivedAt)
                .Take(20)
                .ToList();
            if (candidates.Count == 0) continue;

            var identityPrompts = promptGroups[ProfileIdentityKey(prompt)];
            foreach (var message in candidates)
            {
                // Advance the prompt cursor even for ordinary chat. We never want this
                // worker to reconsider the same sentence every few seconds.
                var processedAt = message.SentAt > prompt.LastProcessedAt
                    ? message.SentAt
                    : prompt.LastProcessedAt.AddTicks(1);

                var firstPass = ZaloNaturalProfileReplyParser.Parse(
                    message.Content,
                    prompt.MissingGender,
                    prompt.MissingRole,
                    prompt.MissingLevel);
                ZaloMessageGraphRelation? relation = null;
                if (firstPass.HasRecognizedValue || firstPass.WantsToSkip)
                {
                    relation = await graphStore.LoadRelationAsync(
                        prompt.ZaloConnectionId,
                        prompt.GroupId,
                        message.MessageId,
                        cancellationToken);
                }

                var repliedPrompt = relation?.ToMessageId is { Length: > 0 } quotedId
                    ? identityPrompts.FirstOrDefault(item =>
                        !string.IsNullOrWhiteSpace(item.PromptMessageId) &&
                        string.Equals(item.PromptMessageId, quotedId, StringComparison.Ordinal))
                    : null;
                if (repliedPrompt is not null && repliedPrompt.Id != prompt.Id)
                {
                    await AdvanceProfilePromptCursorAsync(promptStore, prompt, processedAt, cancellationToken);
                    prompt = prompt with { LastProcessedAt = processedAt };
                    continue;
                }

                var repliedToThisPrompt = repliedPrompt?.Id == prompt.Id;
                var parsed = repliedToThisPrompt
                    ? ZaloNaturalProfileReplyParser.Parse(
                        message.Content,
                        prompt.MissingGender,
                        prompt.MissingRole,
                        prompt.MissingLevel,
                        repliedToPrompt: true)
                    : firstPass;

                var referencesThisSession = ReferencesProfileSession(
                    message.Content,
                    sessionNames.GetValueOrDefault(prompt.SessionId));
                if (identityPrompts.Count > 1 && !repliedToThisPrompt && !referencesThisSession && parsed.WantsToSkip)
                {
                    // A defer/dismiss like "để sau" is safe to apply to the oldest
                    // active prompt only. Do not silently dismiss multiple matches.
                    if (identityPrompts[0].Id != prompt.Id)
                    {
                        await AdvanceProfilePromptCursorAsync(promptStore, prompt, processedAt, cancellationToken);
                        prompt = prompt with { LastProcessedAt = processedAt };
                        continue;
                    }
                }
                else if (identityPrompts.Count > 1 && !repliedToThisPrompt && !referencesThisSession && parsed.LooksLikeProfileAnswer)
                {
                    if (identityPrompts[0].Id == prompt.Id)
                    {
                        var claimToken = await TryClaimProfileInputAsync(
                            message.Id,
                            message.ReplyAttemptCount,
                            message.ProcessingToken,
                            message.ProcessingStartedAt,
                            cancellationToken);
                        if (claimToken is not null)
                        {
                            try
                            {
                                var choices = identityPrompts
                                    .Select(item => sessionNames.GetValueOrDefault(item.SessionId) ?? item.SessionId)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Take(4);
                                await SendProfileConversationReplyAsync(
                                    session,
                                    prompt,
                                    message.MessageId,
                                    $"Tui đang hỏi hồ sơ của ông cho hơn một kèo ({string.Join(", ", choices)}) 😅 Reply đúng tin nhắn kèo đó, hoặc ghi tên kèo như `T6: nam, công` giúp tui để khỏi cập nhật nhầm.",
                                    cancellationToken);
                                await MarkProfileInputHandledAsync(
                                    message.Id,
                                    "profile_session_ambiguous",
                                    claimToken,
                                    cancellationToken);
                                handled += 1;
                            }
                            catch
                            {
                                await ReleaseProfileInputClaimAsync(message.Id, claimToken, cancellationToken);
                                throw;
                            }
                        }
                    }
                    await AdvanceProfilePromptCursorAsync(promptStore, prompt, processedAt, cancellationToken);
                    prompt = prompt with { LastProcessedAt = processedAt };
                    continue;
                }

                if (identityPrompts.Count > 1 && !repliedToThisPrompt && !referencesThisSession)
                {
                    await AdvanceProfilePromptCursorAsync(promptStore, prompt, processedAt, cancellationToken);
                    prompt = prompt with { LastProcessedAt = processedAt };
                    continue;
                }

                if (parsed.WantsToSkip)
                {
                    var claimToken = await TryClaimProfileInputAsync(
                        message.Id,
                        message.ReplyAttemptCount,
                        message.ProcessingToken,
                        message.ProcessingStartedAt,
                        cancellationToken);
                    if (claimToken is null) continue;
                    try
                    {
                        var response = parsed.WantsToDismiss
                            ? $"Ok {prompt.DisplayName}, tui bỏ qua lượt hỏi hồ sơ này nha 👌 Dữ liệu hiện tại giữ nguyên."
                            : $"Ok {prompt.DisplayName}, để sau cũng được 👌 Tui dừng hỏi ở lượt này; gần chốt nếu vẫn thiếu tui mới nhắc lại nhẹ.";
                        await SendProfileConversationReplyAsync(
                            session,
                            prompt,
                            message.MessageId,
                            response,
                            cancellationToken);
                        await MarkProfileInputHandledAsync(
                            message.Id,
                            parsed.WantsToDismiss ? "profile_dismissed" : "profile_deferred",
                            claimToken,
                            cancellationToken);
                        await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
                        handled += 1;
                        break;
                    }
                    catch
                    {
                        await ReleaseProfileInputClaimAsync(message.Id, claimToken, cancellationToken);
                        throw;
                    }
                }

                if (!parsed.LooksLikeProfileAnswer)
                {
                    await AdvanceProfilePromptCursorAsync(promptStore, prompt, processedAt, cancellationToken);
                    prompt = prompt with { LastProcessedAt = processedAt };
                    continue;
                }

                var claim = await TryClaimProfileInputAsync(
                    message.Id,
                    message.ReplyAttemptCount,
                    message.ProcessingToken,
                    message.ProcessingStartedAt,
                    cancellationToken);
                if (claim is null) continue;

                try
                {
                    var hasRequestedValue = parsed.Gender is not null || parsed.Role is not null || parsed.Level is not null;
                    if (parsed.HasConflict || !hasRequestedValue)
                    {
                        var hint = BuildProfileMissingHint(prompt.MissingGender, prompt.MissingRole, prompt.MissingLevel);
                        await SendProfileConversationReplyAsync(
                            session,
                            prompt,
                            message.MessageId,
                            parsed.HasConflict
                                ? $"Tui thấy câu này có hơn một giá trị cùng loại nên chưa dám ghi 😅 {hint}"
                                : $"Phần đó tui có rồi; hiện còn thiếu chỗ này thôi: {hint}",
                            cancellationToken);
                        await MarkProfileInputHandledAsync(
                            message.Id,
                            "profile_needs_clarification",
                            claim,
                            cancellationToken);
                        await AdvanceProfilePromptCursorAsync(promptStore, prompt, processedAt, cancellationToken);
                        prompt = prompt with { LastProcessedAt = processedAt };
                        handled += 1;
                        continue;
                    }

                    var history = new ZaloBotActionHistoryService(
                        db,
                        NullLogger<ZaloBotActionHistoryService>.Instance);
                    var before = await history.CaptureAsync(session.Id, cancellationToken);
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
                        await SendProfileConversationReplyAsync(
                            session,
                            prompt,
                            message.MessageId,
                            "Tui hiểu ý rồi nhưng backend vừa chặn cập nhật để giữ an toàn dữ liệu. Ông không cần nhập lại; tui giữ nguyên hồ sơ hiện tại nha.",
                            cancellationToken);
                        await MarkProfileInputHandledAsync(
                            message.Id,
                            "profile_update_blocked",
                            claim,
                            cancellationToken);
                        await AdvanceProfilePromptCursorAsync(promptStore, prompt, processedAt, cancellationToken);
                        prompt = prompt with { LastProcessedAt = processedAt };
                        handled += 1;
                        continue;
                    }

                    await history.RecordAsync(
                        session.Id,
                        prompt.ZaloUserId,
                        prompt.DisplayName,
                        "UpdateOwnProfile",
                        $"{prompt.DisplayName} tự bổ sung hồ sơ qua hội thoại Zalo",
                        before,
                        cancellationToken);

                    var refreshed = await db.SessionPlayers
                        .AsNoTracking()
                        .Include(item => item.PlayerProfile)
                        .SingleAsync(item => item.Id == prompt.SessionPlayerId, cancellationToken);
                    var missing = GetMissingProfileFlags(refreshed);
                    var completed = !missing.Gender && !missing.Role && !missing.Level;

                    var accepted = new List<string>();
                    if (parsed.Gender is not null) accepted.Add(parsed.Gender == PlayerGender.Female ? "nữ" : "nam");
                    if (parsed.Role is not null) accepted.Add(parsed.Role switch
                    {
                        PlayerRole.Attack => "công",
                        PlayerRole.Defense => "thủ",
                        PlayerRole.Setter => "chuyền 2",
                        PlayerRole.FullStack => "toàn diện",
                        _ => "người mới"
                    });
                    if (parsed.Level is not null) accepted.Add(parsed.Level switch
                    {
                        PlayerLevel.Good => "tốt",
                        PlayerLevel.Average => "trung bình",
                        _ => "mới chơi"
                    });

                    var response = completed
                        ? $"Ok {prompt.DisplayName} 😎 tui ghi {string.Join(" · ", accepted)} rồi. Hồ sơ kèo {session.Name} xong, không cần làm gì thêm."
                        : $"Ok {prompt.DisplayName}, tui ghi {string.Join(" · ", accepted)} rồi 👌 Còn {BuildProfileMissingHint(missing.Gender, missing.Role, missing.Level)} Cứ trả lời tiếp bình thường, không cần @bot.";
                    await SendProfileConversationReplyAsync(
                        session,
                        prompt,
                        message.MessageId,
                        response,
                        cancellationToken);
                    await MarkProfileInputHandledAsync(
                        message.Id,
                        "profile_updated",
                        claim,
                        cancellationToken);
                    await promptStore.UpdateProgressAsync(
                        prompt.Id,
                        missing.Gender,
                        missing.Role,
                        missing.Level,
                        processedAt,
                        completed,
                        cancellationToken);
                    prompt = prompt with
                    {
                        MissingGender = missing.Gender,
                        MissingRole = missing.Role,
                        MissingLevel = missing.Level,
                        LastProcessedAt = processedAt,
                        CompletedAt = completed ? DateTimeOffset.UtcNow : null
                    };
                    handled += 1;
                    if (completed) break;
                }
                catch
                {
                    await ReleaseProfileInputClaimAsync(message.Id, claim, cancellationToken);
                    throw;
                }
            }
        }

        return handled;
    }

    private static string ProfileIdentityKey(ZaloMissingProfilePromptContext prompt) =>
        $"{prompt.ZaloConnectionId}\u001f{prompt.GroupId}\u001f{prompt.ZaloUserId}";

    private static bool ReferencesProfileSession(string? content, string? sessionName)
    {
        var text = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var name = ZaloBotIntelligence.Normalize(sessionName ?? string.Empty);
        if (text.Length == 0 || name.Length == 0) return false;
        if ($" {text} ".Contains($" {name} ", StringComparison.Ordinal)) return true;

        foreach (var day in new[] { "t2", "t3", "t4", "t5", "t6", "t7", "cn" })
        {
            if ($" {name} ".Contains($" {day} ", StringComparison.Ordinal) &&
                $" {text} ".Contains($" {day} ", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private async Task AdvanceProfilePromptCursorAsync(
        ZaloMissingProfilePromptStore promptStore,
        ZaloMissingProfilePromptContext prompt,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken) =>
        await promptStore.UpdateProgressAsync(
            prompt.Id,
            prompt.MissingGender,
            prompt.MissingRole,
            prompt.MissingLevel,
            processedAt,
            false,
            cancellationToken);

    private async Task<string?> TryClaimProfileInputAsync(
        string rowId,
        int previousAttemptCount,
        string? previousProcessingToken,
        DateTimeOffset? previousProcessingStartedAt,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(previousProcessingToken) &&
            previousProcessingStartedAt is { } startedAt &&
            startedAt >= now.AddMinutes(-2))
            return null;

        var token = $"profile:{Guid.NewGuid():n}";
        var query = db.ZaloGroupMessages
            .Where(item =>
                item.Id == rowId &&
                item.BotReplySentAt == null &&
                item.ReplyAttemptCount == previousAttemptCount);
        query = string.IsNullOrWhiteSpace(previousProcessingToken)
            ? query.Where(item => item.ProcessingToken == null)
            : query.Where(item => item.ProcessingToken == previousProcessingToken);

        var claimed = await query.ExecuteUpdateAsync(update => update
            .SetProperty(item => item.ProcessingToken, token)
            .SetProperty(item => item.ProcessingStartedAt, now)
            .SetProperty(item => item.ReplyAttemptCount, item => item.ReplyAttemptCount + 1)
            .SetProperty(item => item.ReplyOutcome, "profile_processing"),
            cancellationToken);
        return claimed == 1 ? token : null;
    }

    private async Task ReleaseProfileInputClaimAsync(
        string rowId,
        string processingToken,
        CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages
            .Where(item =>
                item.Id == rowId &&
                item.BotReplySentAt == null &&
                item.ProcessingToken == processingToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ProcessingToken, (string?)null)
                .SetProperty(item => item.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(item => item.ReplyOutcome, "profile_retry"),
                cancellationToken);
    }

    private async Task SendProfileConversationReplyAsync(
        MatchSession session,
        ZaloMissingProfilePromptContext prompt,
        string sourceMessageId,
        string text,
        CancellationToken cancellationToken)
    {
        if (session.ZaloConnection is null || string.IsNullOrWhiteSpace(session.ZaloGroupId)) return;
        var idempotencyKey = $"profile-conversation:{prompt.Id}:{sourceMessageId}";
        var send = await bridge.SendGroupMessageAsync(
            session.ZaloConnection.AccountZaloId,
            session.ZaloGroupId,
            text,
            [],
            idempotencyKey: idempotencyKey);
        if (!send.Sent)
            throw new InvalidOperationException("Zalo bridge did not confirm missing-profile conversation send.");
        var persistedId = string.IsNullOrWhiteSpace(send.MessageId)
            ? idempotencyKey
            : send.MessageId!;
        await SaveBotMessageAsync(session, persistedId, text, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task MarkProfileInputHandledAsync(
        string rowId,
        string outcome,
        string processingToken,
        CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages
            .Where(item =>
                item.Id == rowId &&
                item.BotReplySentAt == null &&
                item.ProcessingToken == processingToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.BotReplySentAt, DateTimeOffset.UtcNow)
                .SetProperty(item => item.SelectedIntent, "UpdateOwnProfile")
                .SetProperty(item => item.AiCalled, false)
                .SetProperty(item => item.ReplyOutcome, outcome)
                .SetProperty(item => item.ProcessingToken, (string?)null)
                .SetProperty(item => item.ProcessingStartedAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    private static string BuildProfileMissingHint(bool missingGender, bool missingRole, bool missingLevel)
    {
        var parts = new List<string>();
        if (missingGender) parts.Add("giới tính (`nam` hoặc `nữ`)");
        if (missingRole) parts.Add("vị trí (`công`, `thủ`, `chuyền 2`, `toàn diện`)");
        if (missingLevel) parts.Add("trình độ (`mới`, `trung bình`, `tốt`)");
        return parts.Count == 0
            ? "không còn gì thiếu."
            : string.Join("; ", parts) + ".";
    }
}
