# Zalo Member Assist + Chat Permissions

This rollout moves NPC from a command-only bot toward a useful group member while preserving domain truth and authorization boundaries.

## Member-assist rule

An ordinary unmentioned group message may trigger a **high-confidence help opportunity** when the meaning is useful. The first supported opportunity is a member clearly offering to pass their own slot, including common chat variants such as `pass slot` and `pass sỉ lót`.

The ambient helper may:

- identify the sender's current/upcoming session from authoritative roster data;
- ask which session when more than one is possible;
- open a group-scoped coordination offer for a verified member-owned slot;
- let another member naturally say `tui nhận`, `tui lấy`, `để tui`, or `tui nhận T6`;
- reserve that offer for one claimant and ask for the next safe step.

The ambient helper may write **coordination state only**. Opening or reserving an offer does not change roster, team, draft slot, preference, waitlist, profile, reminder, or poll state.

### Open-slot coordination

A typical conversation is:

1. Hoàng says `em pass slot T6 nha` without mentioning NPC.
2. NPC verifies Hoàng currently owns that participation and opens an `OpenSlotOffer` with a short TTL capped by session start.
3. Vivian says `tui nhận` without mentioning NPC.
4. NPC reserves the offer for Vivian. A second claimant cannot steal the same pending offer.
5. If the session is pre-draft (`Setup` / `CaptainSelection`), NPC does **not** modify registration. Hoàng must leave the linked poll option and Vivian must vote it; NPC only verifies the resulting authoritative roster after the sync.
6. If the session is already `Finished` but has not started, NPC previews the existing safe post-draft transfer and asks Vivian to say `chốt`.
7. Only that claimant confirmation may call the existing post-draft transfer service; the service revalidates source slot, target, session state, start time and action lease.

A member who already owns a pre-draft slot in that session cannot claim another open slot because poll membership represents one participation per member. They should use `ShareSlot` or a team-preference workflow instead if that is what they mean.

Claims are intentionally narrow. Generic chat such as `tui nhận xét team này đẹp` is not treated as a slot claim. When a claimant explicitly mentions a human, only an open offer owned by that mentioned user is eligible.

The owner may cancel an open pass before completion. A pending claimant may also cancel and release the offer for somebody else.

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
- Ambient assistance may persist coordination state, but it does not directly mutate domain roster/team/slot data.
- Pre-draft registration remains poll-authoritative.
- Post-draft open-slot transfer requires the reserved claimant to confirm and then goes through the existing safe transfer service.
- Offer claim uses version/status transitions so the first valid claimant wins and duplicate apply attempts do not perform duplicate domain writes.
- Self-service requires stable sender identity; name-only ambiguity never authorizes mutation.
- A different stored UID is never replaced silently.
- Delegated member changes still require authorization.
- Only live Zalo group-role authority can grant or revoke configured operators.
- Duplicate webhook delivery remains protected by the existing message/idempotency infrastructure plus offer/apply state transitions.
