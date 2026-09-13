# Successful share caller unit-of-work corpus

Confirmed Slot Lifecycle failure class for command isolation around a successful pre-draft share.

Stable fingerprint: `slot-lifecycle:successful-share-must-not-commit-caller-uow`.

Grounded invariant:
- a successful `SharePreDraftSlotAsync` may durably commit its own roster/share mutation;
- unrelated `Added`/`Modified`/`Deleted` state already staged by the caller on the shared request `DbContext` must not become durable merely because the share command calls `SaveChanges` internally;
- after the command returns, the caller-owned pending state must still be pending so an explicit later caller save can persist it exactly once.

Minimized executable scenario:
1. seed a pre-draft session with present `Anchor` and absent `Returning`;
2. stage one unrelated caller-owned mutation without saving it;
3. successfully share `Anchor + Returning`;
4. reread through a separate `DbContext` before caller save;
5. require the returning player to be durable/present while caller-owned state remains non-durable;
6. require the original shared `DbContext` to still hold the caller mutation in its original pending state;
7. explicitly save from the caller and require that mutation to become durable only then.

Permanent mutation families:
- caller-owned `Added User`;
- caller-owned `Modified MatchSession.Name` on the same aggregate touched by the share command;
- caller-owned `Deleted User`;
- durable reread before caller save;
- explicit caller save after the share as the progression check.

The pre-fix head deterministically flushes caller state during the successful share command because its internal `SaveChangesAsync` writes every dirty entry on the shared EF `ChangeTracker`. The systemic fix must isolate command-owned persistence without dropping caller state and without weakening rejected-share rollback coverage.
