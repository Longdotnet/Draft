# Zalo Domain Event Narrator Pilot

This phase consumes the authoritative domain-event decisions produced after Zalo poll synchronization and can turn only selected transitions into deterministic group messages.

## Safety boundary

The narrator does not inspect chat history and does not decide whether registration changed. Its only input is `ZaloDomainEventShadowDecision`, which is produced by comparing database roster snapshots around a successful `SyncLatestPollAsync`.

It performs no domain mutation and calls no AI model.

## Narratable events

Only two high-signal transitions are eligible:

- `RosterFilled`: the poll-derived roster crosses from below capacity to full;
- `RosterReopened`: a previously full roster drops below capacity.

Ordinary `RosterIncreased` and `RosterDecreased` changes stay silent to avoid group spam.

## Three gates

Outbound sending requires all of these:

```text
ZaloBot:Ambient:DomainEventPilot:Enabled = true
ZaloBot:Ambient:DomainEventPilot:SendEnabled = true
ZaloBot:Ambient:ShadowMode = false
```

Source defaults keep the first two false and global shadow mode true, so deployment of this code does not automatically enable messages.

## Message examples

```text
✅ T6 đã đủ 18/18 người theo poll hiện tại.
📢 CN vừa trống lại 2 suất (16/18) theo poll hiện tại.
```

The wording explicitly says `theo poll hiện tại` so the bot does not present chat inference as registration truth.

## Idempotency

Each send uses a deterministic key derived from session id, event kind, before/after counts and capacity. Provider retry protection remains owned by the existing Zalo Bridge send endpoint.

## Failure isolation

Narration runs inside the existing domain-event observation try/catch. A bridge/narrator failure does not undo poll synchronization and does not block overbook or waitlist processing.
