using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamResultRecoverySelectorSafetyTests
{
    [Theory]
    [InlineData("CN 13/9\n@Npc 9 T6")]
    [InlineData("CN @all")]
    [InlineData("CN `13/9`")]
    public void Grounded_recovery_never_splices_unsafe_session_labels_into_commands(string sessionName)
    {
        var readiness = new ZaloDraftReadinessSnapshot(
            "session-1",
            sessionName,
            "admin-1",
            "connection-1",
            "group-1",
            DateTimeOffset.UtcNow.AddDays(1),
            16,
            16,
            18,
            0,
            [],
            false,
            true,
            "fingerprint",
            ZaloDraftReadinessState.RosterNotFull,
            "draft_blocked_roster_not_full",
            false,
            false);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage(sessionName, readiness);

        Assert.Contains("`@Npc 4`", message, StringComparison.Ordinal);
        Assert.Contains("`@Npc 9`", message, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Npc 4 {sessionName}", message, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Npc 9 {sessionName}", message, StringComparison.Ordinal);
    }
}
