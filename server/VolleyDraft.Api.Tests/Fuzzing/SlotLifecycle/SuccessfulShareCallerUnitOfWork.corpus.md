# Successful share caller unit-of-work corpus

Candidate Slot Lifecycle failure class for command isolation around a successful pre-draft share.

Stable fingerprint: `slot-lifecycle:successful-share-must-not-commit-caller-uow`.

Grounded invariant:
- a successful `SharePreDraftSlotAsync` may durably commit its own roster/share mutation;
- unrelated `Added`/`Modified`/`Deleted` state already staged by the caller on the shared request `DbContext` must not become durable merely because the share command calls `SaveChanges` internally.

Minimal executable scenarios:
1. seed a pre-draft session with present `Anchor` and absent `Returning`;
2. stage unrelated caller-owned state without saving it;
3. successfully share `Anchor + Returning`;
4. reread through a separate `DbContext` before caller save;
5. require the returning player to be durable/present while caller-owned state remains non-durable.

The first executable reproducers cover both caller-owned `Added User` and `Modified MatchSession.Name` state. If confirmed, expand mutations to `Deleted` state and restart/save interleavings, then promote the minimized failure and systemic fix.
