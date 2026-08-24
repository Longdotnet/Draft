# Match Lifecycle Autopilot

## Product goal

Normal weekly volleyball operation should happen in Zalo. The website is an exception/control room, not a checklist the organizer must revisit for every match.

The client-facing contract is:

```text
Poll appears
  -> Auto Session / poll sync
  -> roster recruiting and guest/waitlist lanes
  -> profile completion
  -> draft readiness + authorized confirmation
  -> draft/team card
  -> post-draft slot changes
```

At every stage the product should be able to answer three questions without asking the organizer to understand backend subsystems:

1. What state is this match in?
2. What will the bot/system do next?
3. Does a human actually need to open the website?

## V1: authoritative lifecycle + Match Autopilot Center

V1 adds a read-only `MatchLifecycleCoordinator` on the backend and puts `MatchAutopilotCenter` before the manual desktop admin tools.

For Zalo-linked sessions, the existing overbook status response now carries the lifecycle snapshot produced by the coordinator. The client prefers this backend snapshot. When the live Zalo/poll status cannot be read, or before a session is linked to Zalo, the client may render a deliberately conservative fallback card. Fallback state is UX guidance only and never grants mutation authority.

The coordinator combines existing backend truth instead of inventing a parallel workflow:

- `ZaloDraftReadinessService` for effective slots, profile blockers and draft readiness;
- `ZaloDraftPreparationDecisionStore` for `KeepRecruiting`, exact-fingerprint `PlayCurrentRoster`, and `StopMatch`;
- durable open-slot offers for active pass/claim risk;
- stored overbook state for automatic handling vs ambiguous target confirmation;
- `MatchSession` draft/status fields for setup, drafting, finished and cancelled states.

This fixes an important product ambiguity: `15/18` is not automatically `Recruiting`. Poll state is observation; leader intent decides whether the group keeps recruiting or intentionally plays the current roster. A valid `chốt 15` decision can therefore move to a separately-confirmed draft path when the current fingerprint is unchanged and the engine can divide the effective slots evenly.

The center refreshes every 30 seconds and supports these client-facing stages:

| Stage | Normal owner | Website? |
| --- | --- | --- |
| `NeedsSetup` | admin | yes, one-time configuration |
| `AwaitingLeaderDecision` | authorized leader | no; decide in Zalo |
| `Recruiting` | Zalo/poll + existing recruiting lanes | no |
| `ResolvingOverbook` | overbook automation, or admin when evidence is ambiguous | only on exception |
| `ResolvingPassSlots` | pass-slot/rescue flow | no |
| `AwaitingProfiles` | Zalo conversation | no |
| `ReadyForDraft` | authorized leader confirmation on Zalo | no |
| `Drafting` | current draft flow | no extra admin screen |
| `Drafted` | post-draft bot/domain flows | no |
| `Stopped` | leader decision | no irreversible delete implied |
| `Cancelled` | none | no |
| `NeedsAttention` | admin | yes, fail closed |

Cards requiring web attention are sorted first. Normal cards explicitly say `KHÔNG CẦN WEBSITE` so an organizer does not open admin pages merely to check whether something changed.

## Exception-first navigation

When the center detects a condition that should not be guessed automatically, it links the operator directly to the relevant section:

- missing Zalo link -> Auto Session / Zalo setup;
- missing match time -> draft/session workspace;
- bot disabled -> bot/overbook control;
- ambiguous overbook target -> bot/overbook confirmation.

This is deliberately different from a generic `Open admin` link. The product should always tell the operator what is wrong and take them to the smallest surface capable of resolving it.

## Safety boundary

V1 does not grant AI, the coordinator or the dashboard any new mutation authority.

- poll/database remain authoritative;
- current authorization/confirmation gates remain authoritative;
- `PlayCurrentRoster` is accepted only for its exact stored roster fingerprint/effective slot count;
- active pass-slot offers block a clean draft state;
- ambiguous overbook target selection remains a human exception;
- partial-roster state never implies cancellation and never silently implies `KeepRecruiting`;
- a partial roster still needs the separate `draft đi` confirmation before draft mutation;
- final draft execution continues to re-sync and validate through the existing domain path;
- the dashboard tolerates a temporarily unreadable live overbook endpoint and falls back conservatively instead of presenting fake certainty.

## UX copy rules

Every lifecycle message should follow this order:

```text
FACT -> WHAT THE SYSTEM IS DOING -> WHAT THE HUMAN MUST DO
```

Good:

```text
T6 đang 17/18.
Trưởng/phó chưa chốt hướng.
Chọn `kiếm thêm` hoặc chốt roster hiện tại ngay trên Zalo; chưa cần mở website.
```

Good automatic state:

```text
T6 đang 17/18 và trưởng/phó đã chọn `kiếm thêm`.
KeepRecruiting tiếp tục sync/nhắc.
Chưa cần mở website.
```

Good exception:

```text
T6 đang 19/18 nhưng thứ tự voter không đủ chắc để tự chọn người vượt slot.
Bot đã dừng trước mutation.
Admin cần xác nhận target dư slot tại khu vực Overbook.
```

Avoid vague copy such as `Vào web kiểm tra giúp tui` because it sends the operator hunting through unrelated screens.

## Next slices

V1 intentionally reuses existing automation instead of replacing it. Follow-up slices should remain incremental:

1. let reminder/KeepRecruiting/draft-autopilot lanes consume the same lifecycle snapshot so UI and Zalo wording cannot drift;
2. add targeted missing-profile collection that mentions/resolves the exact Zalo identities when safe;
3. regenerate and resend team-card automatically after a confirmed post-draft slot transfer;
4. persist richer exception/deep-link context so the website can open the exact session/action directly;
5. add lifecycle transition/eval coverage from real production conversation cases.

The invariant is unchanged: orchestration may decide **which existing safe lane should run next**, but it does not bypass domain validation, authorization, idempotency, leases, or explicit confirmation requirements.
