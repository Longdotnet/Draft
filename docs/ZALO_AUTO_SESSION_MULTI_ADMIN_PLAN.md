# Auto Session V3 — Multi-admin / non-user organizer plan

## Problem

A Zalo group can have many creator/admin accounts, but **Zalo admin is not the same thing as an Auto Session operator**.

Some admins may never use the website or understand the bot conversation. If every current Zalo admin is allowed to mutate the same draft, ordinary replies such as `ok`, jokes, or comments about a day can steal conversation ownership, reset reminders, or make the bot answer the wrong person.

The safe model is therefore:

> one poll = one active human owner; other admins are bystanders unless a controlled takeover is allowed.

## Phase A — single-owner conversation gate (implemented in PR #68)

Rules:

1. Poll creator starts as `ActiveOrganizerId`.
2. The active organizer can continue the conversation normally.
3. Another current Zalo admin does **not** gain ownership just by replying to the bot.
4. Before escalation:
   - `ok`, `ừ`, jokes, `T6 thôi`, `sân A`, etc. from another admin do not modify the draft;
   - normal bystander messages are silently ignored;
   - an explicit `nhận xử lý` / `để tui xử lý` gets one deterministic explanation that the poll is still owned by someone else.
5. After escalation:
   - another current admin still cannot claim ownership with `ok`, `ừ`, `haha`, etc.;
   - takeover is allowed only when the admin explicitly addresses the bot and sends either a concrete Auto Session action (`T6 thôi`, `T6 18h`, `sân A`, `tạo đi`, `bỏ CN`) or an explicit takeover phrase (`nhận xử lý`).
6. If the current owner is no longer a current creator/admin, a current admin can take over immediately, but only through a deliberate bot-addressed action.
7. Non-admin members are bystanders and do not create extra rejection chatter in the group.
8. A random bystander reply never changes `ActiveOrganizerId`, never resets the reminder clock, and never changes the draft.

## Phase B — trusted Auto Session operators (next hardening phase)

Do not treat all Zalo admins as Auto Session operators.

Add a per-group trusted operator policy:

- `PollCreator`: the current poll creator remains the primary owner when they are still a current creator/admin.
- `TrustedBackup`: explicitly configured people who understand the bot and may take over after escalation.
- `ZaloAdminBystander`: current group admin but not configured as an Auto Session operator; cannot mutate/take over the Auto Session draft.
- `Member`: normal member; ignored by the Auto Session authorization layer.

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

Do not auto-promote somebody into this table from AI/chat behavior. Trust changes require a human admin action from the web operations UI.

## Phase C — escalation only to trusted backups

Current V3 escalation mentions current creator/admin accounts. After the trusted-operator table exists:

1. first reminder -> active owner only;
2. escalation -> enabled `TrustedBackup` organizers only;
3. if there are no trusted backups -> do not mention every Zalo admin; keep the draft pending until expiry;
4. if owner loses Zalo admin role -> first available trusted backup may explicitly take over;
5. if every trusted operator is unavailable -> expire safely, never create automatically.

This prevents a large admin list from turning the bot conversation into a group discussion.

## Phase D — noise suppression / addressing policy

The bot should prefer silence over adding confusing messages.

| Sender | Message | Behavior |
|---|---|---|
| Active owner | short natural follow-up | process in context |
| Other admin before escalation | random reply / `ok` / joke | silently ignore |
| Other admin before escalation | `T6 thôi` | silently ignore |
| Other admin before escalation | `nhận xử lý` | explain ownership once; no takeover |
| Trusted backup after escalation | `ok` / joke | silently ignore |
| Trusted backup after escalation | concrete action | takeover + process |
| Untrusted Zalo admin | any Auto Session action | do not mutate; usually silent |
| Normal member | reply to preview | silent for Auto Session |

Only an explicitly addressed, authorized, conversation-relevant message should produce a response.

## Phase E — audit and observability

Record routing outcomes without spamming Zalo:

- `active_owner_processed`
- `bystander_ignored`
- `early_takeover_rejected`
- `trusted_takeover_accepted`
- `untrusted_admin_ignored`
- `owner_role_lost`
- `conversation_expired_no_operator`

Expose aggregate counts in Auto Session Operations so admins can see whether group configuration is too broad without reading raw logs.

## Acceptance tests

Required cases:

1. poll creator + 5 other admins; other admins chat normally -> draft stays owned by creator;
2. another admin replies `ok` to preview -> no ownership change;
3. another admin replies `T6 thôi` before escalation -> no draft change;
4. another admin says `nhận xử lý` before escalation -> no takeover;
5. after escalation, random `haha` / `ok` -> no takeover;
6. after escalation, trusted backup replies `T6 thôi` -> ownership moves once and draft changes once;
7. two trusted backups reply simultaneously -> only one execution can win via existing CAS/version guard;
8. original owner loses admin role -> trusted backup can explicitly take over;
9. untrusted Zalo admin can never create a website session;
10. no trusted backup -> conversation expires without group-wide admin noise and without auto-create.

## Rollout order

1. Keep the Phase A single-owner gate in PR #68.
2. Canary one real group in `PreviewOnly` and intentionally let multiple admins reply around the preview.
3. Confirm ignored messages do not change owner/draft/reminder timestamps.
4. Add Phase B trusted operators to the web operations UI.
5. Change escalation from all current admins to trusted backups only.
6. Run another PreviewOnly canary.
7. Move one group to `Live` only after routing/audit behavior is clean.
