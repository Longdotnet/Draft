namespace VolleyDraft.Api.Services;

/// <summary>
/// Stable umbrella for the member-integration feature family.
/// Small capabilities plug into this feature instead of growing one large onboarding service.
/// </summary>
public static class ZaloMemberIntegrationFeature
{
    public const string FeatureKey = "member-integration";
}

/// <summary>
/// Activity signals used to decide how much social help a member still needs.
/// These are observations, not a permanent label on the person.
/// </summary>
public sealed record ZaloMemberIntegrationSignals(
    int MessageCount = 0,
    int ActiveMessageDays = 0,
    int VoteCount = 0,
    int DistinctHumanInteractionCount = 0,
    bool HasSpoken = false,
    bool HasRepliedToMember = false,
    bool HasBeenRepliedTo = false,
    DateTimeOffset? LastActivityAt = null);

public sealed record ZaloMemberIntegrationGroupFacts(
    string GroupName,
    string BotCreatorName,
    string GroupFounderName,
    string GroupPurpose,
    string CultureSummary,
    int? NormalCourtFeeVnd,
    int? MaxPlayersPerSession,
    IReadOnlyList<string> ApprovedLore);

public sealed record ZaloMemberIntegrationRoleDirectory(
    ZaloMemberIntegrationMentionTarget? Creator,
    IReadOnlyList<ZaloMemberIntegrationMentionTarget> Admins);

public sealed record ZaloMemberIntegrationPeer(
    string ZaloUserId,
    string DisplayName,
    int MessageCount,
    int VoteCount,
    DateTimeOffset? LastActivityAt);

public sealed record ZaloMemberIntegrationLoreHint(
    string Key,
    string Text,
    IReadOnlyList<ZaloMemberIntegrationMentionTarget> MentionTargets);

/// <summary>
/// Authoritative scope passed to every integration capability.
/// JoinedAt is nullable because directory observation alone does not prove a real join time.
/// </summary>
public sealed record ZaloMemberIntegrationContext(
    string ZaloConnectionId,
    string AccountId,
    string GroupId,
    string ZaloUserId,
    string DisplayName,
    DateTimeOffset? JoinedAt,
    bool IsRejoin,
    DateTimeOffset SnapshotAt,
    ZaloMemberIntegrationSignals Signals,
    ZaloMemberIntegrationGroupFacts Facts,
    ZaloMemberIntegrationRoleDirectory Roles,
    IReadOnlyList<ZaloMemberIntegrationPeer> PeerCandidates,
    IReadOnlyList<ZaloMemberIntegrationLoreHint> LoreHints);

/// <summary>
/// A person a capability wants to mention when its suggestion is eventually rendered.
/// Mention offsets are deliberately built only after the final message text is selected.
/// </summary>
public sealed record ZaloMemberIntegrationMentionTarget(
    string ZaloUserId,
    string DisplayName);

/// <summary>
/// A capability proposes content; it does not send directly to Zalo.
/// The outer proactive policy remains responsible for cooldown, timing, rotation and send leases.
/// </summary>
public sealed record ZaloMemberIntegrationSuggestion(
    string CapabilityKey,
    int Priority,
    string Text,
    string ContentKey,
    string Reason,
    IReadOnlyList<ZaloMemberIntegrationMentionTarget> MentionTargets);

/// <summary>
/// Extension point for a small feature inside MemberIntegration.
/// Examples: welcome, group culture, role introduction, practical guide, social bridge,
/// group lore and integration scoring.
/// </summary>
public interface IZaloMemberIntegrationCapability
{
    string Key { get; }

    int Priority { get; }

    ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record ZaloMemberIntegrationPlan(
    ZaloMemberIntegrationContext Context,
    IReadOnlyList<ZaloMemberIntegrationSuggestion> Suggestions);
