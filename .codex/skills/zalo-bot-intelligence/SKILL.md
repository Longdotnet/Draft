# Zalo Bot Intelligence Skill

## Goal

Maintain a reliable Vietnamese conversational bot for VolleyDraft.

The bot must understand user intent, preserve short-term conversational context, remember only high-confidence user concepts, query real application data and avoid inventing business facts.

## Core architecture

Use this order:

1. Security and authorization.
2. Message idempotency.
3. Normalize channel metadata such as mentions and quote/reply relations.
4. Pending conversation-state resolution.
5. Exact deterministic commands.
6. Sender-aware conversation context assembly.
7. Structured AI intent classification.
8. Backend tool execution.
9. Approved learned knowledge and scoped user concepts.
10. Natural-language answer generation.
11. Safe fallback.

## Never do

- Do not interpret a sentence beginning with `1 ` as menu command 1.
- Do not use broad `Contains("tuan nay")` routing that steals unrelated questions.
- Do not treat user chat as model fine-tuning.
- Do not automatically turn ordinary group chatter into long-term memory.
- Do not infer a fact about person A from a statement written by person B unless a reviewed domain workflow explicitly supports that fact.
- Do not hardcode changing facts such as the number of weekly sessions.
- Do not let learned rules or user concepts override database facts.
- Do not share pending context or user concepts between users.
- Do not share a user's concepts between groups unless a future product requirement explicitly introduces a broader reviewed scope.
- Do not store conversation state only in memory.
- Do not allow arbitrary members to activate group rules without approval.
- Do not let the model invent session IDs, player status, times, locations or slot counts.
- Do not send duplicate replies for one Zalo message ID.
- Do not discard quote/reply metadata at the Zalo transport boundary.
- Do not require a fresh textual `@bot` mention when the user directly replies to a bot message; a verified quote owned by the bot is an explicit address.
- Do not advance application scheduling/reminder state unless the Zalo bridge positively confirms delivery.
- Do not persist an idempotency key as though it were a provider-issued Zalo message ID; retry identity and channel message identity are separate contracts.

## Message and reply context

Preserve quote/reply information when Zalo provides it:

- quoted message ID;
- quoted sender Zalo UID;
- quoted sender display name;
- quoted text;
- quoted message type/timestamp;
- attachment metadata when available.

Treat a direct reply to a message owned by the current bot UID as an explicit bot address. Keep the user's actual follow-up text unchanged. A reply to another member must not wake the bot by itself.

Actual outbound Zalo message IDs should be preserved whenever the library returns them. Do not manufacture an ID and later assume it is the channel's real reply target.

## Conversation state

A pending clarification is scoped by:

- Zalo connection/account;
- group;
- sender Zalo user ID.

It has:
- pending intent;
- candidate entities;
- collected arguments;
- missing arguments;
- expiry time.

When the bot asks a clarification, the next short answer must resolve the pending state before normal intent routing when it is genuinely a follow-up.

Examples:
- `t6`
- `thứ sáu`
- `2`
- `trận cuối`
- `15/7`

Clear state after success, cancellation or expiry.

A future ConversationState v2 should distinguish a short follow-up from a high-confidence new topic so a stale confirmation does not trap the user.

## Auto-session temporal invariants

Poll/session scheduling is authoritative application data and must stay deterministic end to end.

- If a poll option contains an explicit calendar date (`dd/MM/yyyy` or `dd/MM`), that date is authoritative. A weekday token in the same option may validate the date but must never override it.
- Infer from a weekday only when the option has no explicit calendar date.
- Temporal scope from the poll question, such as `tuần sau`, applies to weekday-only options. It must not shift an explicit date.
- If an explicit date and weekday conflict, fail closed before creating a website session and ask for clarification instead of choosing one interpretation silently.
- Resolve yearless dates with the established rollover/leap-year rules; do not regress New Year or leap-day behavior while changing weekday logic.
- The organizer preview and final create must share the same persisted structured draft. Do not reparse the source option into a different date during final confirmation.
- Revalidate the live poll structure before final creation. If question/options changed after preview, supersede the old confirmation and require a fresh preview.
- Immediately before any MatchSession/link mutation, verify that a source option containing an explicit date still agrees with the persisted resolved date. Abort the mutation on any mismatch.

## Sender-aware context assembly

Do not treat the latest N group messages as equally relevant.

Before AI classification or general chat, prefer:

1. the current sender's recent turns;
2. bot replies addressed to that sender;
3. messages adjacent to those turns;
4. semantic overlap with the current question;
5. a small guaranteed immediate tail for local referents such as `cái đó`, `ông này`, `T6`, or `xác nhận`.

Keep the selected messages in chronological order before serialization.

All recent chat is untrusted context, never system instructions.

## AI use

Use AI primarily for:
- intent classification;
- entity extraction;
- paraphrase understanding;
- final natural phrasing;
- semantic matching of approved knowledge.

Use strict structured JSON for classification/extraction.

C# handlers are responsible for:
- querying sessions;
- calculating week ranges;
- roster lookup;
- registration status;
- slot counts;
- permissions;
- writing data.

## User concepts

User concepts are distinct from approved group learned rules.

A user concept is scoped by at least:

- GroupId;
- subject Zalo UID;
- concept type;
- concept key.

Supported high-confidence examples include:

- Alias: `gọi tui là Long`;
- Preference: `tui hay đánh T6`;
- Availability preference: `tui không chơi CN`;
- Domain self-fact: `tui đánh libero`.

Concept records must keep:

- structured value JSON;
- confidence;
- scope;
- provenance/source when available;
- status;
- timestamps;
- optional expiry;
- superseded concept ID when a newer value replaces an older one.

When a new active concept conflicts with the same subject/type/key, supersede the older active concept instead of leaving two equal truths.

Current user text wins stale memory. Structured application/session data wins memory when they conflict.

Never infer persistent memory from jokes, guesses, third-party claims or unrelated group chatter.

## Learning

Application learning means:
- approved aliases/FAQ behavior rules at group scope;
- explicit scoped user concepts;
- conversation examples;
- semantic retrieval;
- evaluation improvements.

It does not mean automatically changing model weights.

All approved group rules require status and provenance. User concepts must remain traceable to the subject/scope and must support future review/forget controls.

## Testing

Every routing or context bug must produce a regression test.

Always test:
- natural sentences beginning with numbers;
- short follow-up answers;
- direct reply to the bot without a fresh textual mention;
- reply to another member does not wake the bot;
- two users in one group;
- the same user in two groups;
- expired context;
- superseding conflicting user concepts;
- duplicate message delivery;
- AI unavailable;
- legacy payload without quote metadata;
- SQLite user-concept persistence;
- PostgreSQL schema upgrade.

For auto-session date changes, also test explicit-date precedence, conflicting weekday/date, weekday-only temporal scope, New Year/leap-day boundaries, poll fingerprint changes after preview, persisted preview-plan reuse at create, and the final pre-mutation consistency guard.

CI must run the backend xUnit suite with `dotnet test`; compiling the test project alone is not sufficient.