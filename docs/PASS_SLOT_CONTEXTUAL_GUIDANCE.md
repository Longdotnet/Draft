# Pass/share-slot contextual guidance

NPC owns beginner help for pass/share-slot workflows deterministically when the user explicitly addresses the bot or directly replies to a bot message.

## Product distinction

- **Pass/nhường slot**: the current owner gives up one participation so another member can take it.
- **Share slot**: multiple members intentionally share/alternate one team slot. It is not a pass.

## Natural discovery without AI

Beginner help must not require internal vocabulary such as `slot`. Addressed/reply-to-bot questions may describe the same intent through normalized concepts such as:

- `tui nghỉ trận này thì làm sao`;
- `em không đánh kèo này, phải làm gì`;
- `cho người khác đánh thì làm sao`;
- `nhường chỗ kiểu gì`.

The help lane still requires a help/question shape and must yield to real mutation/fact turns. Negated language such as `đừng pass slot` or `không nghỉ trận này` must not teach a pass mutation. Explicit `share slot` wording wins over nearby generic pass words so NPC never explains a destructive give-away flow for a sharing request.

## Supported pass flow without AI

1. Current owner: `pass slot T6` or `nhường suất CN`.
2. Claimant: `tui nhận T6`; when exactly one offer is unambiguous, `tui nhận` is also supported.
3. Pre-draft: owner leaves the linked poll option, claimant votes it, then claimant says `xong`; NPC verifies authoritative roster state rather than editing the poll.
4. Post-draft: reserved claimant says `chốt`; the transfer service revalidates state before mutation.
5. Owner may cancel an unfinished offer with `huỷ pass`; claimant may release an unfinished reservation with `huỷ nhận`.
6. Admin/operator delegated transfer uses the existing deterministic shape `@Npc @A pass slot cho @B`; authorization, UID binding and session/state validation remain server-side. If session selection is ambiguous, NPC must clarify instead of guessing.

## Supported share flow without AI

- Self-service: `@Npc tui muốn share slot với @To An T6`.
- Delegated/operator form: `@Npc @A muốn share slot với @B T6`.

Explicit mentions are bound to stable Zalo UIDs by the existing ShareSlot pipeline and authoritative roster/session checks still own mutation.

## Routing invariant

Contextual help is informational only. It must not open an offer, reserve a claimant, alter poll/roster/team state, or call AI. Real mutation/fact queries must keep their existing owners. Ordinary unmentioned group chatter must not wake this help lane.
