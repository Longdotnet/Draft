namespace VolleyDraft.Api.Services;

/// <summary>
/// Gives a missed Morning/Night greeting a short recovery window without changing
/// the existing deterministic target minute. Planning is replayed at the end of the
/// original window while the bot-cooldown age is preserved relative to real time.
/// Idempotency/history checks therefore remain authoritative and prevent duplicates.
/// </summary>
internal static class ZaloDailyGreetingRecoveryPolicy
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private const int MorningNormalEnd = 8 * 60 + 45; // 08:45
    private const int MorningRecoveryEnd = 10 * 60;   // exclusive 10:00
    private const int NightNormalEnd = 20;            // 00:20
    private const int NightRecoveryEnd = 60;          // exclusive 01:00

    public static ZaloDailyGreetingPlan? Plan(
        ZaloDailyGreetingSnapshot snapshot,
        ZaloDailySocialSettings settings,
        int minBotIntervalMinutes)
    {
        var normal = ZaloDailyGreetingEngine.Plan(snapshot, settings, minBotIntervalMinutes);
        if (normal is not null) return normal;
        if (!TryGetRecoveryPlanningNow(snapshot.Now, out var planningNow)) return null;

        var elapsed = snapshot.Now - planningNow;
        var recoveredLastBot = snapshot.LastBotMessageAt is { } lastBot
            ? lastBot - elapsed
            : null;
        var recoverySnapshot = snapshot with
        {
            Now = planningNow,
            LastBotMessageAt = recoveredLastBot
        };
        return ZaloDailyGreetingEngine.Plan(
            recoverySnapshot,
            settings,
            minBotIntervalMinutes);
    }

    public static bool IsGreetingZone(DateTimeOffset now) =>
        ZaloDailyGreetingEngine.IsSoftGreetingZone(now) ||
        TryGetRecoveryPlanningNow(now, out _);

    internal static bool TryGetRecoveryPlanningNow(
        DateTimeOffset now,
        out DateTimeOffset planningNow)
    {
        var local = now.ToOffset(VietnamOffset);
        var minute = local.Hour * 60 + local.Minute;

        if (minute > MorningNormalEnd && minute < MorningRecoveryEnd)
        {
            planningNow = new DateTimeOffset(
                local.Year,
                local.Month,
                local.Day,
                8,
                45,
                local.Second,
                VietnamOffset);
            return true;
        }

        if (local.Hour == 0 && local.Minute > NightNormalEnd && local.Minute < NightRecoveryEnd)
        {
            planningNow = new DateTimeOffset(
                local.Year,
                local.Month,
                local.Day,
                0,
                20,
                local.Second,
                VietnamOffset);
            return true;
        }

        planningNow = now;
        return false;
    }
}
