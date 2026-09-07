# Pass/share-slot guidance acceptance cases

These examples are product-level regressions for contextual help.

## Must answer deterministically

- `@Npc ai pass slot thì gõ sao?`
- `@Npc pass slot gõ như nào?`
- `@Npc hướng dẫn nhường suất đi`
- `@Npc cú pháp pass slot là gì?`
- `@Npc share slot dùng sao?`
- direct reply to an NPC message with `pass slot gõ sao?`

## Must keep existing owners

- `@Npc tui pass slot T6 nha` → actual pass/open-offer flow.
- `@Npc tui nhận slot T6` → actual claim flow.
- `@Npc ai đang pass slot?` → factual pass-slot query.
- `@Npc tui muốn share slot với @To An hôm nay` → ShareSlot mutation flow.
- unmentioned `ai pass slot thì gõ sao ta` → ordinary group chatter; contextual help does not wake NPC by itself.

## No-AI requirement

The contextual answer is built from deterministic product knowledge and must not depend on an AI provider. It teaches only syntax already owned by existing deterministic handlers and never mutates authoritative state itself.
