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

## V1: Match Autopilot Center

The desktop app now puts `MatchAutopilotCenter` before the manual admin tools. It derives a conservative lifecycle view from existing authoritative APIs; it does not create a second source of business truth and it does not mutate anything.

The center refreshes every 30 seconds and classifies the latest admin sessions into these client-facing stages:

| Stage | Normal owner | Website? |
| --- | --- | --- |
| `NeedsSetup` | admin | yes, one-time configuration |
| `Recruiting` | Zalo/poll + existing recruiting lanes | no |
| `ResolvingOverbook` | overbook automation, or admin when evidence is ambiguous | only on exception |
| `AwaitingProfiles` | Zalo conversation | no |
| `ReadyForDraft` | authorized leader confirmation on Zalo | no |
| `Drafting` | current draft flow | no extra admin screen |
| `Drafted` | post-draft bot/domain flows | no |
| `Cancelled` | none | no |

Cards requiring web attention are sorted first. Normal cards explicitly say `KHÔNG CẦN WEBSITE` so an organizer does not open admin pages merely to check whether something changed.

## Exception-first navigation

When the center detects a condition that should not be guessed automatically, it links the operator directly to the relevant section:

- missing Zalo link -> Auto Session / Zalo setup;
- missing match time -> draft/session workspace;
- bot disabled -> bot/overbook control;
- ambiguous overbook target -> bot/overbook confirmation.

This is deliberately different from a generic `Open admin` link. The product should always tell the operator what is wrong and take them to the smallest surface capable of resolving it.

## Safety boundary

V1 does not grant AI or the dashboard any new mutation authority.

- poll/database remain authoritative;
- current authorization/confirmation gates remain authoritative;
- ambiguous overbook state remains a human exception;
- the dashboard tolerates a temporarily unreadable overbook endpoint and falls back to session/roster facts instead of presenting a false success;
- a client-side lifecycle label is UX guidance, never permission to mutate backend state.

## UX copy rules

Every lifecycle message should follow this order:

```text
FACT -> WHAT THE SYSTEM IS DOING -> WHAT THE HUMAN MUST DO
```

Good:

```text
T6 đang 17/18.
Nếu trưởng/phó đã chọn `kiếm thêm`, KeepRecruiting tiếp tục sync/nhắc.
Chưa cần mở website.
```

Good exception:

```text
T6 đang 19/18 nhưng thứ tự voter không đủ chắc để tự chọn người vượt slot.
Bot đã dừng trước mutation.
Admin cần xác nhận target dư slot tại khu vực Overbook.
```

Avoid vague copy such as `Vào web kiểm tra giúp tui` because it sends the operator hunting through unrelated screens.

## Next backend slices

V1 intentionally reuses existing automation instead of replacing it. Follow-up slices should remain incremental:

1. expose one authoritative backend lifecycle snapshot built from `ZaloDraftReadinessService`, active pass-slot risks, recruiting decision and draft state;
2. let reminder/KeepRecruiting/draft-autopilot lanes consume the same lifecycle snapshot so UI and Zalo wording cannot drift;
3. add targeted missing-profile collection that mentions/resolves the exact Zalo identities when safe;
4. regenerate and resend team-card automatically after a confirmed post-draft slot transfer;
5. persist exception codes and deep-link hints so the website can open the exact session/action directly.

The invariant is unchanged: orchestration may decide **which existing safe lane should run next**, but it does not bypass domain validation, authorization, idempotency, leases, or explicit confirmation requirements.
