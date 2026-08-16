# Zalo Member Assist + Chat Permissions

This rollout moves NPC from a command-only bot toward a useful group member while preserving domain truth and authorization boundaries.

## Member-assist rule

An ordinary unmentioned group message may trigger a **read-only help offer** when the meaning is high-confidence and useful. The first supported opportunity is a member clearly offering to pass their own slot, including common chat variants such as `pass slot` and `pass sỉ lót`.

The ambient helper may:

- identify the sender's current/upcoming session from authoritative roster data;
- ask which session when more than one is possible;
- tell the member how to continue the transfer flow naturally.

The ambient helper may **not** mutate roster, slot, team, preference, waitlist, profile or reminder state. Actual changes stay on the existing explicit/confirmed domain path.

## Self-service boundary

`ShareSlot` and `TeamPreference` do not require operator privilege when the sender is operating on their own participation and the backend can bind the realtime Zalo UID to that player safely.

To support old poll/import data where a player profile existed before its Zalo UID was known, pre-routing may link the realtime sender UID to an existing profile only when all of these are true:

1. the player is present in a bot-enabled session in the same Zalo group/connection;
2. the realtime sender display name is an exact normalized match;
3. exactly one distinct blank-UID player profile matches;
4. the UID is not already attached to another profile;
5. no different existing UID is overwritten.

Ambiguous or conflicting identity never becomes self-service automatically.

Delegated requests that operate only on other members continue through the existing operator/group-role authorization gate.

## Permission management from Zalo

The commands below use the existing `BotOperatorZaloUserIdsJson` field as their source of truth, so the website and Zalo chat remain consistent:

- `@Npc cấp quyền cho @Tùng`
- `@Npc thu quyền @Tùng`
- `@Npc ai đang có quyền?`

Grant/revoke requires the caller to pass the live Zalo group-role authorization check. An ordinary configured operator may operate the bot but may not create additional operators. Permission changes are applied consistently to bot-enabled sessions for the same Zalo group/connection.

## Conversation tone

Short plain-text wake turns such as `Bot ơi` are answered by the deterministic casual wake response before Social AI. This prevents the bot from drifting into formal customer-service phrasing such as `Dạ, em đây ạ...` when the product persona should feel like another member in the club.

## Safety invariants

- Database/poll state remains authoritative.
- Ambient assistance never performs a write.
- Self-service requires stable sender identity; name-only ambiguity never authorizes mutation.
- A different stored UID is never replaced silently.
- Delegated member changes still require authorization.
- Only live Zalo group-role authority can grant or revoke configured operators.
- Duplicate webhook delivery remains protected by the existing message/idempotency infrastructure.
