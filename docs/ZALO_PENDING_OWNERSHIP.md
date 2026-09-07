# Zalo Pending Conversation Ownership

## Why this exists

Auto Session V4 needs one predictable ownership boundary before durable proposal/recovery work can become authoritative. V5 builds on that completed V4 foundation; it is not a direct rename or jump from Conversation V3.

Historically, session-selector pending state and ConversationState V2 each implemented their own ordering for:

- a bare pending control such as `hủy` / `xác nhận`;
- a domain-qualified fresh command such as `hủy reminder`;
- broad feature-specific cancel/confirm grammar.

That duplication already diverged in production-facing code and required separate fixes (#171 and #172).

## Shared contract

`ZaloPendingOwnershipPolicy` owns only the cross-domain precedence that every pending workflow must agree on:

1. Exact bare pending controls stay with the current pending workflow.
2. Otherwise, a different high-confidence deterministic intent owns the current turn.
3. Feature-specific continuation/cancel/confirm grammar runs only after that shared decision.

Examples:

- pending draft + `hủy` -> cancel the pending draft flow;
- pending draft + `hủy reminder` classified as `CancelReminder` -> switch to reminder;
- pending session choice + `xác nhận` -> shared layer says “pending confirm”, then the session-specific layer still refuses to choose a session without a selector;
- low-confidence classifier output never steals pending ownership.

## What remains feature-specific

The shared policy intentionally does **not** decide whether arbitrary text is a valid continuation.

Examples kept outside the shared layer:

- session selectors such as `T6`, `13/9`, `trận cuối`;
- profile answers;
- reminder-specific grammar;
- pass/share-slot confirmation semantics;
- whether a bare confirmation can execute a particular workflow.

This avoids creating one giant global parser while still preventing ownership precedence from drifting again.

## Auto Session V4/V5 progression

This shared ownership contract is a **V4 foundation**, not evidence that Auto Session has already reached V5.

The intended progression is:

```text
V3 stateful conversation
  -> V4 unified ownership + durable MatchProposal + provenance/recovery/reconciliation
  -> V5 policy-driven Match Birth + lifecycle handoff
```

During V4, Auto Session should continue toward:

`normalized turn -> addressing/context evidence -> one conversation owner -> deterministic/optional-AI interpretation -> durable proposal revision -> deterministic validation -> domain action`

V5 can then place policy evaluation and lifecycle handoff on top of that stable proposal/ownership contract.

New pending workflows should reuse the shared ownership boundary instead of copying fresh-intent/cancel ordering into another helper.