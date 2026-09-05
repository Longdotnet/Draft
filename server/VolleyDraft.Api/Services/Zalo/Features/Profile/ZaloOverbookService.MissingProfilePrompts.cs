using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// When a full roster (or an explicitly locked partial roster) is blocked only by
    /// missing player profile data, ask the actual affected members in Zalo instead of
    /// making an organizer hunt through admin screens. This shares the existing draft-
    /// prep bucket with the leader reminder so one bucket produces one useful message.
    /// </summary>
    public async Task<int> ProcessMissingProfilePromptsDueAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (!settings.Enabled || !settings.ProactiveEnabled) return 0;

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item => item.BotEnabled &&
                           item.ZaloConnection != null &&
                           item.ZaloConnectionId != null &&
                           item.ZaloGroupId != null &&
                           item.StartTime != null &&
                           (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection))
            .ToListAsync(cancellationToken);

        var candidates = sessions
            .Select(session => new
            {
                Session = session,
                Bucket = ZaloDraftPreparationReminderPolicy.GetDueBucket(
                    session.StartTime!.Value,
                    now,
                    settings.StopNudgingMinutesBeforeStart)
            })
            .Where(item => item.Bucket is not null)
            .GroupBy(
                item => $"{item.Session.ZaloConnectionId}:{item.Session.ZaloGroupId}",
                StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Session.StartTime).First())
            .OrderBy(item => item.Session.StartTime)
            .Take(30)
            .ToList();

        var reminderStore = new ZaloDraftPreparationReminderStore(db);
        var decisionStore = new ZaloDraftPreparationDecisionStore(db);
        var promptStore = new ZaloMissingProfilePromptStore(db);
        var sent = 0;

        foreach (var candidate in candidates)
        {
            if (sent >= settings.MaxSendsPerCycle) break;
            var session = candidate.Session;
            var bucket = candidate.Bucket!;
            var previous = await reminderStore.GetAsync(session.Id, cancellationToken);
            if (string.Equals(previous?.LastBucketKey, bucket.Key, StringComparison.Ordinal))
                continue;

            var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
            if (!sync.Success)
            {
                logger.LogDebug(
                    "Missing-profile prompt postponed because linked poll sync failed Session={SessionId} Reason={Reason}",
                    session.Id,
                    sync.Error);
                continue;
            }

            var readiness = await new ZaloDraftReadinessService(db)
                .BuildAsync(session.Id, now, cancellationToken);
            if (readiness is null || readiness.MissingProfileCount <= 0)
                continue;

            var decision = await decisionStore.GetAsync(session.Id, cancellationToken);
            if (decision?.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
                continue;

            var lockedPartial = decision?.Kind == ZaloDraftPreparationDecisionKind.PlayCurrentRoster &&
                                decision.MatchesRoster(readiness);
            var fullRosterBlockedByProfiles = readiness.State == ZaloDraftReadinessState.MissingProfiles;
            if (!lockedPartial && !fullRosterBlockedByProfiles)
                continue;

            if (await CountActiveSlotRisksAsync(session, cancellationToken) > 0)
                continue;

            var incompletePlayers = await db.SessionPlayers
                .AsNoTracking()
                .Include(player => player.PlayerProfile)
                .Where(player =>
                    player.SessionId == session.Id &&
                    player.IsPresent &&
                    (player.Gender == PlayerGender.Unknown ||
                     (player.PlayerProfile != null &&
                      (player.PlayerProfile.Gender == null ||
                       player.PlayerProfile.Gender == PlayerGender.Unknown ||
                       player.PlayerProfile.DefaultRole == null ||
                       player.PlayerProfile.DefaultLevel == null))))
                .OrderBy(player => player.DisplayName)
                .ToListAsync(cancellationToken);
            if (incompletePlayers.Count == 0)
                continue;

            var mentionableAll = incompletePlayers
                .Where(player => !string.IsNullOrWhiteSpace(player.PlayerProfile?.ZaloUserId))
                .GroupBy(
                    player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId),
                    StringComparer.Ordinal)
                .Where(group => group.Key.Length > 0)
                .Select(group => group.First())
                .ToList();
            var mentionable = mentionableAll.Take(8).ToList();
            var ids = mentionable
                .Select(player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId))
                .ToList();
            var names = mentionable.ToDictionary(
                player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId),
                player => player.DisplayName,
                StringComparer.Ordinal);

            var withoutVerifiedUid = incompletePlayers
                .Where(player => string.IsNullOrWhiteSpace(player.PlayerProfile?.ZaloUserId))
                .Select(player => player.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            var deferredMentionNames = mentionableAll
                .Skip(8)
                .Select(player => player.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            var needsOrganizerHelp = withoutVerifiedUid.Count > 0 || deferredMentionNames.Count > 0;

            DraftApproverCandidate? organizer = null;
            if (needsOrganizerHelp)
            {
                var resolved = await ResolveDraftApproversAsync(session, settings, cancellationToken);
                if (resolved.RoleLookupSucceeded)
                {
                    organizer = resolved.Candidates.FirstOrDefault(candidate =>
                        !ids.Contains(candidate.ZaloUserId, StringComparer.Ordinal));
                    if (organizer is not null)
                    {
                        ids.Add(organizer.ZaloUserId);
                        names[organizer.ZaloUserId] = organizer.DisplayName;
                    }
                }
            }

            // If nobody can be safely addressed, leave this bucket untouched. The
            // existing leader reminder remains the fallback rather than fabricating a
            // UID from display-name similarity.
            if (ids.Count == 0)
                continue;

            var details = incompletePlayers
                .Take(10)
                .Select(FormatMissingProfileDetail)
                .ToList();
            var body = $"Kèo {session.Name} gần chốt draft rồi, còn thiếu chút hồ sơ: {string.Join("; ", details)}. ";
            if (mentionable.Count > 0)
            {
                body += "Mấy ông được tag cứ trả lời tự nhiên ngay trong group — ví dụ `nam`, `thủ`, `mới chơi`, " +
                        "hoặc `tui nam, đánh công, tầm trung bình`. Không cần @bot; biết phần nào nói phần đó 😎";
            }

            if (withoutVerifiedUid.Count > 0)
            {
                body += $" Còn {string.Join(", ", withoutVerifiedUid)} chưa map được UID chắc chắn nên tui không tag bừa theo tên.";
                body += organizer is null
                    ? " Khi map được đúng người tui mới hỏi tiếp."
                    : " Trưởng/phó được tag giúp gọi đúng người vào trả lời một lần nha.";
            }
            if (deferredMentionNames.Count > 0)
            {
                body += $" Lượt này tui chưa tag hết để khỏi spam; còn {string.Join(", ", deferredMentionNames)} sẽ được xử lý ở lượt kế tiếp nếu vẫn thiếu.";
            }

            var outgoing = BuildMentionMessage(ids, names, body);
            var idempotencyKey = $"missing-profile:{session.Id}:{bucket.Key}";
            try
            {
                var send = await bridge.SendGroupMessageAsync(
                    session.ZaloConnection!.AccountZaloId,
                    session.ZaloGroupId!,
                    outgoing.Message,
                    outgoing.Mentions,
                    idempotencyKey: idempotencyKey);
                if (!send.Sent)
                    throw new InvalidOperationException("Zalo bridge did not confirm missing-profile prompt send.");

                var providerMessageId = NormalizeProviderMessageId(send.MessageId);
                if (providerMessageId is not null)
                    await SaveBotMessageAsync(
                        session,
                        providerMessageId,
                        outgoing.Message,
                        now,
                        cancellationToken);

                var ttlMinutes = Math.Clamp(
                    configuration.GetValue("ZaloBot:ProfilePromptTtlMinutes", 60),
                    10,
                    180);
                var ttlExpiry = now.AddMinutes(ttlMinutes);
                var expiresAt = session.StartTime is { } startTime && startTime > now && startTime < ttlExpiry
                    ? startTime
                    : ttlExpiry;
                foreach (var player in mentionable)
                {
                    var uid = ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId);
                    var missing = GetMissingProfileFlags(player);
                    await promptStore.UpsertAsync(
                        session.ZaloConnectionId!,
                        session.ZaloGroupId!,
                        session.Id,
                        player.Id,
                        uid,
                        player.DisplayName,
                        missing.Gender,
                        missing.Role,
                        missing.Level,
                        providerMessageId,
                        now,
                        expiresAt,
                        cancellationToken);
                }

                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    0,
                    readiness.Fingerprint,
                    now,
                    cancellationToken);
                sent += 1;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Could not send targeted missing-profile prompt Session={SessionId}",
                    session.Id);
            }
        }

        return sent;
    }

    private static (bool Gender, bool Role, bool Level) GetMissingProfileFlags(SessionPlayer player)
    {
        var missingGender = player.Gender == PlayerGender.Unknown ||
                            player.PlayerProfile?.Gender is null or PlayerGender.Unknown;
        var missingRole = player.PlayerProfile is not null && player.PlayerProfile.DefaultRole is null;
        var missingLevel = player.PlayerProfile is not null && player.PlayerProfile.DefaultLevel is null;
        return (missingGender, missingRole, missingLevel);
    }

    private static string FormatMissingProfileDetail(SessionPlayer player)
    {
        var missing = GetMissingProfileFlags(player);
        var fields = new List<string>();
        if (missing.Gender) fields.Add("giới tính");
        if (missing.Role) fields.Add("vị trí");
        if (missing.Level) fields.Add("trình độ");
        if (fields.Count == 0) fields.Add("hồ sơ");
        return $"{player.DisplayName} ({string.Join("/", fields)})";
    }
}
