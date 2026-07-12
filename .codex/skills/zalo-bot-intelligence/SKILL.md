# Zalo Bot Intelligence Skill

## Goal

Maintain a reliable Vietnamese conversational bot for VolleyDraft.

The bot must understand user intent, preserve short-term conversational context, query real application data and avoid inventing business facts.

## Core architecture

Use this order:

1. Security and authorization.
2. Message idempotency.
3. Pending conversation-state resolution.
4. Exact deterministic commands.
5. Structured AI intent classification.
6. Backend tool execution.
7. Approved learned knowledge.
8. Natural-language answer generation.
9. Safe fallback.

## Never do

- Do not interpret a sentence beginning with `1 ` as menu command 1.
- Do not use broad `Contains("tuan nay")` routing that steals unrelated questions.
- Do not treat user chat as model fine-tuning.
- Do not hardcode changing facts such as the number of weekly sessions.
- Do not let learned rules override database facts.
- Do not share pending context between users.
- Do not store conversation state only in memory.
- Do not allow arbitrary members to activate rules without approval.
- Do not let the model invent session IDs, player status, times, locations or slot counts.
- Do not send duplicate replies for one Zalo message ID.

## Conversation state

A pending clarification is scoped by:

- Zalo connection/account
- group
- sender Zalo user ID

It has:
- pending intent;
- candidate entities;
- collected arguments;
- missing arguments;
- expiry time.

When the bot asks a clarification, the next short answer must resolve the pending state before normal intent routing.

Examples:
- `t6`
- `thứ sáu`
- `2`
- `trận cuối`
- `15/7`

Clear state after success, cancellation or expiry.

## AI use

Use AI primarily for:
- intent classification;
- entity extraction;
- paraphrase understanding;
- final natural phrasing;
- semantic matching of approved knowledge.

Use strict structured JSON.

C# handlers are responsible for:
- querying sessions;
- calculating week ranges;
- roster lookup;
- registration status;
- slot counts;
- permissions;
- writing data.

## Learning

Application learning means:
- approved aliases;
- approved FAQ rules;
- conversation examples;
- semantic retrieval;
- evaluation improvements.

It does not mean automatically changing model weights.

All learned rules require status and provenance.

## Testing

Every routing bug must produce a regression test.

Always test:
- natural sentences beginning with numbers;
- short follow-up answers;
- two users in one group;
- expired context;
- duplicate message delivery;
- AI unavailable;
- PostgreSQL schema upgrade.