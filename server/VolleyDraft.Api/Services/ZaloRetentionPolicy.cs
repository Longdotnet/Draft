namespace VolleyDraft.Api.Services;

public sealed record ZaloRetentionPolicy(
    TimeSpan TraceRetention,
    TimeSpan MessageRelationRetention,
    TimeSpan? ActiveUserConceptRetention)
{
    public static ZaloRetentionPolicy Default { get; } = new(
        TraceRetention: TimeSpan.FromDays(30),
        MessageRelationRetention: TimeSpan.FromDays(90),
        ActiveUserConceptRetention: null);

    public static ZaloRetentionPolicy FromConfiguration(IConfiguration configuration)
    {
        var traceDays = Math.Clamp(configuration.GetValue("ZaloBot:Retention:TraceDays", 30), 1, 365);
        var relationDays = Math.Clamp(configuration.GetValue("ZaloBot:Retention:MessageRelationDays", 90), 1, 730);
        var conceptDays = configuration.GetValue<int?>("ZaloBot:Retention:UserConceptDays");
        return new ZaloRetentionPolicy(
            TimeSpan.FromDays(traceDays),
            TimeSpan.FromDays(relationDays),
            conceptDays is > 0 ? TimeSpan.FromDays(Math.Clamp(conceptDays.Value, 1, 3650)) : null);
    }

    public DateTimeOffset TraceCutoff(DateTimeOffset now) => now - TraceRetention;
    public DateTimeOffset MessageRelationCutoff(DateTimeOffset now) => now - MessageRelationRetention;
}
