# Zalo Proposal Confirmation Handoff

This phase converts a **read-only conversational proposal** into an existing domain write only after a strong, deterministic confirmation signal.

## Target flow

```text
Long: tui muốn chơi chung với To An thì bạn xếp đc ko
Bot: Cả hai có T6/CN. Chọn kèo nào?
Long: T6
Bot: Long + To An đều ở roster T6. Reply đúng tin này và nói "xác nhận" nếu muốn áp dụng.
Long: [reply chính tin bot ở trên] xác nhận
Bot: existing TeamPreferenceConfirm path applies the preference
```

## Authorization boundary

The V2 `AmbientTeamPreferenceProposal` is **context, not permission**. A write handoff requires all of these conditions:

1. active proposal is scoped to the same `GroupId + SenderZaloUserId`;
2. proposal already contains stable requester/partner Zalo UIDs and a concrete session id;
3. inbound text is a deterministic confirmation phrase;
4. inbound is a real quote/reply whose quoted sender is the bot;
5. quoted message id exactly matches the provider outbound message id recorded by the message graph for the proposal's latest source message;
6. requester UID equals the confirming sender UID;
7. session is still active, bot-enabled and in the same connection/group;
8. `SessionDraftService` can rebuild a feasible TeamPreference preview from the **current** roster;
9. no unrelated live legacy confirmation would be overwritten.

No AI output, memory value, quoted text snapshot, display-name guess, or synthetic local message id can authorize the write.

## Same-webhook handoff

`ZaloMemoryV2Service` runs before the legacy domain router for explicitly addressed messages (reply-to-bot is already explicit addressing in the inbound contract). It asks `ZaloAmbientTeamPreferenceHandoff` to validate the exact reply.

When valid, the adapter creates the already-supported `TeamPreferenceConfirm` pending envelope with a maximum TTL of 30 seconds and marks the V2 proposal `Completed` as a one-shot token. It deliberately returns through the normal unhandled path so **the same inbound webhook** reaches the existing atomic `ZaloBotService` flow.

The existing handler remains the mutation owner. It applies `TeamPreferencePreview`, records normal action history, and its `StateToken` validation rejects stale roster/share/preference state.

## Failure behavior

These do not promote or mutate:

- `ok` / `xác nhận` without a reply;
- reply to another bot message;
- another member replying to the proposal;
- expired/missing proposal;
- missing session selection;
- player removed from the current roster before confirmation;
- unrelated active legacy pending workflow;
- missing provider message-graph edge.

The proposal system does not affect Zalo poll registration truth. It writes only the existing team-preference domain after explicit confirmation.

## Social AI

Social AI / banter remains out of scope. This phase is strictly deterministic authorization and domain handoff.
