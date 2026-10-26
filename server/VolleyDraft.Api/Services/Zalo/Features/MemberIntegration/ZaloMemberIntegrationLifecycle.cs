namespace VolleyDraft.Api.Services;

internal enum ZaloMemberIntegrationLifecycleStage
{
    Newcomer,
    Returning,
    Integrating,
    PassiveIntegrating,
    Established,
    PassiveMember
}

internal sealed record ZaloMemberIntegrationLifecycleDecision(
    ZaloMemberIntegrationLifecycleStage Stage,
    int TargetPerDay,
    TimeSpan MinimumMemberSpacing,
    bool ShouldAnnounceGraduation,
    bool StopOnboarding);

internal static class ZaloMemberIntegrationLifecyclePolicy
{
    private const int EstablishedScoreThreshold = 55;
    internal const int PassiveMemberAfterDays = 8;

    public static ZaloMemberIntegrationLifecycleDecision Evaluate(
        ZaloMemberIntegrationContext context,
        ZaloMemberIntegrationDeliverySettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        var days = ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context);
        var signals = context.Signals;

        if (context.IsRejoin)
        {
            var rejoinedNaturally =
                signals.DistinctHumanInteractionCount >= 1 ||
                signals.MessageCount + signals.VoteCount >= 2;

            if (rejoinedNaturally)
            {
                return new(
                    ZaloMemberIntegrationLifecycleStage.Established,
                    TargetPerDay: 0,
                    MinimumMemberSpacing: TimeSpan.Zero,
                    ShouldAnnounceGraduation: true,
                    StopOnboarding: true);
            }

            if (days >= 3)
            {
                return new(
                    ZaloMemberIntegrationLifecycleStage.PassiveMember,
                    TargetPerDay: 0,
                    MinimumMemberSpacing: TimeSpan.Zero,
                    ShouldAnnounceGraduation: false,
                    StopOnboarding: true);
            }

            return new(
                ZaloMemberIntegrationLifecycleStage.Returning,
                TargetPerDay: 1,
                MinimumMemberSpacing: TimeSpan.Zero,
                ShouldAnnounceGraduation: false,
                StopOnboarding: false);
        }

        var score = ZaloMemberIntegrationScoreCapability.Calculate(signals);
        var established =
            days >= 2 &&
            score >= EstablishedScoreThreshold &&
            (signals.ActiveMessageDays >= 2 || signals.DistinctHumanInteractionCount >= 2) &&
            (signals.MessageCount >= 3 || signals.VoteCount >= 2);

        if (established)
        {
            return new(
                ZaloMemberIntegrationLifecycleStage.Established,
                TargetPerDay: 0,
                MinimumMemberSpacing: TimeSpan.Zero,
                ShouldAnnounceGraduation: true,
                StopOnboarding: true);
        }

        if (days >= PassiveMemberAfterDays)
        {
            return new(
                ZaloMemberIntegrationLifecycleStage.PassiveMember,
                TargetPerDay: 0,
                MinimumMemberSpacing: TimeSpan.Zero,
                ShouldAnnounceGraduation: false,
                StopOnboarding: true);
        }

        if (days >= 6)
        {
            return new(
                ZaloMemberIntegrationLifecycleStage.PassiveIntegrating,
                TargetPerDay: 1,
                MinimumMemberSpacing: TimeSpan.FromDays(2),
                ShouldAnnounceGraduation: false,
                StopOnboarding: false);
        }

        if (days >= 3)
        {
            return new(
                ZaloMemberIntegrationLifecycleStage.Integrating,
                TargetPerDay: 1,
                MinimumMemberSpacing: TimeSpan.Zero,
                ShouldAnnounceGraduation: false,
                StopOnboarding: false);
        }

        return new(
            ZaloMemberIntegrationLifecycleStage.Newcomer,
            TargetPerDay: settings.TargetPerMemberPerDay,
            MinimumMemberSpacing: TimeSpan.FromMinutes(settings.MinMemberIntervalMinutes),
            ShouldAnnounceGraduation: false,
            StopOnboarding: false);
    }

    public static string CurrentPeriodKey(ZaloMemberIntegrationContext context)
    {
        var anchor = context.JoinedAt ?? context.IntegrationStartedAt;
        if (anchor is null)
            throw new InvalidOperationException("MemberIntegration context is missing its current membership/integration anchor.");

        return anchor.Value.ToUniversalTime().ToString("yyyyMMddHHmmss");
    }

    public static string GraduationContentKey(ZaloMemberIntegrationContext context) =>
        $"member-integration:graduated:{context.ZaloUserId}:{CurrentPeriodKey(context)}";

    public static bool HasCurrentPeriodGraduation(
        ZaloMemberIntegrationContext context,
        IReadOnlyList<ZaloProactiveMessageHistoryData> history)
    {
        var contentKey = GraduationContentKey(context);
        return history.Any(item =>
            string.Equals(item.Kind, "member_integration_graduated", StringComparison.Ordinal) &&
            string.Equals(item.ContentKey, contentKey, StringComparison.Ordinal));
    }

    public static ZaloMemberIntegrationSuggestion BuildGraduationSuggestion(
        ZaloMemberIntegrationContext context)
    {
        var subject = ZaloMemberIntegrationCapabilitySupport.Subject(context);
        var text = context.IsRejoin
            ? $"😆 @{context.DisplayName} bắt nhịp lại nhanh quá nên coi như ĐÃ TRỞ LẠI ĐỘI HÌNH rồi nha. Từ giờ tui bỏ chế độ welcome back/hướng dẫn với ông — cứ sinh hoạt với anh em như bình thường thôi 🏐"
            : $"🎉 @{context.DisplayName} chính thức ĐÃ NHẬP HỘI rồi nha 😆 Thấy ông bắt nhịp với anh em và sinh hoạt trong group khá tự nhiên rồi. Từ giờ tui không réo ông theo kiểu hướng dẫn tân binh nữa — cứ sinh hoạt với mọi người bình thường nha 🏐";

        return new(
            CapabilityKey: "graduated",
            Priority: 1000,
            Text: text,
            ContentKey: GraduationContentKey(context),
            Reason: context.IsRejoin ? "returning_member_established" : "new_member_established",
            MentionTargets: [subject]);
    }
}
