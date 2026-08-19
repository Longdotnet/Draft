# Auto Session Trusted Backups

Phase B replaces the unsafe assumption that every Zalo admin is an Auto Session operator.

## Implemented policy

- poll creator remains the active owner for the poll they created while they are still a current creator/admin;
- current Zalo group creator is a default fallback;
- additional phó/admin accounts must be explicitly enabled as `Trusted Backup` from Auto Session Operations;
- normal Zalo admins remain bystanders and cannot mutate/take over a draft;
- a Trusted Backup must still explicitly address the bot and send a concrete action or takeover phrase;
- trust is rechecked again immediately before website creation;
- removing Trusted Backup access blocks a non-original organizer from creating even if they had taken over earlier;
- escalation targets only the group creator and enabled Trusted Backups, never the full admin list;
- if no trusted fallback exists, the conversation remains quiet until expiry and never auto-creates.

Trust is a human web-admin decision. AI/chat behavior never grants operator access.

## Persistence

`ZaloAutoSessionTrustedOrganizers` stores trust per tracked group with a unique `(TrackedGroupId, ZaloUserId)` key. Zalo IDs are normalized before persistence. A saved Trusted Backup can be disabled without deleting its audit metadata.

## Operations UI

The `Auto Session Operations` panel now shows current organizer candidates:

- `Creator` → `Fallback mặc định`; no toggle is required;
- current Zalo `Admin` → can be explicitly toggled `Trusted Backup` ON/OFF;
- a previously trusted user who is no longer a current creator/admin is shown as stale so the web admin can disable the old trust.

Enabling trust is accepted only when the Zalo connection is currently connected and the target is a current group creator/admin. The group creator cannot be redundantly added as Trusted Backup because that account already has fallback status.

## Conversation routing

```text
poll creator
   ↓ owns conversation
other ordinary admins
   ↓ ignored by Auto Session
30m silence
   ↓ remind active owner only
+150m silence
   ↓ mention only current group creator + enabled Trusted Backups
trusted fallback replies to bot with concrete action / “nhận xử lý”
   ↓ takeover allowed
final create
   ↓ recheck current Zalo role + trust + Live rollout + poll structure + CAS/idempotency
```

Short chatter such as `ok`, `ừ`, jokes, or unrelated comments never claims ownership for another admin.

## Validation

- organizer-routing tests cover active owner, untrusted admin, trusted takeover, chatter rejection, lost-role takeover, and non-addressed messages;
- trusted-organizer store tests cover explicit enable/disable, Zalo ID normalization, and per-group isolation;
- full backend suite passed with `713/713` tests after Trusted Backup backend integration;
- Trusted Backup Operations UI passed the production frontend build before being committed.

## Canary checklist

1. Keep the real test group in `PreviewOnly`.
2. Configure only one known phó/admin as `Trusted Backup`.
3. Have several other Zalo admins reply `ok`, `T6 thôi`, jokes, and unrelated messages around the preview; owner/draft/reminder state must not move to them.
4. Let the active owner remain silent through escalation; only the group creator and configured Trusted Backup should be addressed.
5. Confirm random chatter from the fallback does not claim ownership.
6. Reply from the Trusted Backup with `nhận xử lý` or a concrete change; ownership may move once.
7. Disable that Trusted Backup before final create and verify the backend blocks the write.
8. Re-enable trust, create a fresh conversation, and verify the normal final confirmation path creates once.
9. Only after these checks are clean should the group move from `PreviewOnly` to `Live`.
