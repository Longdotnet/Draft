# Draft Preparation Reminder — Decision Matrix

Status: design gate for PR #83. Do not treat numeric shortage as a cancellation decision.

## Core rule

Poll state is observation. Match continuation/cancellation is a leader decision.

The bot must never infer `cancel court`, `cancel match`, or `do not play` from `effectiveSlots < configuredCapacity` alone.

The bot must distinguish:

- `huỷ slot` / `pass slot`: one member is giving up a slot.
- `huỷ kèo` / `huỷ sân` / `nay nghỉ`: potential match-level decision.
- `15 vẫn đánh` / `chốt 15`: leader intends to play the current partial roster.
- `kiếm thêm` / `chờ thêm người`: leader intends to keep recruiting.
- `draft đi`: destructive/visible team-allocation confirmation, not the same thing as deciding the match can run short.

## What the current engine can actually draft

The project currently supports three teams. `SessionDraftService` accepts an effective slot count only when:

1. `effectiveSlots >= teamCount * 2`, and
2. `effectiveSlots % teamCount == 0`.

For three teams, directly draftable effective counts include:

- 6 -> 3 x 2
- 9 -> 3 x 3
- 12 -> 3 x 4
- 15 -> 3 x 5
- 18 -> 3 x 6

Counts such as 16 or 17 people can still be a real-world playable match, but the current equal-team auto-draft cannot represent 16/17 effective slots directly. Shared/rotation slots may reduce the effective count to a divisible value (for example 16 real players with one two-person shared slot -> 15 effective slots).

Therefore the bot must never say `16/18 cannot play`; it may only say `auto-draft cannot currently split 16 effective slots evenly across 3 teams`.

## Decision state

Leader decisions should be stored separately from poll/readiness state.

Recommended decision enum:

- `Undecided`
- `KeepRecruiting`
- `PlayCurrentRoster`
- `StopMatch`

A `PlayCurrentRoster` decision must be bound to:

- connection + group + session
- exact roster/share fingerprint
- exact effective slot count
- actor Zalo user id
- actor live Zalo role at decision time
- source message id / quoted reminder id when available
- timestamp + expiry no later than the match start

If roster/share membership changes, the decision is stale automatically. Example: leader says `chốt 15`, then poll becomes 16 -> the previous `chốt 15` must not authorize draft.

## Two-step partial-roster flow

Partial roster must not turn one conversational sentence into an immediate draft.

Example for 15/18:

1. Bot fresh-syncs the linked poll and sees 15 effective slots.
2. Bot reports: `15/18; 15 is technically draftable as 3 team x5. Kèo mình vẫn chạy 15 hay tiếp tục kiếm?`
3. A live authorized leader replies `15 vẫn đánh` / `chốt 15`.
4. Bot stores `PlayCurrentRoster` for the exact current fingerprint and replies: `Ok, chốt roster 15 -> 3 team x5. Nói draft đi để chạy; tui sẽ sync lại lần cuối.`
5. On `draft đi`, bot re-checks:
   - sender still authorized,
   - linked poll fresh-sync succeeds,
   - fingerprint still identical,
   - effective slot count is still the approved count,
   - no unresolved pass/open-slot state invalidates the roster,
   - profiles are complete,
   - slot count is supported by the draft engine.
6. Only then execute draft.

A direct `draft đi` on an undecided 15/18 roster must not silently imply `PlayCurrentRoster`; bot should ask whether the leader is intentionally locking the partial roster first.

## Reminder behavior by roster

### Full and clean

Example: 18/18, no unresolved pass slot, profiles complete.

- Can invite the authorized recipient to `draft đi`.
- Final execution still fresh-syncs and checks fingerprint.

### Partial but engine-compatible

Examples: 15/18, 12/18, 9/18.

- Never infer cancellation.
- Inform leader that current effective count can be divided evenly.
- Ask/observe leader decision: play current roster vs keep recruiting.
- Do not auto-draft until `PlayCurrentRoster` + separate draft confirmation exist.

### Partial and not engine-compatible

Examples: 16/18 or 17/18 effective slots.

- Never say the real-world match is impossible.
- Explain only that current auto-draft cannot split the effective slots evenly into 3 teams.
- Continue monitoring the poll.
- Shared/rotation-slot configuration may make the effective count draftable.
- A leader saying `vẫn chơi 16` should stop cancellation-style nudges, but must not cause unsupported auto-draft.

### Empty/very low roster

- Report facts and ask leader intent.
- Never auto-convert low attendance into `cancel match`.

### Open/pass/cancelled member slot

