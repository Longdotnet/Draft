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

If the Zalo listener was offline and missed a real join, an admin may explicitly confirm that a currently observed member is a newcomer. Those confirmations are stored separately in `ZaloRecoveredNewcomers`. They participate in onboarding, but they do not mutate `ZaloGroupMembershipPeriods`, do not convert `FirstSeenAt` into a fake join timestamp, and are ignored whenever a verified provider join/rejoin exists for the same member.

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

The first complete capability set is delivered through the dedicated `MemberIntegration` proactive lane:

- `welcome` uses only verified join/rejoin evidence;
- `bot-identity` introduces the NPC and its configured creator;
- `group-origins` explains the configured founder and non-profit community purpose;
- `role-introduction` uses live Zalo creator/admin roles when Bridge can resolve them;
- `group-culture` reinforces configured community norms;
- `practical-guide` surfaces configured normal court cost and capacity guidance;
- `social-bridge` links a newcomer with an established active member when human interaction is still low;
- `group-lore` uses approved lore first, otherwise harmless activity-backed observations from message/poll history;
- `integration-score` uses chat days, messages, votes and reply interactions to decide whether to encourage more conversation or recognize successful integration. The numeric score is never published to the group.

### Delivery lane

`MemberIntegration` now has its own proactive delivery lane and no longer consumes `Community/STT` daily slots. The lane checks frequently but only sends when the group is quiet. A newcomer who has recently produced a real group activity signal such as a realtime message can be approached after a shorter quiet period; a newcomer without a recent signal requires a much longer quiet period. The lane has its own per-member spacing, group interval, daily safety cap and durable history.

The Zalo Bridge now forwards the realtime `zca-js` group `seen_messages` and `typing` listener events into `/api/internal/zalo/presence-events`. `seen` is treated as the strongest available presence signal because it demonstrates that Zalo reported the member among the viewers of a group message. `typing` is recorded as activity but the delivery policy deliberately avoids nudging somebody in the first few minutes after a typing event so the bot does not interrupt a message they are already composing. `delivered_messages` is not used as an online signal because delivery to a device does not prove that the member is actively viewing Zalo.

This still does not claim a durable Zalo "online" state. The stored rows are observation facts (`seen` / `typing`, observed timestamp, optional provider timestamp/message id), and MemberIntegration uses only recent observations. A fresh `seen` can reduce the required group-quiet window to 10 minutes; ordinary recent message/poll activity uses the 20-minute window and members without recent activity still require the longer passive quiet window.

Only one highest-priority eligible capability is proposed for a member at a time. Durable proactive history spaces messages for the same UID and prevents the same join welcome or capability from being repeated too frequently. Real Zalo mentions are built only after final text selection so mention offsets remain correct.

### Lifecycle and graduation

The 28-day newcomer window is a discovery/history window, not a promise to keep onboarding somebody for 28 days.

For a first-time or recovered newcomer, the delivery cadence now tapers with age:

- day 0-2: target up to 2 MemberIntegration touches per local day, with at least 3 hours between touches for the same member;
- day 3-5: at most 1 touch per local day;
- day 6-7: at most 1 touch every 2 days;
- from day 8: stop onboarding if the member still has not integrated. This is a passive stop, not a claim that the member successfully integrated.

A first-time or recovered newcomer is considered established only after at least 2 days and only when the observed behavior supports it: integration score at least 55, plus either 2 active chat days or 2 distinct human interactions, plus either at least 3 human messages or 2 poll participations.

Returning members use a lighter path. A rejoin graduates after one distinct human interaction, or after at least two meaningful message/vote actions. If that does not happen within 3 days, returning-member onboarding stops without emitting a fake success message.

Successful integration emits one terminal MemberIntegration notice for the current membership period:

- first-time/recovered: the public badge is **ĐÃ NHẬP HỘI**;
- rejoin: the public badge is **ĐÃ TRỞ LẠI ĐỘI HÌNH**.

The message explains naturally that the member has caught the group's rhythm and that the bot will stop newcomer-style guidance. Numeric scores and threshold details remain internal. The terminal notice is keyed to the current join/rejoin/integration anchor, so it is durable and sent once per membership period; a future genuine rejoin gets a new period and is not incorrectly blocked by an old graduation record.
