# Zalo V2 Implementation Status

## Implemented on this branch

- First-class quote semantic resolver (`ZaloQuotedContextResolver`).
- Request-turn quote grounding shared across async service boundaries via the ASP.NET request `Activity`, then injected into the common AI context assembler for classifier and general-answer paths as explicitly untrusted conversational data.
- Additive message relation/provider ID store (`ZaloMessageGraphStore`).
- Provider outbound IDs recorded best-effort after every successful real bridge send without turning persistence failures into duplicate-send retries.
- Backward reconciliation from legacy `bot:{guid}` core history IDs to real provider IDs when a trustworthy direct quote arrives.
- Stable scoped identity resolver with explicit ambiguity (`ZaloIdentityResolver`).
- Quote-person UID projection into legacy mention-aware handlers for deictic references such as `ông này` while broader handler migration remains incremental.
- Structured conversation-state V2 store and central topic-switch rule (`ZaloConversationStateV2Store`).
- Legacy pending-state shadowing into V2 plus high-confidence escape from stale pending confirmations.
- Draft-family conversation continuity treats `AutoDraft` and `Redraft` as one conversational workflow while keeping unrelated operational intents distinct.
- Deterministic memory list/forget-all/forget-by-key plus explicit self-concept pre-routing component (`ZaloMemoryV2Service`).
- Repeated identical concept writes are idempotent; changed concepts still supersede previous values.
- User forget/delete commands hard-delete the requested scoped concept history instead of merely hiding it from prompts.
- ID-first routing trace store (`ZaloBotTraceStore`).
- Terminal outcomes persisted by the legacy bot are projected exactly once into the V2 trace schema by `ZaloLegacyOutcomeTraceProjector` without editing every legacy return path.
- Configurable retention policy and scheduled V2 cleanup from the existing worker.
- Unit and multi-turn eval coverage for quote-person, quote-object, async quote propagation, identity ambiguity, topic switch, argument correction, memory controls/privacy, graph persistence/reconciliation, traces and retention.

## Production-hook status

V2 is deliberately additive beside the V1 runtime. The current migration status is:

- [x] Store incoming quote relation before legacy `BuildAnswerAsync` routing.
- [x] Run explicit self-memory ingestion before deterministic/AI routing.
- [x] Return deterministic user-owned memory-control answers without AI deciding deletion.
- [x] Record the real provider outbound `messageId` in the V2 message graph after every successful non-mock bridge send.
- [~] Replace synthetic `bot:{guid}` in the legacy core `ZaloGroupMessage` history. New V2-owned memory replies persist the provider ID directly; legacy rows are safely reconciled when directly quoted, but the legacy bot send/history block itself still writes a synthetic ID.
- [~] Register outbound reply relations. V2-owned deterministic replies carry the incoming parent ID; generic legacy bridge sends record the provider outbound ID even when the parent message is not available to `ZaloBridgeClient`.
- [x] Feed structured quote grounding to both classifier and general-answer context through the shared `ZaloConversationContextAssembler`.
- [~] Migrate person-targeting handlers to stable identity resolution. Quote-person references now project the quoted sender UID into existing mention-aware handlers; `ZaloIdentityResolver` is available and tested for aliases/ambiguity, but not every legacy display-name lookup has been replaced yet.
- [~] Migrate legacy pending workflows to `ZaloConversationStatesV2`. Active legacy state is shadowed with structured V2 metadata and central topic switching can escape stale pending state; individual workflow payloads still use legacy handler-specific JSON until migrated one by one.
- [~] Emit `ZaloBotTraceStore` records for all routing detail. Terminal legacy sent/throttled/no-reply/failure outcomes are now eventually projected into the common trace schema, while some richer per-turn fields such as exact reply message ID, entity resolution and concept IDs are only available on V2-owned paths until the legacy common finish/send block is migrated.
- [x] Run retention cleanup on a scheduled maintenance path. The existing overbook worker performs V2 cleanup every six hours according to `ZaloRetentionPolicy`.

## Validation

Latest full backend test run on this branch: **395 passed, 0 failed, 0 skipped**. Frontend build, Zalo Bridge build/tests and NPC11 worker syntax also pass. Docker image validation is part of the same CI workflow and must be green before this PR is considered ready for merge.

## Remaining migration work after this PR

1. Replace the legacy `ZaloBotService` outbound persistence block so the canonical core `ZaloGroupMessage.MessageId` is the provider ID immediately, eliminating the need for reconciliation on new messages.
2. Move the remaining person-targeting handlers from local display-name matching to `ZaloIdentityResolver`, preserving explicit ambiguity/clarification behavior.
3. Convert each legacy pending workflow payload into typed/structured V2 collected and missing arguments instead of only shadowing it.
4. Move richer trace emission (reply provider ID, entity/concept IDs and exact routing source) into the legacy bot's common finish/send path; terminal outcome coverage itself is already projected by the worker.
5. After those migrations are complete and production behavior is observed, remove the legacy compatibility paths rather than maintaining two state/identity systems indefinitely.

This status file intentionally distinguishes implemented infrastructure from runtime-complete migration so V2 is never mistaken for finished merely because the new kernel exists.
