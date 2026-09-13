# Rejected new-participant share corpus

Permanent Slot Lifecycle campaign for rejected `+2 share` state that touches identity/profile state before a later participant invalidates the command.

Stable fingerprint: `slot-lifecycle:failed-share-must-not-leak-new-identity`.

Minimized scenario:
1. pre-draft session contains present `Anchor`;
2. call `SharePreDraftSlotAsync(Anchor, [New Partner(uid), Anchor])`;
3. participant 1 causes real `PlayerProfile` + `SessionPlayer` tracking;
4. participant 2 rejects the command because the anchor cannot share with itself;
5. execute restart and/or unrelated save;
6. verify no rejected profile/player became durable.

Adjacent permanent oracle:
- when participant 1 resolves an existing profile and temporarily enriches avatar/default role/default level/sync timestamps, a later rejection must restore the durable profile exactly and must not add a session player.

Mutation dimensions:
- 128 deterministic seeds;
- restart vs unrelated-save prefixes;
- optional restart after rejection;
- delayed unrelated save after the rejected command.

This corpus uses the real `SessionDraftService`, EF Core tracking and SQLite persistence; only external boundaries are absent. A failure is promotable only when replay preserves the stable fingerprint three times.
