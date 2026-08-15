# Zalo Domain Event Shadow

This phase lets the ambient agent observe **real domain changes** without sending any user-visible message.

## Source of truth

The observer runs only around the existing Zalo poll synchronization path:

```text
Zalo poll update_board event
        ↓
Capture current DB roster count
        ↓
SyncLatestPollAsync
        ↓ authoritative poll → SessionPlayer projection
Capture current DB roster count again
        ↓
metadata-only domain event shadow trace
```

Chat text is never used to decide whether a player registered, withdrew, filled a slot or reopened a slot.

## Event kinds

The first shadow vocabulary is intentionally small:

- `RosterIncreased`
- `RosterDecreased`
- `RosterFilled`
- `RosterReopened`

`RosterFilled` means the authoritative present-player count crossed from below capacity to capacity-or-higher. `RosterReopened` is the reverse transition.

## No outbound behavior

`ZaloDomainEventShadowObserver` has no bridge dependency and no send method. It writes only a `ZaloBotTraceStore` row with:

- poll board/event-derived synthetic trace message ID;
- group ID;
- actor Zalo UID when the provider event supplied one;
- session ID;
- event kind;
- before/after counts and capacity;
- `AiCalled=false`.

It stores no raw chat text and calls no AI model.

## Failure isolation

A shadow-observer failure is caught inside `ZaloPollEventWorker` and does not block:

- poll synchronization;
- overbook observation;
- waitlist vacancy processing.

No trace is written when the authoritative roster count did not change.

## Future gate

A later PR may add a separately gated read-only narrator for selected domain events. That future sender must consume these authoritative events; it must not recreate event truth from group-chat inference.
