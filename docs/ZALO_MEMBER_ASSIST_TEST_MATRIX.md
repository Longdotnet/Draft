# Zalo member-assist regression matrix

Required before merge:

- `Bot ơi` uses casual teammate tone, not customer-service `Dạ/em` phrasing.
- `Em pass sỉ lót tối nay á` is recognized as a read-only pass-slot help opportunity.
- Ambiguous multiple owned sessions cause a clarification instead of guessing.
- A pass-slot message that explicitly mentions another human is not assumed to be the sender's own slot.
- A unique blank-UID player profile may be linked to the realtime sender UID only by exact same-group identity; duplicate names remain ambiguous and an existing different UID is never overwritten.
- `xếp tui chung team với @To An ...` keeps `tui` as the requester and the structured mention UID as the partner.
- Self share-slot language keeps the sender as anchor.
- Zalo operator grant/revoke updates the existing `BotOperatorZaloUserIdsJson` source consistently across the group's bot-enabled sessions.
- Repeating a grant is idempotent.
- An ordinary configured operator cannot grant more operators without live Zalo group-role authority.
- Ambient member-assist tests verify no roster/team mutation occurs.
- Full backend xUnit suite, frontend build, Zalo bridge checks and Docker validation remain green in CI.
