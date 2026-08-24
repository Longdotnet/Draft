using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// When a full roster (or an explicitly locked partial roster) is blocked only by
    /// missing player profile data, ask the actual affected members in Zalo instead of
    /// making an organizer open the website. This shares the existing draft-prep bucket
    /// with the leader reminder so one bucket produces one useful message, not two.
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

            var mentionable = incompletePlayers
                .Where(player => !string.IsNullOrWhiteSpace(player.PlayerProfile?.ZaloUserId))
                .GroupBy(
                    player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId),
                    StringComparer.Ordinal)
                .Where(group => group.Key.Length > 0)
                .Select(group => group.First())
                .Take(8)
                .ToList();
            if (mentionable.Count == 0)
            {
                // No verified Zalo identity means this lane cannot safely target a human.
                // Leave the bucket untouched so the existing leader reminder can explain
                // the blocker without fabricating a mention from display-name matching.
                continue;
            }

            var ids = mentionable
                .Select(player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId))
                .ToList();
            var names = mentionable.ToDictionary(
                player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId),
                player => player.DisplayName,
                StringComparer.Ordinal);

            var details = incompletePlayers
                .Take(10)
                .Select(FormatMissingProfileDetail)
                .ToList();
            var body = $"Kèo {session.Name} gần chốt draft rồi nhưng còn hồ sơ thiếu dữ liệu: {string.Join("; ", details)}. " +
                       "Mấy ông được tag trả lời ngay trong group là được, không cần vào web. " +
                       "Ví dụ: `nam, công, tốt` hoặc chỉ gửi phần đang thiếu. Tui chỉ cập nhật đúng người/field backend resolve được.";

            var withoutVerifiedUid = incompletePlayers
                .Where(player => string.IsNullOrWhiteSpace(player.PlayerProfile?.ZaloUserId))
                .Select(player => player.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
            if (withoutVerifiedUid.Count > 0)
            {
                body += $" Chưa tag an toàn được: {string.Join(", ", withoutVerifiedUid)}; bot không tự đoán UID theo tên.";
            }

            var outgoing = BuildMentionMessage(ids, names, body);
            var idempotencyKey = $"missing-profile:{session.Id}:{bucket.Key}";
            try
            {
                await bridge.SendGroupMessageAsync(
                    session.ZaloConnection!.AccountZaloId,
                    session.ZaloGroupId!,
                    outgoing.Message,
                    outgoing.Mentions,
                    idempotencyKey: idempotencyKey);
                await SaveBotMessageAsync(session, idempotencyKey, outgoing.Message, now, cancellationToken);
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

    private static string FormatMissingProfileDetail(SessionPlayer player)
    {
        var missing = new List<string>();
        if (player.Gender == PlayerGender.Unknown ||
            player.PlayerProfile?.Gender is null or PlayerGender.Unknown)
            missing.Add("giới tính");
        if (player.PlayerProfile is not null && player.PlayerProfile.DefaultRole is null)
            missing.Add("vị trí");
        if (player.PlayerProfile is not null && player.PlayerProfile.DefaultLevel is null)
            missing.Add("trình độ");
        if (missing.Count == 0) missing.Add("hồ sơ");
        return $"{player.DisplayName} ({string.Join("/", missing)})";
    }
}
