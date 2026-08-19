# Auto Session V3 — Multi-admin / non-user organizer plan

## Problem

A Zalo group can have many creator/admin accounts, but **Zalo admin is not the same thing as an Auto Session operator**.

Some admins may never use the website or understand the bot conversation. If every current Zalo admin can mutate the same draft, ordinary replies such as `ok`, jokes, or comments about a day can steal ownership, reset reminders, or make the bot answer the wrong person.

The safe model is:

> one poll = one active human owner; everybody else is a bystander unless an explicit trusted-takeover rule allows them in.

## Phase A — single-owner gate (implemented in PR #68)

1. Poll creator starts as `ActiveOrganizerId`.
2. The active organizer can continue naturally.
3. Another current Zalo admin does **not** gain ownership merely by replying to the bot.
4. Before escalation:
   - `ok`, `ừ`, jokes, `T6 thôi`, `sân A`, etc. from another admin do not modify the draft;
   - ordinary bystander replies are silently ignored;
   - an explicit early `nhận xử lý` / `để tui xử lý` gets one deterministic explanation, but ownership does not move.
5. A bystander reply never changes `ActiveOrganizerId`, never resets reminders, and never changes the draft.
6. Non-admin members are silent bystanders for Auto Session.

## Interim trusted fallback — implemented in PR #68

Until the explicit Trusted Backup UI exists, the current fallback policy is deliberately narrow:

- the poll creator is the active owner;
- **only the current Zalo group creator (trưởng nhóm) is trusted as takeover fallback**;
- ordinary phó nhóm/admin accounts are not Auto Session operators just because Zalo grants them admin rights;
- after escalation, an untrusted admin still cannot take over with `T6 thôi`, `tạo đi`, `nhận xử lý`, `ok`, etc.;
- the group creator fallback must explicitly address the bot and send either a concrete Auto Session action or an explicit takeover phrase;
- if the current owner loses creator/admin rights, the group creator fallback may take over immediately through a deliberate bot-addressed action;
- if the group creator is already the active owner, or no distinct trusted fallback exists, the bot does **not** mention every admin; it stays quiet until expiry.

Current reminder flow:

```text
poll creator = active owner
        ↓
30m silence
        ↓
remind active owner only
        ↓
+150m silence
        ↓
if distinct group creator exists:
  mention group creator only
else:
  no group-wide admin escalation
        ↓
24h expiry
        ↓
no automatic website creation
```

## Phase B — trusted Auto Session operators (next implementation phase)

Do not permanently use the full Zalo admin list as the operator list. Add a per-group trusted operator policy:

- `PollCreator`: primary owner for the poll while still a current creator/admin.
- `TrustedBackup`: explicitly configured person who understands the bot and may take over after escalation.
- `ZaloAdminBystander`: current Zalo admin but not configured as Auto Session operator.
- `Member`: normal group member.

Suggested persistence:

```text
ZaloAutoSessionTrustedOrganizers
- Id
- TrackedGroupId
- ZaloUserId
- DisplayName
- Role              Primary | Backup
- Enabled
- AddedByUserId
- CreatedAt
- UpdatedAt
UNIQUE (TrackedGroupId, ZaloUserId)
```

Trust must be changed by a human admin from the web operations UI. AI/chat behavior must never auto-promote somebody into the trusted list.

## Phase C — trusted-only escalation

After the trusted-operator table exists:

1. first reminder -> active owner only;
2. escalation -> enabled `TrustedBackup` operators only;
3. if no trusted backup exists -> do not mention all Zalo admins; keep pending until expiry;
4. if owner loses Zalo admin role -> a trusted backup may explicitly take over;
5. if every trusted operator is unavailable -> expire safely, never auto-create.

The current group-creator-only fallback is the safe bridge to this phase.

## Phase D — noise suppression

The bot should prefer silence over confusing the group.

| Sender | Message | Behavior |
|---|---|---|
| Active owner | short natural follow-up | process in context |
| Other admin before escalation | random reply / `ok` / joke | silently ignore |
| Other admin before escalation | `T6 thôi` | silently ignore |
| Trusted fallback before escalation | `nhận xử lý` | explain ownership once; no takeover |
| Untrusted admin after escalation | any action | silently ignore for Auto Session |
| Trusted fallback after escalation | `ok` / joke | silently ignore |
| Trusted fallback after escalation | concrete action / `nhận xử lý` | takeover + process |
| Normal member | reply to preview | silent for Auto Session |

Only an explicitly addressed, authorized, conversation-relevant message should produce an Auto Session response.

## Phase E — audit and observability

Record routing outcomes without spamming Zalo:

- `active_owner_processed`
- `bystander_ignored`
- `early_takeover_rejected`
- `trusted_takeover_accepted`
- `untrusted_admin_ignored`
- `owner_role_lost`
- `conversation_expired_no_operator`

Expose aggregate counts in Auto Session Operations so the web admin can see whether routing configuration is too broad without reading raw logs.

## Acceptance tests

Required cases:

1. poll creator + 5 other admins; other admins chat normally -> creator remains owner;
2. another admin replies `ok` -> no ownership change;
3. another admin replies `T6 thôi` before escalation -> no draft change;
4. another admin says `nhận xử lý` before escalation -> no takeover;
5. after escalation, an untrusted phó/admin sends `T6 thôi`, `tạo đi`, or `nhận xử lý` -> no takeover;
6. after escalation, group creator replies `ok` / joke -> no takeover;
7. after escalation, group creator replies a concrete action or `nhận xử lý` -> ownership moves once;
8. original owner loses admin role -> group creator can explicitly take over;
9. two authorized execution attempts race -> only one can win via existing CAS/version + option idempotency;
10. no distinct trusted fallback -> no mention-all-admins; conversation expires without auto-create.

## Rollout order

1. Keep the single-owner + group-creator-only fallback in PR #68.
2. Canary one real group in `PreviewOnly` and intentionally let multiple admins reply around the preview.
3. Verify ignored messages do not change owner/draft/reminder timestamps.
4. Implement Phase B trusted operator persistence + web operations UI.
5. Replace group-creator-only fallback with configured Trusted Backups.
6. Add routing audit counters in Operations.
7. Run another PreviewOnly canary.
8. Move one group to `Live` only after routing/audit behavior is clean.
