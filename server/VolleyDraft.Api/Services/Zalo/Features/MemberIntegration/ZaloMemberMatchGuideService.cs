using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloMemberMatchGuideOutcome
{
    None,
    Deferred,
    Sent
}

internal static class ZaloMemberMatchGuidePolicy
{
    public static bool CountsTowardMemberDelivery(ZaloProactiveMessageHistoryData item) =>
        (item.Lane == ZaloProactiveLane.MemberIntegration ||
         item.Lane == ZaloProactiveLane.MemberIntegrationRecipient ||
         item.Lane == ZaloProactiveLane.Community) &&
        item.Kind.StartsWith("member_integration_", StringComparison.Ordinal) &&
        item.SubjectUserId is not null;

    public static bool IsEligible(
        ZaloMemberIntegrationContext context,
        ZaloMemberIntegrationDeliverySettings deliverySettings,
        IReadOnlyList<ZaloProactiveMessageHistoryData>? memberHistory = null)
    {
        if (context.IsRejoin) return false;
        if (memberHistory is not null &&
            ZaloMemberIntegrationLifecyclePolicy.HasCurrentPeriodGraduation(context, memberHistory))
        {
            return false;
        }

        var lifecycle = ZaloMemberIntegrationLifecyclePolicy.Evaluate(context, deliverySettings);
        return !lifecycle.StopOnboarding && lifecycle.Stage is
            ZaloMemberIntegrationLifecycleStage.Newcomer or
            ZaloMemberIntegrationLifecycleStage.Integrating or
            ZaloMemberIntegrationLifecycleStage.PassiveIntegrating;
    }
}

