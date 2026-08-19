# Auto Session Trusted Backups

Phase B replaces the unsafe assumption that every Zalo admin is an Auto Session operator.

Policy:
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
