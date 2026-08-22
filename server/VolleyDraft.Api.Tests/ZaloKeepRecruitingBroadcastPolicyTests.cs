using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloKeepRecruitingBroadcastPolicyTests
{
    [Fact]
    public void UnderCapacity_BuildsAllMentionRecruitmentCopyFromGroundedReadiness()
    {
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(Snapshot(15, 15, 18));

        Assert.NotNull(message);
        Assert.StartsWith("@all ", message!, StringComparison.Ordinal);
        Assert.Contains("15/18", message);
        Assert.Contains("thiếu 3 slot", message);
        Assert.Contains("chưa vote", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vào poll", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tiếp tục kiếm thêm", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedRoster_DoesNotHideRawVersusEffectiveSlotFacts()
    {
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(Snapshot(16, 15, 18));

        Assert.NotNull(message);
        Assert.Contains("16 người / 15 effective slot", message!);
        Assert.Contains("mốc 18", message);
        Assert.Contains("thiếu 3 slot", message);
    }

    [Fact]
    public void FullRoster_DoesNotBuildRecruitmentBroadcast()
    {
        Assert.Null(ZaloKeepRecruitingBroadcastPolicy.BuildMessage(Snapshot(18, 18, 18)));
        Assert.Null(ZaloKeepRecruitingBroadcastPolicy.BuildMessage(Snapshot(19, 19, 18)));
    }

    [Fact]
    public void Cooldown_DefaultsToOneHourAndIsConfigurableWithinSafeBounds()
    {
        var defaults = new ConfigurationBuilder().Build();
        var shortValue = Config("1");
        var custom = Config("45");
        var longValue = Config("999");

        Assert.Equal(TimeSpan.FromMinutes(60), ZaloKeepRecruitingBroadcastPolicy.GetCooldown(defaults));
        Assert.Equal(TimeSpan.FromMinutes(10), ZaloKeepRecruitingBroadcastPolicy.GetCooldown(shortValue));
        Assert.Equal(TimeSpan.FromMinutes(45), ZaloKeepRecruitingBroadcastPolicy.GetCooldown(custom));
        Assert.Equal(TimeSpan.FromMinutes(180), ZaloKeepRecruitingBroadcastPolicy.GetCooldown(longValue));
    }

    [Fact]
    public void IdempotencyKey_IsStableInsideCooldownBucketAndScopedBySession()
    {
        var cooldown = TimeSpan.FromMinutes(60);
        var first = new DateTimeOffset(2026, 8, 22, 4, 10, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 8, 22, 4, 40, 0, TimeSpan.Zero);

        var firstKey = ZaloKeepRecruitingBroadcastPolicy.BuildIdempotencyKey("session-a", first, cooldown);
        var secondKey = ZaloKeepRecruitingBroadcastPolicy.BuildIdempotencyKey("session-a", second, cooldown);
        var otherSession = ZaloKeepRecruitingBroadcastPolicy.BuildIdempotencyKey("session-b", first, cooldown);

        Assert.Equal(firstKey, secondKey);
        Assert.NotEqual(firstKey, otherSession);
        Assert.StartsWith("draft-keep-recruiting:session-a:", firstKey, StringComparison.Ordinal);
    }

    private static IConfiguration Config(string cooldownMinutes) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:DraftAutopilot:KeepRecruitingBroadcastCooldownMinutes"] = cooldownMinutes
            })
            .Build();

    private static ZaloDraftReadinessSnapshot Snapshot(
        int presentPlayers,
        int effectiveSlots,
        int capacity) => new(
            SessionId: "session-a",
            SessionName: "Thứ 7 22/8",
            AdminUserId: "admin",
            ZaloConnectionId: "conn",
            GroupId: "group",
            StartTime: DateTimeOffset.UtcNow.AddHours(4),
            PresentPlayerCount: presentPlayers,
            EffectiveSlotCount: effectiveSlots,
            Capacity: capacity,
            MissingProfileCount: 0,
            MissingProfileNames: [],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: "fp",
            State: effectiveSlots >= capacity ? ZaloDraftReadinessState.Ready : ZaloDraftReadinessState.RosterNotFull,
            ReasonCode: effectiveSlots >= capacity ? "draft_ready" : "draft_blocked_roster_not_full",
            IsRosterReady: effectiveSlots == capacity,
            CanEscalate: effectiveSlots == capacity);
}
