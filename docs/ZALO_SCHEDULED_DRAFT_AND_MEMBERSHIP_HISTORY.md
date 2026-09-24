# Scheduled draft and verified membership history

This document describes the two opt-in Zalo workflows added in PR #420. Both use the existing group and session ownership model. AI can help recognize or phrase a question, but C# owns permissions, timestamps, draft readiness, query results and state changes.

## Scheduled draft

Scheduled draft is **disabled by default**, with policy stored per Zalo connection and group. A verified group operator can send `bật tự draft` to use 17:30 Vietnam time with one reminder at 17:00, `bật tự draft lúc 18h` to change the time, or `tắt tự draft` to disable it. For the next upcoming session, `hoãn draft đến 18h` defers its draft; `hôm nay không tự draft` skips it. The bot acknowledges accepted decisions. Ordinary group members cannot change this policy.

The scheduler only considers a linked, bot-enabled, future match in setup/captain-selection state. A reminder gives the operator the opportunity to draft manually or change the schedule. When the scheduled time arrives, the service refreshes the linked poll, rebuilds canonical readiness, requires the configured full roster and complete profiles, verifies the roster fingerprint, and uses the existing `SessionDraftService` draft mutation. A partial roster continues to need the existing leader decision and manual draft path. Open pass-slot handoffs remain contextual, following current `ZaloDraftReadinessService` behavior.

Policy, per-session decisions and run progress are persisted in `ZaloScheduledDraftPolicies`, `ZaloScheduledDraftDecisions` and `ZaloScheduledDraftRuns`. The session ID is the durable uniqueness boundary for a scheduled run. Reminder completion and draft completion are separate. A successful draft is recorded before sending the result message, so a failed notification can be retried without running draft again. The scheduler also uses its existing distributed lease, while the shared session draft mutation remains the final ownership boundary.

If the host wakes after the expected reminder time, the service moves the draft time far enough forward to preserve the warning interval. If that would reach the match start, it skips automatic execution. An undelivered reminder never authorizes automatic execution. A configured target time is therefore conditional on the scheduler process actually running; persistent state cannot wake a sleeping host. Production scheduling should supply reliable external ticks and record wake-up latency.

## Verified group joins

The membership webhook receives `join`, `leave` and `remove_member` events from the existing Zalo bridge. An event is accepted only when its source account and group resolve unambiguously to one tracked connection, it has an event ID and a trustworthy provider timestamp. Membership periods are keyed by connection, group and stable user UID, preserving repeated join/leave cycles. Database uniqueness prevents simultaneous open periods for one identity and rejects duplicate source join events.

Directory sync may show a person for the first time without proving when they joined. Such records remain **ObservedOnly** with an unknown `JoinedAt`; historical `FirstSeenAt` must not be promoted to a verified join timestamp. A provider join event can supply a verified timestamp. The membership coverage ledger is distinct from message and poll coverage. Until complete historical membership coverage is actually proven, answers warn that older events or gaps may be missing.

High-confidence Vietnamese questions such as `7 ngày qua có ai mới vào?`, `những ai vào nhóm 45 ngày đổ lại?` and `tui vào nhóm ngày nào?` route to deterministic membership queries even when AI is unavailable. The default list describes current group members with a verified current join within the requested time window, labels verified rejoins and explains any unknown dates or incomplete coverage. Group-wide member lists retain Member Intelligence's existing operator/admin/deputy authorization; ordinary members may inspect their own join date. The backend computes the dates and counts.

## Verification and rollout

Regression checks should include a manual draft racing two scheduled workers, a reminder followed by restart, a draft followed by notification failure, a deferred/cancelled session racing the scheduler, poll/roster changes, duplicate and delayed provider events, rejoin cycles, provider gaps, cross-group access, and the exact 45-day Vietnamese phrasing with AI disabled. Before enabling the policy for a live group, validate the outbound reminder and draft-result delivery receipts, scheduler wake cadence and available source membership events with the group's operators.

Related: [Draft readiness](DRAFT_PREP_REMINDER_DECISION_MATRIX.md), [Member Intelligence](ZALO_MEMBER_INTELLIGENCE.md), [Modular architecture](ZALO_MODULAR_ARCHITECTURE.md).