If a member says `pass slot` / `huỷ slot` but still exists in the linked poll:

- roster is considered at-risk,
- do not invite draft,
- poll remains source of truth,
- wait for replacement / poll update / offer resolution.

### Match-level cancellation language

`huỷ sân`, `huỷ kèo`, `nay nghỉ` must be handled separately from `huỷ slot`.

Recommended safety:

- only accept from a live authorized leader,
- require unambiguous session correlation when multiple sessions exist,
- first suppress draft reminders / mark leader intent,
- do not delete the session or perform an irreversible cancellation from an ambient sentence without an explicit confirmation path.

## Message correlation

Leader decisions should only be consumed when at least one is true:

1. the message replies/quotes the bot reminder for that session, or
2. the message explicitly names the session/day/date, or
3. there is exactly one relevant upcoming session in the group.

If multiple sessions are plausible, ask which session instead of guessing.

## Authorization

- Live Zalo creator/admin role is the authority source for roster decisions.
- Draft-tag web preference controls who the bot proactively mentions; it does not grant or revoke operational authority.
- A normal member saying `15 vẫn chơi` cannot lock the roster.
- Re-check live authority again at the final `draft đi` step.

## Ambiguous language

Do not mutate state for weak/ambiguous replies such as:

- `ok`
- `chốt`
- `ừ`
- `tùy`
- `để coi`

unless the system has a very narrow pending prompt where the exact meaning is deterministic. Prefer explicit phrases for state-changing decisions.

## Conflicting leaders

If two authorized leaders issue different decisions for the same current fingerprint:

- do not silently hide the conflict,
- latest explicit authorized decision may supersede the previous one,
- bot should acknowledge the change in-channel (for example `đã đổi từ kiếm thêm -> chốt 15 theo @X`),
- final draft still requires an explicit draft confirmation.

## Decision expiry / invalidation

Invalidate a partial-roster decision when any of these changes:

- linked poll id / selected option id
- roster/share fingerprint
- effective slot count
- session status
- start time passes
- decision actor is no longer a live authorized organizer (recommended fail-closed)

`KeepRecruiting` can remain conversational guidance, but must not authorize draft.

## Reminder anti-spam

Schedule may remain 12:00 -> 14:00 -> every 30 minutes from 16:00 until the stop window, but copy should reflect the current decision:

- `Undecided`: ask leader whether to keep recruiting or play current roster when relevant.
- `KeepRecruiting`: report delta (`15 -> 16`, `16 -> 15`) and remaining time; do not repeatedly ask `huỷ hay không` every 30 minutes.
- `PlayCurrentRoster`: if fingerprint unchanged, stop recruitment-pressure messages and only remind about pending draft confirmation when useful.
- `StopMatch`: stop draft-preparation reminders; do not perform irreversible deletion automatically.

## Cases that must have tests before PR is merge-ready

1. 18/18 clean -> normal draft confirmation.
2. 18/18 + member says pass but still voted -> no draft.
3. 18 -> 17 after unvote -> report drop, no cancellation inference.
4. 15/18 + no leader decision -> no draft, no cancellation inference.
5. 15/18 + member says `15 vẫn đánh` -> ignored as authority decision.
6. 15/18 + leader says `15 vẫn đánh` -> store exact-fingerprint PlayCurrentRoster; still no immediate draft.
7. 15 approved -> poll changes to 16 -> approval invalidated.
8. 15 approved -> leader says `draft đi`, poll remains 15, profiles complete -> draft 3 x 5.
9. 16/18 + leader says `vẫn chơi` -> acknowledge play intent but block auto-draft as 16 effective slots are not divisible by 3.
10. 16 real players + shared slot -> 15 effective -> partial-roster approval can become draftable.
11. 17/18 + generic `chốt` -> no decision mutation.
12. `huỷ slot` from member -> open-slot flow, not match cancellation.
13. `huỷ kèo` from ordinary member -> no match-level decision.
14. `huỷ kèo` from leader -> suppress reminders / require explicit cancellation confirmation for irreversible action.
15. two sessions in same group + `15 vẫn đánh` without quote/reference -> ask which session.
16. leader role removed after PlayCurrentRoster -> final draft fails closed.
17. Draft-tag opt-out leader can still explicitly make a roster decision / draft if their live role permits it.
18. poll sync failure before decision or draft -> no mutation.
19. anonymous/hidden-voter poll -> no fake `0/N`, no decision/draft mutation.
20. shared-slot change with unchanged raw player count -> fingerprint invalidates previous partial-roster decision.
