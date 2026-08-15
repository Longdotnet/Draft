# Zalo Ambient Fact Pilot

This phase adds the first possible outbound ambient response, but repository defaults keep it disabled.

## Independent safety gates

A real untagged Fact reply requires all of the following:

1. `ZaloBot:Ambient:Enabled=true`
2. `ZaloBot:Ambient:ShadowMode=false`
3. `ZaloBot:Ambient:FactPilot:Enabled=true`
4. participation decision is `WouldReply=true`
5. candidate kind is `Fact`
6. score is at least `FactPilot:MinimumScore` (default `85`)
7. intent is in the hardcoded read-only allowlist
8. the session reference resolves uniquely when the intent needs one

Default repository configuration fails gates 2 and 3 intentionally.

## Read-only allowlist

`ZaloAmbientFactResponder` supports only:

- `SessionSchedule`
- `LocationParking`
- `MissingSlots`
- `UpcomingSessions`
- `Roster`

The allowlist is code-owned rather than configuration-owned so an operator cannot accidentally enable a write intent by editing settings.

## Why the legacy router is not reused

`ZaloBotService.BuildAnswerAsync` is designed for explicitly-addressed commands. Before routing a fresh command it can resolve pending confirmations and later enter mutation handlers such as draft/redraft, guest creation, slot sharing/transfers, team rebalance, waitlist changes and undo.

Ambient participation therefore uses a separate responder that reads only current `MatchSession` and `SessionPlayer` state. It does not inspect pending action state, learned rules, AI memory, or write handlers.

## Ambiguity policy

Ambient mode does not ask a clarification merely because it noticed a possible Fact question. If more than one current session matches a reference such as `T6`, it stays silent. Explicit `@bot` routing remains available when the user wants an interactive clarification flow.

## Send idempotency

The outbound key is stable for the source message:

```text
ambient-fact:{accountId}:{messageId}
```

The inbound `ZaloGroupMessage` is atomically claimed before sending. Successful replies set `ReplyOutcome=ambient_sent` and write an `AmbientFactPilot` trace. Provider reply IDs are linked to the inbound message graph when available.

## Production enablement

Do not enable this pilot until the Phase 1 shadow review in `ZALO_AMBIENT_SHADOW_REVIEW.md` meets the precision gate. Start with one low-risk group, keep the minimum score high, and retain the existing bot cooldown/human-thread hard suppressions.
