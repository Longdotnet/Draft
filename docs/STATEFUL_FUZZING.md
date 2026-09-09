# Stateful Fuzzing

VolleyDraft uses stateful fuzzing to turn production incidents and realistic client behavior into continuously reusable executable pressure on domain invariants.

The goal is not random input for its own sake. A fuzz finding counts only when a reproducible scenario violates a grounded invariant or produces a deterministic failure in real application/domain code.

## Core loop

```text
seed corpus
  -> deterministic mutation
  -> execute real code behind test doubles at external boundaries
  -> evaluate invariants after state transitions
  -> reproduce
  -> deduplicate
  -> minimize
  -> permanent regression
  -> systemic fix
  -> replay target and adjacent corpus
```

A confirmed failure is never discarded after the production fix. Its minimized reproducer becomes permanent executable knowledge.

## Foundation

The shared test foundation under `server/VolleyDraft.Api.Tests/Fuzzing/Core` provides:

- a scenario with a stable seed and ordered actions;
- a target abstraction that creates state, applies real actions and evaluates invariants;
- deterministic seed replay using a repository-owned xorshift32 PRNG rather than relying on `System.Random` implementation stability;
- sequence mutation operators for insertion, deletion, duplication, replacement and reordering;
- a runner that records the first grounded invariant violation or exception;
- a stable failure fingerprint;
- sequence minimization that removes actions while preserving the same fingerprint;
- JSON reproducer serialization.

The foundation intentionally has no dependency on a fuzzing framework package. Domain-specific targets can grow independently while keeping replay semantics stable.

## Initial corpus

The first production seed is the Auto Session poll-date incident where `Chủ nhật 13/9` must never resolve to `06/09`.

`ZaloPollScheduleFuzzTests` mutates only semantics-preserving forms that the parser contract already supports:

- `CN`, accented and non-accented `Chủ nhật`, and case variants;
- date-first and weekday-first ordering;
- `/`, `-` and `.` calendar separators;
- supported whitespace around separators;
- yearless, two-digit-year and four-digit-year forms;
- equivalent explicit/default `17:45` time forms.

The target calls the production `ZaloPollScheduleParser`. It checks that:

- valid explicit dates remain authoritative;
- the candidate still passes source-consistency validation;
- weekday/date conflicts fail closed;
- invalid explicit dates fail closed;
- New Year forward resolution remains deterministic.

A failed assertion prints the seed and mutated option text so the exact case can be replayed.

## Invariant families

Stateful targets should prefer invariants over expected-message snapshots. High-value families include:

- authorization and account/group isolation;
- explicit date/time provenance;
- one logical mutation under duplicate/concurrent confirmation;
- slot ownership/pass/share/claim consistency;
- conversation ownership and stale-prompt isolation;
- message/idempotency correctness under duplicate and out-of-order delivery;
- restart/deploy persistence and reconciliation;
- reminder/expiry/time-boundary behavior in `Asia/Ho_Chi_Minh`;
- deterministic authority when AI is disabled, unavailable, rate-limited, malformed or contradictory.

## Mutation dimensions

Fuzz targets should evolve beyond language-only variation:

1. language — accents, no accents, shorthand, whitespace, typos, mention/reply forms;
2. sequence — insert/delete/repeat/reorder actions and topic changes;
3. time — exact thresholds, midnight/week/year boundaries and delayed messages;
4. failure — AI/persistence/send/restart failures;
5. concurrency — competing admins/users/workers and stale versions;
6. cross-feature — Auto Session, roster, pass/share/claim, team preference, poll sync, draft and reminders.

## External safety

Fuzz exploration must not send real Zalo traffic, log in to a real Zalo account or hammer Render/provider endpoints.

External boundaries use deterministic fakes/test doubles. The code behind those boundaries should remain the same production application/domain code wherever practical.

## Campaign rule

Each autonomous fuzz campaign owns one coherent target until it is either:

- merged with a confirmed minimized regression and fix; or
- completed as a substantial fuzz-system improvement when no real bug was found.

Do not manufacture a bug to justify a run. If a campaign stays green, improve the oracle, state model, corpus, mutation operators, minimizer or reachable lifecycle coverage.
