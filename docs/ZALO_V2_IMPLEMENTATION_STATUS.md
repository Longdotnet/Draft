# Zalo V2 Implementation Status

## Implemented on this branch

- First-class quote semantic resolver (`ZaloQuotedContextResolver`).
- Additive message relation/provider ID store (`ZaloMessageGraphStore`).
- Stable scoped identity resolver with explicit ambiguity (`ZaloIdentityResolver`).
- Structured conversation-state V2 store and central topic-switch rule (`ZaloConversationStateV2Store`).
- Deterministic memory list/forget-all/forget-by-key plus explicit self-concept pre-routing component (`ZaloMemoryV2Service`).
- ID-first routing trace store (`ZaloBotTraceStore`).
- Configurable retention policy (`ZaloRetentionPolicy`).
- Unit and multi-turn eval coverage for quote-person, quote-object, identity ambiguity, topic switch, argument correction, memory controls, graph persistence and traces.

## Production-hook checklist

The V2 kernel is intentionally additive beside the V1 runtime while CI verifies it. Runtime migration is considered complete only when the following hooks are wired:

- [ ] store incoming quote relation before `BuildAnswerAsync`;
- [ ] run memory V2 before deterministic/AI routing and return deterministic memory-control answers directly;
- [ ] persist the real provider outbound message ID returned by `ZaloBridgeClient.SendGroupMessageAsync` instead of a synthetic `bot:{guid}` history ID;
- [ ] register outbound reply relation in the message graph;
- [ ] feed structured quote grounding to classifier/general-answer context;
- [ ] migrate person-targeting handlers to `ZaloIdentityResolver`;
- [ ] migrate legacy pending workflows incrementally to `ZaloConversationStatesV2`;
- [ ] emit `ZaloBotTraceStore` records for sent/no-reply/throttled/failure outcomes;
- [ ] run retention cleanup on a scheduled maintenance path.

Keeping this checklist explicit prevents a foundation-only branch from being mistaken for a fully migrated V2 runtime.
