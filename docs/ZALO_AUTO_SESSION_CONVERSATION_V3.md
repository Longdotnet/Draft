# Zalo Auto Session Conversation V3

## Goal

The organizer should not need to memorize bot commands.

When a current Zalo group creator/admin creates a volleyball signup poll, Auto Session sends a website preview and starts a stateful conversation. The organizer can answer naturally (`T6 thôi`, `à thêm CN`, `6h nha`, `sân A`, `ok`, `tạo đi`) and the bot maintains a draft until a safe final confirmation is reached.

AI is interpretation-only. It never authorizes a database write.

## What the user sees

The bot intentionally does **not** expose internal MatchSession metadata such as `TotalSets`.

Example:

```text
@Long tui hiểu poll "Lịch tuần này" là lịch bóng chuyền.

PREVIEW WEBSITE
• T6 21/08 17:30 — hiện 10/18 người
• CN 23/08 17:30 — hiện 9/18 người
• Địa điểm: Sân UTE

Website CHƯA được tạo.
Bạn cứ nói tự nhiên. Muốn tạo ở bước cuối, bấm Trả lời tin bot rồi nói "tạo đi".
```

`DefaultTotalSets=4` can remain an internal website default; it is not a decision the organizer needs to see in the Zalo conversation.

## Conversation state

```text
PreviewSent
  -> Discussing
  -> Clarifying
  -> ReadyToConfirm
  -> Executing
  -> Created

Terminal:
Cancelled / Expired / Superseded / Failed
```

Each poll has a persistent conversation with:
- proposal/poll/tracked-group identity
- original organizer and current active organizer
- initial draft and current draft
- preview/current bot message IDs
- last question/intent
- optimistic `Version`
- reminder count
- expiry / follow-up timestamps
- turn audit

## Natural messages

Deterministic rules handle common Vietnamese replies first:
- `T6 thôi`
- `T6 CN`
- `à thêm CN`
- `bỏ T4`
- `cái cuối bỏ đi`
- `2 cái sau`
- `T6 6h`
- `6h nha` (uses context when one day is selected)
- `sân A`
- `sân cũ`
- `làm lại từ đầu`
- `tạo đi`
- `ok` / `ừ` only at the final confirmation state
- `bỏ qua`, `không tạo`, `hủy`

If rules cannot understand a message, configured AI can return a structured interpretation. AI output can update/clarify the draft, but `ExplicitExecute` is always forced to `false`.

## Ambiguity

The bot asks only for the missing decision instead of rejecting the message.

Examples:

```text
Admin: làm 2 cái đi
Bot: Bạn muốn 2 lịch nào: T4 + T6, T4 + CN, hay T6 + CN?
```

```text
Admin: 6h nha
Bot: Bạn muốn 18:00 cho ngày nào: T6, CN?
Admin: T6
```

The pending time is carried across the clarification turn, so answering only `T6` does not lose the intended `18:00`.

## Implicit short follow-ups

For usability, if there is exactly one active conversation and the same organizer types a short action-like message within 3 minutes after the bot (without quote/@bot), V3 may use it to update the **draft**.

It explicitly rejects obvious chatter such as:
- `T6 đông quá`
- `ai đi`
- `haha`

An implicitly associated message can never perform the final write. Final creation requires an explicitly addressed bot message (reply/quote or @bot).

### Late plain `tạo đi` recovery

If the preview/reminder has scrolled away and there is **exactly one** active Auto Session conversation in the group, the current active organizer may later type a narrow, unmistakable create phrase such as `tạo đi`, `tạo luôn`, `ok tạo đi`, or `xác nhận tạo` as a normal group message.

That message still **does not create the website**. V3 only uses it to resurface the current draft, mention the active organizer, move the conversation to `ReadyToConfirm`, and give them a fresh bot message to reply to. The organizer must then reply to that bot message (or explicitly @mention the bot) to perform the final write.

This recovery path is deliberately strict:
- it is available only to the current active organizer
- it is disabled when multiple active conversations make the target ambiguous
- vague group chatter such as `ok`, `ừ`, `chốt`, `triển`, `làm đi`, or messages that merely contain `tạo đi` inside a longer sentence do not qualify
- it never bypasses the normal final authorization and idempotency checks

## Final confirmation

Safe write rules:
- current runtime kill switch must be ON
- group rollout must still be `Live`
- tracked group must still be enabled
- sender must still be current group creator/admin
- poll must still match the preview structure
- at least one schedule option must remain selected
- conversation state must be `ReadyToConfirm`
- final confirmation must explicitly address the bot
- optimistic version claim must succeed
- existing `(TrackedGroupId, PollId, OptionId)` idempotency remains the second duplicate guard

A quote counts as explicit addressing **only when the quoted message belongs to this Auto Session bot conversation**. Quoting another member's message never upgrades an implicit group-chat message into an executable confirmation.

Examples:

```text
Admin: T6 thôi
Bot: [updated draft] Website CHƯA được tạo. Bấm Trả lời tin này rồi nói "tạo đi".
Admin replies: ừ
=> allowed because the bot was explicitly replied to and state is ReadyToConfirm
```

```text
Admin types plain group chat: ừ
=> no write
```

A clear direct reply such as `tạo T6 CN` is a deterministic explicit command and can use the fast path.

## Organizer forgets or never answers

No automatic creation occurs.

Default policy:
1. preview is sent to the poll creator
2. after 30 minutes: one reminder to the active/original organizer
3. after another 150 minutes: one escalation mentioning all current creator/admin accounts
4. any current creator/admin can reply to that escalation and take over the same draft
5. after 24 hours: conversation expires and proposal is closed without creating a website session

All timings are configurable:
- `AutoSession:ConversationFirstReminderMinutes`
- `AutoSession:ConversationEscalationDelayMinutes`
- `AutoSession:ConversationExpiryHours`

There are at most two follow-up messages per conversation.

## Multiple active polls

If an organizer @mentions the bot without quoting a message while multiple Auto Session conversations are active, V3 refuses to guess and asks them to reply to the preview/reminder belonging to the intended poll.

## Race safety

Two organizers can interact with the same conversation, but execution uses optimistic compare-and-swap:

```text
ReadyToConfirm version 5
  -> UPDATE ... WHERE State=ReadyToConfirm AND Version=5
  -> Executing version 6
```

Only one request can claim execution. A second concurrent confirmation does not execute again.

## Legacy compatibility

With `AutoSession:ConversationV3Enabled=true` (default), V2 continues to own poll discovery/classification/preview, but the old history-scanning confirmation path is disabled for Live proposals.

V3 uses a dedicated typed `ZaloAutoSessionActionExecutor`. It preserves the same idempotent session/link/roster/overbook creation sequence while keeping interpretation and authorization separate from writes.

Set `AutoSession:ConversationV3Enabled=false` to fall back to the old exact-preview reply confirmation path.

The global Auto Session kill switch also stops Conversation V3 reconciliation/follow-ups; it does not keep reminding organizers while the feature is globally disabled.

## Waitlist

Conversation V3 does not add or promote a waitlist.

If someone removes a vote, the slot is simply open in the Zalo poll and whoever votes first can take it. Existing poll sync continues to mirror current voters to the website.

## Reminder reset rule

Whenever a current organizer responds, the silence/reminder cycle resets. A previous reminder must not cause immediate escalation to all organizers after the person has resumed the conversation. The follow-up flow is bounded and never creates a session by itself.
