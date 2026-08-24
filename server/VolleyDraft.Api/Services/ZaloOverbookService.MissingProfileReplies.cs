using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloNaturalProfileValues(
    PlayerGender? Gender,
    PlayerRole? Role,
    PlayerLevel? Level,
    bool HasConflict,
    bool LooksLikeProfileAnswer,
    bool WantsToSkip);

internal static class ZaloNaturalProfileReplyParser
{
    private static readonly string[] UnrelatedDomainSignals =
    [
        "draft", "pass slot", "huy slot", "huy keo", "kiem them", "nhan slot",
        "waitlist", "cho team", "doi team", "share slot", "+1", "+2", "qr", "chuyen khoan",
        "cong ty", "cong viec"
    ];

    internal static ZaloNaturalProfileValues Parse(
        string? content,
        bool missingGender,
        bool missingRole,
        bool missingLevel)
    {
        var raw = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var text = NormalizeConversationalSeparators(raw);
        if (text.Length == 0)
            return new(null, null, null, false, false, false);

        var wantsToSkip = HasAny(text,
            "khong biet", "chua biet", "de sau", "bo qua", "skip", "thoi khoi", "khong ro");
        if (wantsToSkip)
            return new(null, null, null, false, true, true);

        var male = ContainsToken(text, "nam") || HasAny(text, "con trai", "male", "boy");
        var female = ContainsToken(text, "nu") || HasAny(text, "con gai", "female", "girl");
        var genderConflict = male && female;
        PlayerGender? gender = genderConflict
            ? null
            : female ? PlayerGender.Female
            : male ? PlayerGender.Male
            : null;

        var setter = HasAny(text, "chuyen 2", "chuyen hai", "setter");
        var fullStack = HasAny(text, "toan dien", "fullstack", "full stack", "all round", "allround");
        var defense = HasAny(text, "phong thu", "danh thu", "choi thu", "libero") ||
                      IsShortNaturalTokenAnswer(text, "thu");
        var attack = HasAny(text, "tan cong", "danh cong", "choi cong", "chu cong", "phu cong", "doi chuyen", "attack") ||
                     IsShortNaturalTokenAnswer(text, "cong");
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
        var good = HasAny(text, "choi tot", "trinh do tot", "level tot", "good") ||
                   ContainsToken(text, "tot") || ContainsToken(text, "kha");
        var newbie = HasAny(text, "moi choi", "newbie", "beginner", "trinh do moi", "level moi") ||
                     (missingLevel && IsShortNaturalTokenAnswer(text, "moi"));
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
        var compact = text.Length <= 140 && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 24;
        var unrelated = UnrelatedDomainSignals.Any(signal =>
            raw.Contains(signal, StringComparison.Ordinal) || text.Contains(signal, StringComparison.Ordinal));
        var looksLikeProfileAnswer = rawRecognized && compact && !unrelated;

        return new(gender, role, level, hasConflict, looksLikeProfileAnswer, false);
    }

    private static int CountTrue(params bool[] values) => values.Count(value => value);

    private static bool HasAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static bool ContainsToken(string text, string token)
    {
        var padded = $" {text} ";
        return padded.Contains($" {token} ", StringComparison.Ordinal);
    }

    private static bool IsShortNaturalTokenAnswer(string text, string token)
    {
        if (!ContainsToken(text, token)) return false;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 7 || tokens.Any(item => item.Any(char.IsDigit))) return false;
        return true;
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
    /// profile fields. It intentionally reads ordinary group replies, so the member
    /// does not need to remember @bot or a command template.
    ///
    /// Safety comes from context, not syntax: exact verified Zalo UID + exact session
    /// player + a short-lived prompt + only currently-missing fields may be updated.
    /// </summary>
    public async Task<int> ProcessMissingProfileRepliesDueAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var promptStore = new ZaloMissingProfilePromptStore(db);
        var prompts = await promptStore.GetActiveAsync(now, 100, cancellationToken);
        if (prompts.Count == 0) return 0;

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
                    message.BotReplySentAt
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

