# Failed share rollback state corpus

Permanent reproducer for `slot-lifecycle:failed-share-must-not-leak-roster-state`.

Initial state:
- one present anchor player;
- one existing returning player with `IsPresent=false`;
- pre-draft session using the real `SessionDraftService` and SQLite persistence.

Minimized action sequence:
1. reject `SharePreDraftSlotAsync(Anchor, [Returning, Anchor])` because the second participant is the anchor itself;
2. execute an unrelated session update that calls `SaveChanges`;
3. reread the returning player from a separate DbContext.

Invariant: a rejected share must leave the durable returning player absent. Database rollback alone is insufficient if the failed command leaves partial EF tracked mutations behind.

The executable corpus additionally mutates restart/unrelated-save prefixes across 128 deterministic seeds and requires the same stable fingerprint before promotion.
