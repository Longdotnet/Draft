# Zalo Ambient Group Agent

The ambient agent is the next layer on top of Zalo Conversation Context V2. Its goal is to let the bot behave like a useful group participant without requiring `@bot` on every turn.

## Core rule

Ambient participation does **not** change the application's truth hierarchy:

1. authorization/security;
2. current application/database and Zalo poll data;
3. current message;
4. scoped memory/context;
5. AI inference.

A social statement such as `tui chắc đi T6` must never register a player. Registration remains poll/domain state.

## Rollout

### Phase 1 — Ambient Observer + Participation Engine (current)

Every ordinary message in a bot-enabled group may be observed before the legacy address gate. The observer:

- stores the realtime message as group context when it is not already present;
- builds a bounded recent `GroupSituation` from message IDs, participants, traffic and recent bot activity;
- runs deterministic participation scoring;
- classifies the candidate as `Fact`, `Social`, `Action` or `None`;
- writes an idempotent `AmbientShadow` trace containing routing metadata and IDs;
- **never sends an ambient reply**.

Explicit mentions and verified replies to the bot bypass ambient scoring and continue through the existing router.

Default production settings:

```json
{
  "ZaloBot": {
    "Ambient": {
      "Enabled": true,
      "ShadowMode": true,
      "WouldReplyThreshold": 65,
      "RecentWindowMinutes": 5,
      "MaxRecentMessages": 40,
      "BotCooldownSeconds": 20,
      "BusyGroupMessagesPerTwoMinutes": 8
    }
  }
}
```

`ShadowMode=true` is a hard rollout gate in phase 1. There is no outbound ambient send path in the observer service.

### Phase 2 — Fact Participant

After shadow precision is acceptable, allow only high-confidence `Fact` candidates to call existing read-only domain handlers. Examples:

- `T6 đủ chưa?`
- `Hải vote chưa?`
- `T6 còn mấy slot?`
- `mai đánh sân nào?`

The response must be rendered from current DB/poll facts. Ambient mode must not execute mutations such as draft, redraft, add guest, slot transfer, waitlist join/leave, profile edits or reminder changes.

### Phase 3 — Social Participant

Add a separate AI response mode for social conversation. The AI may generate short group-style banter, but it cannot choose or execute domain mutations and cannot turn jokes, guesses or third-party statements into memory/business facts.

### Phase 4 — Domain Event Reactions

Allow bounded proactive reactions to authoritative events such as:

- session reaches capacity;
- a voter withdraws and creates a vacancy;
- waitlist promotion;
- draft completion.

Event reactions must be idempotent and respect group cooldown / participation limits.

### Phase 5 — Adaptive Group Agent

Tune participation frequency from observed group traffic and feedback. Adapt **how often/how briefly** the bot speaks, not business truth or authorization.

## Participation policy

The phase-1 score favors precision:

Positive signals include:

- deterministic read-only domain/fact intent;
- a question;
- explicit session reference (`T6`, `CN`, ...);
- volleyball/domain vocabulary (`vote`, `poll`, `slot`, `draft`, `team`, ...).

Suppressing signals include:

- reply to another member;
- acknowledgement / emoji-only text;
- recent bot reply cooldown;
- a busy/high-velocity group.

An untagged mutation can be observed as `Action`, but `WouldReply` is forced false for the ambient path. It must be explicitly addressed to use the normal authorized action workflow.

## Privacy and observability

Ambient traces store routing facts, not raw prompts:

- message/group/sender IDs;
- participation score;
- candidate intent/kind;
- recent context message IDs;
- quote message ID;
- compact signal codes.

Raw group text remains governed by the existing `ZaloGroupMessage` retention policy; ambient trace should not introduce another raw-content store.

Useful shadow metrics before enabling Phase 2:

- total unaddressed messages observed;
- `WouldReply=true` rate;
- Fact/Social/Action candidate distribution;
- false-positive sample rate;
- suppression reasons (`bot_cooldown`, `busy_group`, `reply_to_member`, `ack_or_emoji_only`);
- duplicate webhook / trace idempotency rate.

## Enablement gate for Fact Participant

Do not enable outbound ambient replies until production shadow data shows high precision. For a group bot, false positives are more damaging than missed replies: it is preferable to stay silent on an uncertain message than repeatedly interrupt human conversation.
