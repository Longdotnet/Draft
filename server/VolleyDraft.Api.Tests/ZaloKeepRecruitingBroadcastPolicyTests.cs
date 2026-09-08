using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloKeepRecruitingBroadcastPolicyTests
{
    [Fact]
    public void UnderCapacity_BuildsAllMentionRecruitmentCopyFromGroundedReadiness()
    {
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(
            Snapshot(15, 15, 18),
            guestSignupOpen: true);

        Assert.NotNull(message);
        Assert.StartsWith("@all ", message!, StringComparison.Ordinal);
        Assert.Contains("15/18 chỗ", message);
        Assert.Contains("thiếu 3 chỗ", message);
        Assert.Contains("chưa vote", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mở bình chọn", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tiếp tục kiếm thêm", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reply thẳng tin này `+1` hoặc `+2`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần ở trong nhóm Zalo", message, StringComparison.OrdinalIgnoreCase);
        AssertBeginnerLanguage(message);
    }

    [Fact]
    public void BeforeGuestWindow_RecruitmentCopyPrioritizesGroupMembersOnly()
    {
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(
            Snapshot(15, 15, 18),
            guestSignupOpen: false);

        Assert.NotNull(message);
        Assert.StartsWith("@all ", message!, StringComparison.Ordinal);
        Assert.Contains("chưa vote", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mở bình chọn", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+1", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+2", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ngoài nhóm", message, StringComparison.OrdinalIgnoreCase);
        AssertBeginnerLanguage(message);
    }

    [Fact]
    public void RecruitmentSelectedIntent_RoundTripsSessionIdWithoutParsingMessageText()
    {
        var selectedIntent = ZaloKeepRecruitingBroadcastPolicy.SelectedIntent("session-a");

        Assert.Equal("KeepRecruiting:session-a", selectedIntent);
        Assert.Equal("session-a", ZaloKeepRecruitingBroadcastPolicy.TryReadSessionId(selectedIntent));
        Assert.Null(ZaloKeepRecruitingBroadcastPolicy.TryReadSessionId("DraftAutopilot"));
    }

    [Fact]
    public void SharedRoster_PreservesRawAndCountedFactsWithoutEffectiveSlotJargon()
    {
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(Snapshot(16, 15, 18));

        Assert.NotNull(message);
        Assert.Contains("16 người, tính ra 15/18 chỗ để chia đội", message!);
        Assert.Contains("thiếu 3 chỗ", message);
        AssertBeginnerLanguage(message);
    }

    [Fact]
    public void FullVoteWithPassRisk_StillCallsForAReplacementInPlainLanguage()
    {
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(
            Snapshot(18, 18, 18),
            activeSlotRiskCount: 1);

        Assert.NotNull(message);
        Assert.Contains("18/18 chỗ", message!);
        Assert.Contains("1 chỗ", message);
        Assert.Contains("đang nhường/huỷ", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("người thay", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tự ngưng nhắc", message, StringComparison.OrdinalIgnoreCase);
        AssertBeginnerLanguage(message);
    }

    [Fact]
    public void UnderCapacityWithPassRisk_ExplainsBothMissingPeopleAndUnresolvedHandoff()
    {
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(
            Snapshot(15, 14, 18),
            activeSlotRiskCount: 1);

        Assert.NotNull(message);
        Assert.Contains("15 người, tính ra 14/18 chỗ để chia đội", message!);
        Assert.Contains("thiếu 4 chỗ", message);
        Assert.Contains("1 chỗ đang nhường/huỷ chưa xử lý xong", message);
        AssertBeginnerLanguage(message);
    }

    [Fact]
    public void FullCleanRoster_DoesNotBuildRecruitmentBroadcast()
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

    private static void AssertBeginnerLanguage(string message)
    {
        var forbidden = new[]
        {
            "effective slot",
            "roster",
            "sync",
            "delta",
            "auto-draft",
            "shared/rotation",
            "over-slot",
            "fingerprint",
            "backend",
            "poll",
            " slot"
        };

        foreach (var term in forbidden)
            Assert.DoesNotContain(term, message, StringComparison.OrdinalIgnoreCase);
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
