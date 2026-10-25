# Zalo Member Integration

`MemberIntegration` is the umbrella feature for helping a new or returning Zalo member become part of the community instead of remaining a poll-only participant.

The feature is intentionally modeled as a coordinator plus small capabilities. It is not a base-class hierarchy. Each capability observes the same grounded member context and proposes content; the existing proactive layer remains responsible for timing, cooldown, rotation, idempotency, mentions and the actual Zalo send.

## Feature boundary

The parent feature owns the member-integration journey and shared signals. Smaller capabilities can be added independently, for example:

- `Welcome`: acknowledge a verified new join or rejoin without repeating it too often.
- `BotIdentity`: explain who the NPC is and why it exists.
- `GroupOrigins`: explain who created the group and its community purpose.
- `RoleIntroduction`: introduce current group creator/admins or configured leaders.
- `GroupCulture`: reinforce friendly, non-toxic, non-blaming community norms.
- `PracticalGuide`: surface grounded facts such as normal court cost, vote/capacity rules and other stable group conventions.
- `SocialBridge`: connect a quiet newcomer with suitable established members and create an easy reason to speak.
- `GroupLore`: use approved lore or safe, evidence-backed historical callbacks to make the group feel lived-in without inventing personal facts.
- `IntegrationScore`: summarize behavioral signals that indicate whether the member still needs onboarding help.

This list is a roadmap, not a requirement that all capabilities ship together.

## Grounding rules

Verified membership events remain authoritative for real join time. `FirstSeenAt` from directory sync must not be presented as the actual join date when no provider join event exists.

Community facts should come from one of three sources:

1. configured group facts for stable information such as purpose, leaders, costs and rules;
2. deterministic observations from poll/message/membership history for behavior such as participation frequency or interaction patterns;
3. explicitly approved lore/profile facts for recurring jokes or personal descriptions.

Historical chat can help derive harmless participation patterns, but inferred sensitive personal traits must not be promoted into member lore.

## Integration signals

`ZaloMemberIntegrationSignals` is deliberately broader than vote count. It can carry message count, active message days, distinct human interactions, whether the member has spoken, replied to another member, received a reply, and recent activity.

The goal is to distinguish cases such as:

- votes often but never speaks;
- speaks once but nobody has interacted back;
- already chats with several people across multiple days;
- returning member who does not need first-time onboarding.

No single signal permanently labels a member. Capabilities evaluate the current context at the time a proactive slot is considered.

## Execution model

`ZaloMemberIntegrationCoordinator` evaluates registered `IZaloMemberIntegrationCapability` implementations in deterministic priority order and returns suggestions. It does not send messages itself.

The intended integration point is the existing proactive/community execution lane. That lane already owns group quiet-time checks, daily slots, cooldowns, send leases, durable history and Bridge delivery. Keeping those responsibilities outside `MemberIntegration` prevents each small capability from implementing its own scheduler or anti-spam logic.

This also gives the feature a stable parent boundary for future work: adding one small capability should normally mean adding one implementation and registering it, without expanding a monolithic service or introducing inheritance between unrelated behaviors.

## Current implementation

The first complete capability set is now wired into the existing `Community/STT` lane:

- `welcome` uses only verified join/rejoin evidence;
- `bot-identity` introduces the NPC and its configured creator;
- `group-origins` explains the configured founder and non-profit community purpose;
- `role-introduction` uses live Zalo creator/admin roles when Bridge can resolve them;
- `group-culture` reinforces configured community norms;
- `practical-guide` surfaces configured normal court cost and capacity guidance;
- `social-bridge` links a newcomer with an established active member when human interaction is still low;
- `group-lore` uses approved lore first, otherwise harmless activity-backed observations from message/poll history;
- `integration-score` uses chat days, messages, votes and reply interactions to decide whether to encourage more conversation or recognize successful integration. The numeric score is never published to the group.

Only one highest-priority eligible capability is proposed for a member at a time. Durable proactive history spaces messages for the same UID and prevents the same join welcome or capability from being repeated too frequently. Real Zalo mentions are built only after final text selection so mention offsets remain correct.
