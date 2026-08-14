# Zalo Conversation Context and User Memory

This document describes the conversation-context foundation used by the VolleyDraft Zalo bot and the invariants that future changes must preserve.

## Goals

The bot should be able to:

- identify who is speaking by Zalo UID instead of display name alone;
- keep one user's short-term context separate from another user's context in the same group;
- understand a direct reply to a bot message as a continuation without forcing the user to type `@bot` again;
- preserve Zalo quote/reply metadata instead of throwing it away at the transport layer;
- remember a small set of explicit self facts/preferences with provenance and conflict handling;
- keep database/application facts authoritative over AI inference and memory.

This is not model fine-tuning. Memory is application data.

## Current flow

```text
Zalo event
  -> ZaloBridge normalization
  -> quote/mention/address metadata
  -> VolleyDraft API message idempotency
  -> pending conversation state
  -> deterministic routing
  -> sender-aware context assembly
  -> AI classification when needed
  -> domain handler / database truth
  -> scoped learned knowledge + user concepts
  -> final response
```

## Transport contract

`ZaloBridge` preserves quote metadata when the underlying Zalo library provides it:

- `messageId`
- `senderId`
- `senderName`
- `content`
- `messageType`
- `sentAtUnixMs`
- attachment metadata when available

New quote fields remain optional at the wire boundary so older payloads continue to deserialize.

The bridge also exposes the real outbound Zalo `messageId` returned by `zca-js`. A later persistence phase should write this real ID into outbound message history rather than relying on a synthetic local ID.

## Direct reply addressing

A Zalo user may answer:

```text
Bot: Bạn muốn trận nào?
User: [Reply] T6
```

The second message is an explicit address to the bot because the quote owner is the current bot UID. The API normalizes this into the same addressing invariant used by the existing message gate while keeping the actual question text exactly `T6`.

A quote owned by another member does not wake the bot by itself.

## Sender-aware context

The AI should not receive a blind window where all recent group messages are equally important.

`ZaloConversationContextAssembler` ranks context using:

- same-sender turns;
- bot replies addressed to that sender;
- turns adjacent to the sender's messages;
- token overlap with the current question;
- recency.

It also guarantees a small immediate tail. This prevents old same-user history from crowding out the newest local referent.

The final selected messages are restored to chronological order before serialization.

## User concepts

User concepts are separate from `ZaloBotLearnedRule`.

A learned rule is reviewed group behavior/knowledge. A user concept is a fact or preference about the current subject.

Examples currently extracted only when stated explicitly by the subject:

```text
tui hay đánh T6
nhớ là tui không chơi CN
tui đánh libero
gọi tui là Nguyễn Long
```

Supported concept categories in the first slice:

- `Alias`
- `Preference`
- `DomainFact`

The store is scoped by `GroupId + SubjectZaloUserId`, so two people in one group and one person in two groups do not share memory accidentally.

Each concept keeps:

- `ConceptType`
- `ConceptKey`
- `ValueJson`
- scope
- confidence
- source/provenance fields
- status
- optional expiry
- confirmation timestamp
- creation/update timestamps
- `SupersedesConceptId`

When a newer concept uses the same subject/type/key, the previous active concept becomes `Superseded`.

## Truth precedence

Use this order when data conflicts:

1. authorization/security rules;
2. structured current application/database data;
3. current user message;
4. active scoped user concept;
5. approved learned rule when applicable;
6. recent conversation context;
7. AI inference.

A memory such as `tui hay đánh T6` must never make the bot claim the user is registered for a specific T6 session when the roster says otherwise.

## What must not become memory

Do not persist:

- third-party guesses such as `Long chắc thích libero`;
- ordinary group chatter;
- jokes or sarcasm;
- a statement about another member merely because someone mentioned their name;
- AI-generated assumptions;
- transient operational facts that already belong in the application database.

## Persistence rollout

The first user-concept store uses an additive `CREATE TABLE IF NOT EXISTS` path for SQLite and PostgreSQL so the feature can roll out without destructive schema changes.

SQLite schema initialization intentionally avoids a process-wide cache because in-memory SQLite databases are scoped to their physical connection and can otherwise be mistaken for the same database during tests.

## Testing and CI

Important regression scenarios include:

- same sender vs another sender in the same group;
- same Zalo UID in different groups;
- direct reply to bot with `T6` or `xác nhận`;
- reply to a normal member does not wake the bot;
- legacy incoming payload without `quote`;
- concept superseding;
- SQLite persistence;
- AI request contains only the current sender's active concepts;
- AI unavailable;
- duplicate message delivery.

The GitHub Actions backend job must run `dotnet test`. Building the test project without executing xUnit is not sufficient.

## Next phases

The current foundation intentionally leaves these larger changes separate:

1. **Quoted-content reasoning**: inject the quoted message as a first-class context relation so phrases such as `ông này` or `cái đó` can be grounded safely.
2. **Real outbound message persistence**: persist the real Zalo `messageId` returned by the bridge into `ZaloGroupMessage` so reply chains can join directly to stored bot turns.
3. **Person/Identity resolver**: map aliases and channel identities to a stable person/domain entity instead of matching display text throughout handlers.
4. **ConversationState v2**: represent collected/missing arguments structurally and allow a high-confidence new topic to escape a stale pending confirmation.
5. **Memory controls**: deterministic `bot nhớ gì về tui?`, targeted forget/disable, and review UI.
6. **Trace/observability**: record intent source/confidence, context/concept IDs used, entity resolution, selected session and fallback reason.
7. **Retention/privacy policy**: define message and concept retention, cleanup and export/delete behavior before expanding the memory scope.
