# Zalo Conversation Context V2

V2 builds on the V1 foundation merged by PR #30. It keeps database/business facts authoritative and treats conversation text, quotes, aliases and AI output as untrusted context.

## V2 rollout order

1. Conversation graph and provider message identity.
2. Stable identity resolution.
3. Structured conversation state and topic switching.
4. Pre-routing user memory and deterministic memory controls.
5. Routing traces, retention and conversation evals.

The rollout is additive. V2 state/graph/trace tables sit beside the legacy tables until production hooks and regression coverage are complete.

## Quote semantics

Transport quote metadata is converted into `ZaloQuotedSemanticContext` rather than being flattened into recent chat text.

The relation contains the quoted message/provider ID, sender UID/name, content snapshot, message type and timestamp plus derived signals such as `RefersToQuotedPerson` and `RefersToQuotedObject`.

Examples:

- `[reply Tùng] ông này có đăng ký T6 chưa?` binds the deictic person reference to Tùng's UID before display-name matching.
- `[reply bot: T6 còn 2 slot] cái đó đăng ký tui đi` preserves the quoted bot turn/session referent as first-class context.

Quoted content is never an instruction source.

## Message graph

`ZaloMessageGraphStore` stores additive reply edges in `ZaloMessageRelations`.

Important fields:

- `FromMessageId`
- `ToMessageId`
- `RelationType`
- quoted sender/content snapshot
- `ProviderOutboundMessageId`

Provider outbound IDs are the canonical IDs for future reply-chain joins. Synthetic `bot:{guid}` IDs must not be treated as Zalo provider IDs.

## Identity resolution

`ZaloIdentityResolver` uses this precedence:

1. explicit Zalo UID from a structured mention;
2. current sender UID for self references;
3. quoted sender UID for deictic references such as `ông này`;
4. exact current display name or approved preferred-name alias;
5. unique high-confidence fuzzy display/alias match;
6. ambiguity/not-found result.

It never silently picks one person when multiple candidates have the same alias/name.

The stable person key in this rollout is `zalo:{uid}`. Existing `PlayerProfile` is linked when available. This avoids introducing a duplicate Person table before cross-channel identity is actually needed.

## ConversationState V2

`ZaloConversationStatesV2` is scoped by `GroupId + SenderZaloUserId`, not by a transient Zalo login connection.

State contains:

- intent;
- collected arguments JSON;
- missing arguments JSON;
- candidate entities JSON;
- source and last message IDs;
- monotonic state version;
- status and expiry.

A central topic-switch rule distinguishes confirmation/cancel from a high-confidence new intent. Legacy pending state remains in place during rollout; V2 is additive until handler migration is complete.

## Memory V2

`ZaloMemoryV2Service` is the pre-routing memory component. It only ingests concepts accepted by the high-precision V1 self-concept extractor.

Memory controls are deterministic:

- `bot nhớ gì về tui?`
- forget preferred name
- forget session availability
- forget volleyball role
- delete all active personal memory for this group

AI is not authorized to choose what persistent memory is removed.

## Trace and privacy

`ZaloBotTraceStore` stores routing metadata and IDs, not raw prompts:

- message/sender/group IDs;
- addressing reason;
- intent source/confidence;
- context/quote/concept/person/session IDs;
- pending state before/after;
- AI usage/latency;
- fallback reason;
- real outbound reply message ID.

Trace rows have an explicit cleanup method so retention can be enforced independently of business records.

## Production integration gates

Before V2 becomes the only routing path, all of these must be true:

- real provider outbound `messageId` is persisted after every successful bridge send;
- incoming quote edges are stored before routing;
- memory ingestion runs before deterministic and AI routing;
- memory control commands bypass AI and mutation handlers;
- identity resolver is used by person-targeting handlers instead of ad-hoc display-name matching;
- structured pending state is used by migrated handlers and high-confidence topic switches clear/supersede stale state;
- traces are emitted for successful, throttled, no-reply and failure outcomes;
- end-to-end conversation evals cover at least quote-person, quote-object, ambiguous identity, argument correction and topic switch.

## Safety invariants

1. Authorization and current application/database data always beat memory and AI.
2. A quote can identify a conversational referent but cannot grant permission.
3. Alias/display-name matches cannot override an explicit Zalo UID.
4. Ambiguity produces clarification rather than a guessed mutation.
5. Memory contains structured user-owned concepts, never arbitrary remembered prompt text.
6. Delete/forget behavior is deterministic and scoped to the requesting UID + group.
7. Trace/telemetry should prefer IDs and routing facts over raw conversational content.