            foreach (var message in candidates)
            {
                var parsed = ZaloNaturalProfileReplyParser.Parse(
                    message.Content,
                    prompt.MissingGender,
                    prompt.MissingRole,
                    prompt.MissingLevel);

                // Advance the cursor even for ordinary chat. We never want the worker
                // to keep reconsidering the same sentence every few seconds.
                var processedAt = message.SentAt > prompt.LastProcessedAt
                    ? message.SentAt
                    : prompt.LastProcessedAt.AddTicks(1);

                if (parsed.WantsToSkip)
                {
                    await SendProfileConversationReplyAsync(
                        session,
                        prompt,
                        message.MessageId,
                        $"Ok {prompt.DisplayName}, để sau cũng được 👌 Tui không hỏi ép nữa. Khi nào tiện thì nói lại phần còn thiếu là được.",
                        cancellationToken);
                    await MarkProfileInputHandledAsync(message.Id, "profile_skipped", cancellationToken);
                    await promptStore.CompleteAsync(prompt.Id, processedAt, cancellationToken);
                    handled += 1;
                    break;
                }

                if (!parsed.LooksLikeProfileAnswer)
                {
                    await promptStore.UpdateProgressAsync(
                        prompt.Id,
                        prompt.MissingGender,
                        prompt.MissingRole,
                        prompt.MissingLevel,
                        processedAt,
                        false,
                        cancellationToken);
                    prompt = prompt with { LastProcessedAt = processedAt };
                    continue;
                }

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
                    await MarkProfileInputHandledAsync(message.Id, "profile_needs_clarification", cancellationToken);
                    await promptStore.UpdateProgressAsync(
                        prompt.Id,
                        prompt.MissingGender,
                        prompt.MissingRole,
                        prompt.MissingLevel,
                        processedAt,
                        false,
                        cancellationToken);
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
                        "Tui hiểu ý rồi nhưng backend vừa chặn cập nhật để giữ an toàn dữ liệu. Ông không cần nhập lại; để tui giữ nguyên hồ sơ hiện tại nha.",
                        cancellationToken);
                    await MarkProfileInputHandledAsync(message.Id, "profile_update_blocked", cancellationToken);
                    await promptStore.UpdateProgressAsync(
                        prompt.Id,
                        prompt.MissingGender,
                        prompt.MissingRole,
                        prompt.MissingLevel,
                        processedAt,
                        false,
                        cancellationToken);
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
                var missingGender = refreshed.Gender == PlayerGender.Unknown ||
                                    refreshed.PlayerProfile?.Gender is null or PlayerGender.Unknown;
                var missingRole = refreshed.PlayerProfile is not null && refreshed.PlayerProfile.DefaultRole is null;
                var missingLevel = refreshed.PlayerProfile is not null && refreshed.PlayerProfile.DefaultLevel is null;
                var completed = !missingGender && !missingRole && !missingLevel;

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
                    : $"Ok {prompt.DisplayName}, tui ghi {string.Join(" · ", accepted)} rồi 👌 Còn {BuildProfileMissingHint(missingGender, missingRole, missingLevel)} Cứ trả lời tiếp bình thường, không cần @bot.";
                await SendProfileConversationReplyAsync(
                    session,
                    prompt,
                    message.MessageId,
                    response,
                    cancellationToken);
                await MarkProfileInputHandledAsync(message.Id, "profile_updated", cancellationToken);
                await promptStore.UpdateProgressAsync(
                    prompt.Id,
                    missingGender,
                    missingRole,
                    missingLevel,
                    processedAt,
                    completed,
                    cancellationToken);
                prompt = prompt with
                {
                    MissingGender = missingGender,
                    MissingRole = missingRole,
                    MissingLevel = missingLevel,
                    LastProcessedAt = processedAt,
                    CompletedAt = completed ? DateTimeOffset.UtcNow : null
                };
                handled += 1;
                if (completed) break;
            }
        }

        return handled;
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
        var persistedId = string.IsNullOrWhiteSpace(send.MessageId)
            ? idempotencyKey
            : send.MessageId!;
        await SaveBotMessageAsync(session, persistedId, text, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task MarkProfileInputHandledAsync(
        string rowId,
        string outcome,
        CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages
            .Where(item => item.Id == rowId && item.BotReplySentAt == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.BotReplySentAt, DateTimeOffset.UtcNow)
                .SetProperty(item => item.SelectedIntent, "UpdateOwnProfile")
                .SetProperty(item => item.AiCalled, false)
                .SetProperty(item => item.ReplyOutcome, outcome),
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
