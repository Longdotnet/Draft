using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberIntegrationCapabilitiesTests
{
    [Fact]
    public async Task Welcome_requires_verified_join_time_and_distinguishes_rejoin()
    {
        var capability = new ZaloMemberWelcomeCapability();

        Assert.Null(await capability.EvaluateAsync(Context(joinedDaysAgo: null)));

        var joined = await capability.EvaluateAsync(Context(joinedDaysAgo: 0));
        Assert.NotNull(joined);
        Assert.Equal("welcome", joined!.CapabilityKey);
        Assert.Equal("verified_join", joined.Reason);
        Assert.Contains("Volley Draft", joined.Text);
        Assert.Single(joined.MentionTargets);

        var rejoined = await capability.EvaluateAsync(Context(joinedDaysAgo: 0, isRejoin: true));
        Assert.NotNull(rejoined);
        Assert.Equal("verified_rejoin", rejoined!.Reason);
        Assert.Contains("Welcome back", rejoined.Text);
    }

    [Fact]
    public async Task Welcome_accepts_admin_confirmed_recovered_newcomer_without_fake_join_time()
    {
        var capability = new ZaloMemberWelcomeCapability();
        var recovered = await capability.EvaluateAsync(Context(
            joinedDaysAgo: null,
            integrationStartedDaysAgo: 0,
            isRecoveredNewcomer: true));

        Assert.NotNull(recovered);
        Assert.Equal("admin_confirmed_recovered_newcomer", recovered!.Reason);
        Assert.Contains("bữa trước tui chưa kịp chào", recovered.Text);
        Assert.Contains("Welcome", recovered.Text);
    }

    [Fact]
    public async Task Bot_identity_waits_one_day_and_uses_configured_creator_when_available()
    {
        var capability = new ZaloMemberBotIdentityCapability();

        Assert.Null(await capability.EvaluateAsync(Context(joinedDaysAgo: 0)));

        var suggestion = await capability.EvaluateAsync(Context(joinedDaysAgo: 1));
        Assert.NotNull(suggestion);
        Assert.Equal("bot-identity", suggestion!.CapabilityKey);
        Assert.Contains("Thanh Long", suggestion.Text);
        Assert.Equal("member-integration:bot-identity", suggestion.ContentKey);
    }

    [Fact]
    public async Task Group_origins_requires_age_and_grounded_group_purpose()
    {
        var capability = new ZaloMemberGroupOriginsCapability();

        Assert.Null(await capability.EvaluateAsync(Context(joinedDaysAgo: 1)));
        Assert.Null(await capability.EvaluateAsync(Context(
            joinedDaysAgo: 2,
            facts: Facts(groupPurpose: string.Empty))));

        var suggestion = await capability.EvaluateAsync(Context(joinedDaysAgo: 2));
        Assert.NotNull(suggestion);
        Assert.Contains("Nick Tran", suggestion!.Text);
        Assert.Contains("giao lưu vui vẻ", suggestion.Text);
    }

    [Fact]
    public async Task Role_introduction_requires_provider_roles_and_limits_admin_mentions_to_two()
    {
        var capability = new ZaloMemberRoleIntroductionCapability();

        Assert.Null(await capability.EvaluateAsync(Context(joinedDaysAgo: 2, roles: new(null, []))));

        var roles = new ZaloMemberIntegrationRoleDirectory(
            new ZaloMemberIntegrationMentionTarget("creator", "Trưởng nhóm"),
            [
                new ZaloMemberIntegrationMentionTarget("admin-1", "Phó 1"),
                new ZaloMemberIntegrationMentionTarget("admin-2", "Phó 2"),
                new ZaloMemberIntegrationMentionTarget("admin-3", "Phó 3")
            ]);
        var suggestion = await capability.EvaluateAsync(Context(joinedDaysAgo: 2, roles: roles));

        Assert.NotNull(suggestion);
        Assert.Equal(4, suggestion!.MentionTargets.Count);
        Assert.Contains(suggestion.MentionTargets, target => target.ZaloUserId == "creator");
        Assert.Contains(suggestion.MentionTargets, target => target.ZaloUserId == "admin-1");
        Assert.Contains(suggestion.MentionTargets, target => target.ZaloUserId == "admin-2");
        Assert.DoesNotContain(suggestion.MentionTargets, target => target.ZaloUserId == "admin-3");
    }

    [Fact]
    public async Task Group_culture_requires_three_days_and_configured_culture_summary()
    {
        var capability = new ZaloMemberGroupCultureCapability();

        Assert.Null(await capability.EvaluateAsync(Context(joinedDaysAgo: 2)));
        Assert.Null(await capability.EvaluateAsync(Context(
            joinedDaysAgo: 3,
            facts: Facts(cultureSummary: string.Empty))));

        var suggestion = await capability.EvaluateAsync(Context(joinedDaysAgo: 3));
        Assert.NotNull(suggestion);
        Assert.Contains("không toxic, không blame", suggestion!.Text);
        Assert.Equal("new_member_culture_orientation", suggestion.Reason);
    }

    [Fact]
    public async Task Practical_guide_only_emits_grounded_fee_or_capacity_facts()
    {
        var capability = new ZaloMemberPracticalGuideCapability();

        Assert.Null(await capability.EvaluateAsync(Context(
            joinedDaysAgo: 3,
            facts: Facts(normalCourtFeeVnd: null, maxPlayersPerSession: null))));

        var suggestion = await capability.EvaluateAsync(Context(
            joinedDaysAgo: 3,
            facts: Facts(normalCourtFeeVnd: 45000, maxPlayersPerSession: 12)));

        Assert.NotNull(suggestion);
        Assert.Contains("45k/người", suggestion!.Text);
        Assert.Contains("12 người", suggestion.Text);
        Assert.Equal("grounded_group_practical_facts", suggestion.Reason);
    }

    [Fact]
    public async Task Social_bridge_stays_silent_after_three_human_interactions()
    {
        var capability = new ZaloMemberSocialBridgeCapability();
        var signals = new ZaloMemberIntegrationSignals(DistinctHumanInteractionCount: 3);

        var suggestion = await capability.EvaluateAsync(Context(
            joinedDaysAgo: 4,
            signals: signals,
            peers: [new ZaloMemberIntegrationPeer("peer", "Peer", 10, 10, Snapshot.AddHours(-1))]));

        Assert.Null(suggestion);
    }

    [Fact]
    public async Task Social_bridge_selects_most_active_peer_and_mentions_both_people()
    {
        var capability = new ZaloMemberSocialBridgeCapability();
        var peers = new[]
        {
            new ZaloMemberIntegrationPeer("chatty", "Chatty", MessageCount: 6, VoteCount: 0, LastActivityAt: Snapshot.AddHours(-1)),
            new ZaloMemberIntegrationPeer("voter", "Voter", MessageCount: 1, VoteCount: 3, LastActivityAt: Snapshot.AddHours(-2))
        };

        var suggestion = await capability.EvaluateAsync(Context(joinedDaysAgo: 4, peers: peers));

        Assert.NotNull(suggestion);
        Assert.Equal("member-integration:social-bridge:voter", suggestion!.ContentKey);
        Assert.Equal(new[] { "user-1", "voter" }, suggestion.MentionTargets.Select(target => target.ZaloUserId));
    }

    [Fact]
    public async Task Group_lore_skips_sensitive_approved_lore_and_uses_next_safe_entry()
    {
        var capability = new ZaloMemberGroupLoreCapability();
        var facts = Facts(approvedLore: ["chính trị của thành viên", "Cả hội hay gọi quả cứu bóng là pha hồi sinh"]);

        var suggestion = await capability.EvaluateAsync(Context(joinedDaysAgo: 5, facts: facts));

        Assert.NotNull(suggestion);
        Assert.Equal("approved_group_lore", suggestion!.Reason);
        Assert.DoesNotContain("chính trị", suggestion.Text);
        Assert.Contains("pha hồi sinh", suggestion.Text);
    }

    [Fact]
    public async Task Group_lore_falls_back_to_activity_backed_hint_with_its_mentions()
    {
        var capability = new ZaloMemberGroupLoreCapability();
        var peerMention = new ZaloMemberIntegrationMentionTarget("peer-1", "Peer One");
        var hint = new ZaloMemberIntegrationLoreHint("regular-voter", "@Peer One vote khá đều", [peerMention]);
        var facts = Facts(approvedLore: ["sức khỏe riêng tư"]);

        var suggestion = await capability.EvaluateAsync(Context(
            joinedDaysAgo: 5,
            facts: facts,
            loreHints: [hint]));

        Assert.NotNull(suggestion);
        Assert.Equal("safe_activity_backed_lore", suggestion!.Reason);
        Assert.Equal("member-integration:lore:regular-voter", suggestion.ContentKey);
        Assert.Equal(new[] { "user-1", "peer-1" }, suggestion.MentionTargets.Select(target => target.ZaloUserId));
    }

    [Fact]
    public void Integration_score_calculation_uses_all_signals_and_caps_at_one_hundred()
    {
        Assert.Equal(0, ZaloMemberIntegrationScoreCapability.Calculate(new ZaloMemberIntegrationSignals()));

        var integrated = new ZaloMemberIntegrationSignals(
            MessageCount: 10,
            ActiveMessageDays: 4,
            VoteCount: 5,
            DistinctHumanInteractionCount: 3,
            HasSpoken: true,
            HasRepliedToMember: true,
            HasBeenRepliedTo: true);

        Assert.Equal(100, ZaloMemberIntegrationScoreCapability.Calculate(integrated));
    }

    [Theory]
    [InlineData(2, false, "quiet")]
    [InlineData(0, true, "warming")]
    public async Task Integration_score_emits_expected_content_band(int voteCount, bool warmingSignals, string expectedBand)
    {
        var capability = new ZaloMemberIntegrationScoreCapability();
        var signals = warmingSignals
            ? new ZaloMemberIntegrationSignals(MessageCount: 2, ActiveMessageDays: 3, HasSpoken: true)
            : new ZaloMemberIntegrationSignals(VoteCount: voteCount);

        Assert.Null(await capability.EvaluateAsync(Context(joinedDaysAgo: 6, signals: signals)));

        var suggestion = await capability.EvaluateAsync(Context(joinedDaysAgo: 7, signals: signals));
        Assert.NotNull(suggestion);
        Assert.Equal($"member-integration:score:{expectedBand}", suggestion!.ContentKey);
        Assert.Equal("integration_signals", suggestion.Reason);
    }

    private static readonly DateTimeOffset Snapshot = DateTimeOffset.Parse("2026-10-25T14:00:00+07:00");

    private static ZaloMemberIntegrationContext Context(
        int? joinedDaysAgo,
        bool isRejoin = false,
        ZaloMemberIntegrationSignals? signals = null,
        ZaloMemberIntegrationGroupFacts? facts = null,
        ZaloMemberIntegrationRoleDirectory? roles = null,
        IReadOnlyList<ZaloMemberIntegrationPeer>? peers = null,
        IReadOnlyList<ZaloMemberIntegrationLoreHint>? loreHints = null,
        int? integrationStartedDaysAgo = null,
        bool isRecoveredNewcomer = false) =>
        new(
            ZaloConnectionId: "conn-1",
            AccountId: "account-1",
            GroupId: "group-1",
            ZaloUserId: "user-1",
            DisplayName: "New Member",
            JoinedAt: joinedDaysAgo is null ? null : Snapshot.AddDays(-joinedDaysAgo.Value),
            IsRejoin: isRejoin,
            SnapshotAt: Snapshot,
            Signals: signals ?? new ZaloMemberIntegrationSignals(),
            Facts: facts ?? Facts(),
            Roles: roles ?? new ZaloMemberIntegrationRoleDirectory(null, []),
            PeerCandidates: peers ?? [],
            LoreHints: loreHints ?? [],
            IntegrationStartedAt: integrationStartedDaysAgo is null
                ? null
                : Snapshot.AddDays(-integrationStartedDaysAgo.Value),
            IsRecoveredNewcomer: isRecoveredNewcomer);

    private static ZaloMemberIntegrationGroupFacts Facts(
        string groupPurpose = "giao lưu vui vẻ, không phụ thu",
        string cultureSummary = "vui vẻ, hòa đồng, không toxic, không blame.",
        int? normalCourtFeeVnd = 50000,
        int? maxPlayersPerSession = 12,
        IReadOnlyList<string>? approvedLore = null) =>
        new(
            GroupName: "Volley Draft",
            BotCreatorName: "Thanh Long",
            GroupFounderName: "Nick Tran",
            GroupPurpose: groupPurpose,
            CultureSummary: cultureSummary,
            NormalCourtFeeVnd: normalCourtFeeVnd,
            MaxPlayersPerSession: maxPlayersPerSession,
            ApprovedLore: approvedLore ?? []);
}