/// <summary>
/// Sends a once-per-day practical venue guide to onboarding members who are actually
/// on today's roster. This lane shares MemberIntegration history/cooldowns so a guide
/// replaces the ordinary onboarding touch for that member instead of becoming extra spam.
/// </summary>
internal sealed class ZaloMemberMatchGuideService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloProactiveMessageStore proactiveStore,
    ZaloMemberIntegrationTextGenerator textGenerator,
    ILogger logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<ZaloMemberMatchGuideOutcome> TrySendAsync(
        string accountId,
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloMemberIntegrationContext> contexts,
        IReadOnlyList<ZaloProactiveMessageHistoryData> history,
        ZaloMemberIntegrationDeliverySettings settings,
        DateTimeOffset now,
        DateTimeOffset localNow,
        DateTimeOffset? lastHumanMessageAt,
        CancellationToken cancellationToken)
    {
        var newcomers = contexts
            .Where(context => ZaloMemberMatchGuidePolicy.IsEligible(
                context,
                settings,
                history
                    .Where(item =>
                        (item.Lane == ZaloProactiveLane.MemberIntegration ||
                         item.Lane == ZaloProactiveLane.MemberIntegrationRecipient ||
                         item.Lane == ZaloProactiveLane.Community) &&
                        item.SubjectUserId == context.ZaloUserId &&
                        item.Kind.StartsWith("member_integration_", StringComparison.Ordinal))
                    .ToList()))
            .GroupBy(context => CleanId(context.ZaloUserId), StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(context => CleanId(context.ZaloUserId).Length > 0)
            .ToDictionary(context => CleanId(context.ZaloUserId), StringComparer.Ordinal);
        if (newcomers.Count == 0) return ZaloMemberMatchGuideOutcome.None;

        var localDayStart = new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            0,
            0,
            0,
            VietnamOffset);
        var dayStartUtc = localDayStart.ToUniversalTime();
        var dayEndUtc = localDayStart.AddDays(1).ToUniversalTime();

        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == connectionId &&
                session.ZaloGroupId == groupId &&
                session.StartTime != null &&
                session.StartTime >= dayStartUtc &&
                session.StartTime < dayEndUtc &&
                session.StartTime > now &&
                session.Status != SessionStatus.Cancelled &&
                session.Status != SessionStatus.Finished)
            .OrderBy(session => session.StartTime)
            .Select(session => new SameDaySession(
                session.Id,
                session.StartTime!.Value,
                session.Location,
                session.ParkingInstructions,
                session.LocationImageUrl))
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return ZaloMemberMatchGuideOutcome.None;

        var alreadyTouchedToday = history
            .Where(item =>
                ZaloMemberMatchGuidePolicy.CountsTowardMemberDelivery(item) &&
                item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date)
            .Select(item => CleanId(item.SubjectUserId))
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            var contentKey = $"member-integration:match-guide:{session.Id}";
            var alreadyGuided = history
                .Where(item =>
                    item.Lane == ZaloProactiveLane.MemberIntegrationRecipient &&
                    item.Kind == "member_integration_match_guide" &&
                    string.Equals(item.ContentKey, contentKey, StringComparison.Ordinal) &&
                    item.SubjectUserId is not null)
                .Select(item => CleanId(item.SubjectUserId))
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            var rosterIds = await db.SessionPlayers
                .AsNoTracking()
                .Where(player =>
                    player.SessionId == session.Id &&
                    player.PlayerProfile != null &&
                    player.PlayerProfile.ZaloUserId != null)
                .Select(player => player.PlayerProfile!.ZaloUserId!)
                .ToListAsync(cancellationToken);
            var roster = rosterIds
                .Select(CleanId)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            var recipients = newcomers
                .Where(item =>
                    roster.Contains(item.Key) &&
                    !alreadyGuided.Contains(item.Key) &&
                    !alreadyTouchedToday.Contains(item.Key))
                .Select(item => item.Value)
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (recipients.Count == 0) continue;

            var venue = await new ZaloVenueGuidanceResolver(db).ResolveAsync(
                connectionId,
                groupId,
                session.Location,
                session.ParkingInstructions,
                session.LocationImageUrl,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(venue.LocationImageUrl))
            {
                logger.LogDebug(
                    "Same-day newcomer match guide skipped because no venue image was resolved Group={GroupId} Session={SessionId}",
                    groupId,
                    session.Id);
                continue;
            }

            if (lastHumanMessageAt is { } lastHuman &&
                now - lastHuman < TimeSpan.FromMinutes(settings.QuietMinutes))
            {
                return ZaloMemberMatchGuideOutcome.Deferred;
            }

            var mentionText = string.Join(' ', recipients.Select(item => $"@{item.DisplayName.Trim()}"));
            var localStart = session.StartTime.ToOffset(VietnamOffset).ToString("HH:mm");
            var location = string.IsNullOrWhiteSpace(session.Location)
                ? "sân của kèo hôm nay"
                : session.Location.Trim();
            var parkingSuffix = string.IsNullOrWhiteSpace(venue.ParkingInstructions)
                ? string.Empty
                : $" Gửi xe: {venue.ParkingInstructions.Trim()}.";
            var variants = new[]
            {
                $"🏐 {mentionText} hôm nay {localStart} có kèo ở {location} nha 😆 Mới đi buổi đầu thì coi hình này cho dễ tìm sân với chỗ gửi xe.{parkingSuffix}",
                $"😆 {mentionText} nay có tên trong kèo {localStart} ở {location} nè. Tui gửi sơ đồ luôn, coi trước đường vào sân với chỗ gửi xe cho khỏi đi vòng nha.{parkingSuffix}",
                $"📍 {mentionText} nhắc nhẹ kèo hôm nay {localStart} tại {location}. Có hình sân + chỗ gửi xe đây, tới nơi mở ra nhìn là dễ kiếm hơn nha 😄{parkingSuffix}"
            };
            var mentionTargets = recipients
                .Select(item => new ZaloMemberIntegrationMentionTarget(item.ZaloUserId, item.DisplayName))
                .ToArray();
            var suggestion = new ZaloMemberIntegrationSuggestion(
                "match-guide",
                1200,
                variants[0],
                contentKey,
                "newcomer_rostered_for_same_day_match",
                mentionTargets,
                variants);

            var recipientKey = string.Join('-', recipients
                .Select(item => CleanId(item.ZaloUserId))
                .OrderBy(id => id, StringComparer.Ordinal));
            var idempotencyKey = $"member-integration-match-guide:{connectionId}:{groupId}:{session.Id}:{recipientKey}";
            if (!await proactiveStore.TryAcquireLeaseAsync(
                    connectionId,
                    groupId,
                    idempotencyKey,
                    now,
                    LeaseDuration,
                    cancellationToken))
            {
                return ZaloMemberMatchGuideOutcome.Deferred;
            }

            var accepted = false;
            try
            {
                suggestion = await textGenerator.RewriteAsync(
                    recipients[0],
                    suggestion,
                    history,
                    cancellationToken);
                var response = await bridge.SendGroupMessageAsync(
                    accountId,
                    groupId,
                    suggestion.Text,
                    ZaloCommunityNudgeService.BuildExplicitMentions(suggestion),
                    imageUrl: venue.LocationImageUrl,
                    idempotencyKey: idempotencyKey);
                if (!response.Sent)
                {
                    await proactiveStore.ReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);
                    return ZaloMemberMatchGuideOutcome.Deferred;
                }

                accepted = true;
                var providerMessageId = NormalizeProviderMessageId(response.MessageId);
                await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
                    Guid.NewGuid().ToString("n"),
                    connectionId,
                    groupId,
                    localNow.ToString("yyyy-MM-dd"),
                    ZaloProactiveLane.MemberIntegration,
                    "member_integration_match_guide",
                    contentKey,
                    null,
                    null,
                    suggestion.Text,
                    now,
                    providerMessageId,
                    idempotencyKey), cancellationToken);

                foreach (var recipient in recipients)
                {
                    await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
                        Guid.NewGuid().ToString("n"),
                        connectionId,
                        groupId,
                        localNow.ToString("yyyy-MM-dd"),
                        ZaloProactiveLane.MemberIntegrationRecipient,
                        "member_integration_match_guide",
                        contentKey,
                        recipient.ZaloUserId,
                        recipient.DisplayName,
                        suggestion.Text,
                        now,
                        providerMessageId,
                        $"{idempotencyKey}:recipient:{CleanId(recipient.ZaloUserId)}"), cancellationToken);
                }

                await proactiveStore.CommitCooldownAsync(
                    connectionId,
                    groupId,
                    idempotencyKey,
                    now.AddMinutes(settings.MinGroupIntervalMinutes),
                    cancellationToken);
                return ZaloMemberMatchGuideOutcome.Sent;
            }
            catch (Exception exception)
            {
                if (!accepted)
                {
                    try
                    {
                        await proactiveStore.ReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);
                    }
                    catch (Exception releaseException) when (releaseException is not OperationCanceledException)
                    {
                        logger.LogWarning(
                            releaseException,
                            "Could not release same-day newcomer guide lease Group={GroupId} Session={SessionId}",
                            groupId,
                            session.Id);
                    }
                }

                logger.LogWarning(
                    exception,
                    "Could not send same-day newcomer match guide Group={GroupId} Session={SessionId}",
                    groupId,
                    session.Id);
                return ZaloMemberMatchGuideOutcome.Deferred;
            }
        }

        return ZaloMemberMatchGuideOutcome.None;
    }

    private static string? NormalizeProviderMessageId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private sealed record SameDaySession(
        string Id,
        DateTimeOffset StartTime,
        string? Location,
        string? ParkingInstructions,
        string? LocationImageUrl);
}
