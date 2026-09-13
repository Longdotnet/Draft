# Failed share rollback state corpus

Permanent reproducer family for rejected pre-draft share transaction isolation.

Stable roster fingerprint: `slot-lifecycle:failed-share-must-not-leak-roster-state`.

Initial state:
- one present anchor player;
- one existing returning player with `IsPresent=false`;
- pre-draft session using the real `SessionDraftService` and SQLite persistence.

Minimized rejected-share action sequence:
1. reject `SharePreDraftSlotAsync(Anchor, [Returning, Anchor])` because the second participant is the anchor itself;
2. execute an unrelated save;
3. reread durable state from a separate DbContext.

Permanent invariants:
- rejected share state must not later resurrect the absent returning player;
- caller-owned `Modified` state staged before the share call must survive the rejection;
- caller-owned `Added` and `Deleted` entries staged before the share call must survive the rejection;
- only entities/values introduced by the rejected share may be discarded.

Root failure class: database transaction rollback does not restore EF Core tracked values, while clearing the entire tracker also destroys unrelated caller unit-of-work state. The command therefore snapshots the pre-call tracker and restores that exact state when the share transaction does not commit.

The executable corpus additionally mutates restart/unrelated-save prefixes across 128 deterministic seeds and requires the same stable roster fingerprint before promotion.
