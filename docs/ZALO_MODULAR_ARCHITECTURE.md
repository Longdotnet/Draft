# Zalo Modular Architecture

## Goal

The Zalo bot must remain easy to extend even when the number of features, conversation states and AI models grows. New behavior must not make `ZaloBotService`, `ZaloOverbookService` or `ZaloBotIntelligence` larger god-classes.

The architecture is split into four layers:

```text
Inbound transport
    ↓
Conversation + routing core
    ↓
Feature module
    ↓
Deterministic domain services / repositories
    ↓
Optional AI gateway for bounded NLP or phrasing work
```

AI is never the source of truth for match/session/player state and never performs mutations directly.

## Folder ownership

```text
Services/Zalo/
  Conversation/
    ZaloTextNormalizer.cs
    ZaloSessionResolver.cs
    ZaloMenuCommandParser.cs
    ZaloPendingTurnPolicy.cs

  Routing/
    ... router/orchestration only ...

  Features/
    Draft/
    PassSlot/
    TeamPreference/
    Waitlist/
    Guest/
    Roster/
    Reminder/
    TeamLineup/
    PollSync/
    MemberActivity/
    Social/

  AI/
    ZaloAiGatewayContracts.cs
    OpenAiCompatibleZaloAiGateway.cs

  Observability/
    ... route traces, AI telemetry, feature outcome traces ...
```

A feature folder owns its own contracts, intent recognition, continuation state, handler, response rendering and tests. Shared code belongs outside a feature only when at least two independent feature modules genuinely need the same semantic rule.

## Feature module rule

Each feature should converge on the following shape:

```text
Features/<FeatureName>/
  <FeatureName>Contracts.cs
  <FeatureName>IntentPolicy.cs
  <FeatureName>State.cs            # only when multi-turn state is required
  <FeatureName>Handler.cs
  <FeatureName>Renderer.cs         # when deterministic response formatting is non-trivial
```

Tests should mirror the feature folder by behavior, not by giant service class.

A feature handler must not call another feature handler directly. Cross-feature transitions are returned to the router as an explicit route/continuation outcome.

## Conversation rules

`Services/Zalo/Conversation` is deterministic and model-independent.

It owns only shared conversation semantics:

- text normalization;
- exact date / relative date / weekday session resolution;
- numeric menu parsing;
- cancel / confirmation primitives;
- pending-turn relevance and topic switching.

A pending feature state must never consume a new turn merely because it exists. The current turn must satisfy the feature's continuation grammar or be explicitly correlated to the previous bot prompt.

The following invariants are product behavior, not parser implementation details:

- an exact deterministic menu command always owns the current turn, even when its suffix is also a valid session selector such as `8 T4`;
- a pending session-choice prompt only consumes an actual session selector; a bare `ok`, `chốt` or unrelated new action cannot choose a session;
- explicit calendar dates dominate weekday aliases and retained history must not make a day/month request ambiguous across different years;
- `T4/T6/CN tuần trước|này|sau|tới` resolves to that explicit Vietnam calendar week, including across month/year boundaries;
- bare `mai` is treated as tomorrow only when it is the standalone selector (punctuation is allowed); longer text must use explicit temporal wording such as `ngày mai`, so a member named `Mai` is not silently interpreted as a date.

## AI boundary

Feature code must not read `Ai:Endpoint`, `Ai:ApiKey` or `Ai:Model` directly.

Feature code asks `IZaloAiGateway` for a workload:

- `IntentClassification`
- `StructuredExtraction`
- `GeneralChat`
- `SocialReply`
- `SafeRewrite`
- `DomainNarration`

The gateway owns:

- provider endpoint and API key;
- default model;
- workload-specific model overrides;
- timeout;
- retry of transient failures;
- optional fallback provider/model;
- typed failure results;
- provider/model/attempt telemetry.

This allows, for example, a cheap classifier model and a stronger social model without changing the Draft, PassSlot or TeamPreference feature code.

Suggested environment configuration:

```text
Ai__Endpoint
Ai__ApiKey
Ai__Model
Ai__Provider
Ai__TimeoutSeconds
Ai__RetryCount

Ai__Models__IntentClassification
Ai__Models__StructuredExtraction
Ai__Models__GeneralChat
Ai__Models__SocialReply
Ai__Models__SafeRewrite
Ai__Models__DomainNarration

Ai__Fallback__Provider
Ai__Fallback__Endpoint
Ai__Fallback__ApiKey
Ai__Fallback__Model
```

## Deterministic-first rule

Use deterministic code for:

- authorization;
- session/date resolution;
- current roster/slot state;
- pass-slot availability;
- confirmation/cancel state transitions;
- database mutations;
- idempotency;
- numeric commands;
- safety validation.

AI may be used for:

- ambiguous natural-language classification after deterministic routes fail;
- structured extraction when deterministic parsing is insufficient;
- social conversation;
- bounded style rewriting after facts are already computed;
- optional narration of a deterministic result.

Every AI-produced mutation proposal must pass deterministic validation before execution.

## Migration policy

The existing large services remain compatibility façades during migration. Do not rewrite the whole production bot in one commit.

Migrate one feature at a time:

1. freeze behavior with transcript/regression tests;
2. create the feature folder and contracts;
3. move feature-specific parsing/state/handler logic;
4. make the legacy service delegate to the feature module;
5. run the full CI suite;
6. only then remove duplicated legacy code.

Priority order after the shared core:

1. Draft + draft readiness state;
2. PassSlot;
3. TeamPreference;
4. Waitlist / SlotTransfer / ShareSlot;
5. Guest recruitment;
6. Roster / lineup / images;
7. Reminder / overbook;
8. social and remaining informational features.

## Guardrails for AI coding agents

When adding or modifying a Zalo feature:

- do not add new feature logic to `ZaloBotService.cs`, `ZaloOverbookReminder.cs` or `ZaloBotIntelligence.cs` unless it is a temporary delegation call;
- do not create a second date/session resolver;
- do not access AI provider configuration from a feature;
- do not allow an LLM response to mutate data without deterministic validation;
- add regression tests for real conversational sequences, especially pending-state topic switches;
- route traces should record the winning feature and AI workload/provider/model when AI was used.
