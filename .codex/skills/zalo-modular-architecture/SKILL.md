---
name: zalo-modular-architecture
description: Architecture rules for adding, refactoring, or debugging Zalo bot features without growing legacy god classes.
---

# Zalo Modular Architecture

Use this skill whenever work touches the Zalo bot, a Zalo conversation flow, a new bot command, pending state, session/date parsing, or an AI-powered Zalo behavior.

Read `docs/ZALO_MODULAR_ARCHITECTURE.md` before editing code.

## Non-negotiable rules

1. **One feature = one ownership folder** under `server/VolleyDraft.Api/Services/Zalo/Features/<FeatureName>/`.
2. Do not put new feature logic into `ZaloBotService.cs`, `ZaloOverbookReminder.cs`, `ZaloOverbookService.cs`, or `ZaloBotIntelligence.cs`. During migration those files may only delegate to a feature module or shared core.
3. Do not create another text/date/session parser. Reuse:
   - `ZaloTextNormalizer`
   - `ZaloSessionResolver`
   - `ZaloMenuCommandParser`
   - `ZaloPendingTurnPolicy`
4. A pending state may consume a new message only when the message matches that feature's continuation grammar or is explicitly correlated to the previous prompt. Presence of a pending state alone is never sufficient.
5. Deterministic routing wins over AI classification. Exactly one feature may execute for one inbound message. Ambiguous equal winners fail closed.
6. Feature code must not read `Ai:Endpoint`, `Ai:ApiKey`, or `Ai:Model` directly. Use `IZaloAiGateway` and choose a `ZaloAiWorkload`.
7. AI never owns authoritative facts or performs mutations directly. Compute/validate session, roster, slot, permission, idempotency and database mutation deterministically.
8. Every real-world production bug must become a transcript/regression test before or together with the fix.

## Feature shape

Prefer this layout:

```text
Features/<FeatureName>/
  <FeatureName>Contracts.cs
  <FeatureName>IntentPolicy.cs
  <FeatureName>State.cs          # only if multi-turn
  <FeatureName>Handler.cs
  <FeatureName>Renderer.cs       # only when useful
  stores/repositories owned by the feature
```

Mirror behavioral tests under:

```text
server/VolleyDraft.Api.Tests/Zalo/Features/<FeatureName>/
```

Do not create layers just to create files. A type belongs in shared Conversation/Routing/AI only when multiple independent features truly depend on the same semantic behavior.

## Adding a new feature

Follow this order:

1. Write transcript/regression tests for the user-facing behavior and topic switches.
2. Define feature contracts/state.
3. Implement deterministic intent recognition for high-confidence syntax.
4. Implement the feature handler and deterministic validation/mutation.
5. Use `IZaloAiGateway` only for ambiguity, structured extraction, social generation, or bounded rewriting.
6. Register the module with the single-winner router.
7. Add route/AI observability.
8. Run the full backend suite, bridge tests and frontend build.

## AI model selection

Choose by workload rather than hardcoding a model:

- `IntentClassification`: cheap, fast model is normally enough.
- `StructuredExtraction`: low-temperature structured-output capable model.
- `GeneralChat`: conversational model.
- `SocialReply`: model selected for natural Vietnamese group banter.
- `SafeRewrite`: inexpensive reliable rewriting model.
- `DomainNarration`: model for phrasing deterministic domain outcomes.

Models can be overridden through:

```text
Ai__Models__IntentClassification
Ai__Models__StructuredExtraction
Ai__Models__GeneralChat
Ai__Models__SocialReply
Ai__Models__SafeRewrite
Ai__Models__DomainNarration
```

The feature must not know the concrete provider/model name.

## Refactoring legacy logic

Use a strangler migration, not a big-bang rewrite:

1. freeze current expected behavior with tests;
2. move all files owned exclusively by the feature into its feature folder without changing behavior;
3. extract shared semantics only when demonstrated to be shared;
4. make the legacy class a compatibility facade/delegator;
5. run CI;
6. remove duplicated legacy logic only after callers are migrated.

A refactor is incomplete if logic was only renamed but the feature still depends on another feature's internal implementation.

## Review checklist

Before finishing, verify:

- one inbound turn has one winning feature;
- stale state cannot hijack unrelated chatter;
- exact dates do not degrade to weekday-only matching;
- historical records are not returned for current availability questions;
- no feature introduced direct provider HTTP/config access;
- no AI output can mutate data without deterministic validation;
- tests cover cancel, clarify, topic switch, stale state, explicit session and relative session references.
