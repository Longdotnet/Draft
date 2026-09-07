# Zalo Auto Session V4 — Durable MatchProposal Conversation

## Version progression

Auto Session evolves sequentially. V3 is not renamed to V5 and V5 must not be claimed complete while the V4 contracts below are still missing.

```text
V3 — Stateful Conversation
  natural organizer dialogue
  persisted conversation draft
  deterministic final authorization

        ↓

V4 — Durable MatchProposal Conversation
  authoritative persisted proposal revisions
  field provenance
  restart/concurrency recovery
  poll reconciliation
  approved defaults
  minimal deterministic clarification

        ↓

V5 — Policy-driven Match Birth Autopilot
  policy evaluation
  safe/pre-authorized execution where configured
  material-diff decisions
  explicit Match Lifecycle handoff
```

V3 remains the compatibility surface while V4 is introduced incrementally. A V4 implementation may reuse V3 conversation states and renderers, but the mutable conversation JSON must no longer be the only durable source of match-birth decisions.

## Product goal

An organizer should be able to create a normal volleyball poll and continue naturally even if the process restarts or AI is unavailable.

Example source:

```text
Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00

T3 8/9
T4 9/9
T6 11/9
CN 13/9
```

The system must retain not only the values it plans to use, but also **why each value is trusted**.

## V4 source hierarchy

For authoritative facts and safe defaults:

1. explicit poll identity / option identity / calendar evidence;
2. explicit organizer correction to a field the organizer is allowed to change;
3. persisted approved group defaults;
4. deterministic inference permitted by domain rules;
5. optional AI semantic interpretation, which is only a proposal for deterministic validation.

AI output never becomes authoritative merely because confidence is high.

## Durable MatchProposal revisions

V4 introduces an append-only proposal revision ledger:

`ZaloAutoSessionMatchProposalRevisionsV4`

Each revision retains:

- proposal / tracked-group / poll identity;
- revision number;
- Conversation V3 version that produced the revision;
- source poll question;
- source poll updated timestamp;
- source poll structure hash;
- normalized current draft;
- field provenance/evidence;
- deterministic draft fingerprint;
- organizer actor when applicable;
- change kind / interpreted intent;
- creation timestamp.

A revision is immutable. A new organizer correction appends a new revision rather than replacing history.

## Provenance

The first V4 slice records provenance for the decisions already represented in Conversation V3:

- option identity: `poll_option`;
- initial start time: `deterministic_poll_candidate`;
- initial selection: `poll_option_default_selected`;
- configured location: `approved_group_default` or `missing`;
- team size: `approved_group_default`;
- organizer edits to location/team-size/start-time/selection: `organizer_correction` with actor and intent.

The provenance model is intentionally explicit. Historical guesses, AI memory, or social chat are not approved defaults.

## Authority boundary

Conversation V3 remains a compatibility cache and user-facing state machine during migration.

For V4-enabled conversations:

```text
organizer turn
  -> V3 deterministic / optional-AI interpretation
  -> deterministic draft validation
  -> append V4 proposal revision
  -> mirror accepted V4 draft back to V3 cache
```

Safety-critical execution loads the latest V4 revision before creating `MatchSession` state.

If the legacy V3 `DraftJson` is stale after restart/deploy or cache drift, the V4 revision wins.

## Immutable poll-owned identity

Conversation/AI corrections must not silently rewrite the poll's authoritative option identity.

A V4 revision rejects a changed:

- option ID;
- option text;
- normalized day key;
- option set.

Time and selection may change only through the normal organizer workflow and remain subject to final poll/source validation before mutation.

## Concurrency

V4 uses two independent monotonic identities:

- proposal `Revision` — append-only durable revision number;
- Conversation V3 `Version` — the organizer conversation version that produced a changed draft.

A changed draft must advance beyond the conversation version stored by the latest proposal revision. An equal-version/different-payload write fails closed as a stale/conflicting writer.

Concurrent revision inserts use the unique `(ProposalId, Revision)` boundary and retry from the newly observed latest revision.

The existing final execution compare-and-swap and `(TrackedGroupId, PollId, OptionId)` session/link idempotency remain in force.

## Restart behavior

V4 proposal revisions live in the database and are not process-local.

After restart:

1. load the active Conversation V3 record;
2. load the latest V4 proposal revision;
3. hydrate the conversation draft from V4;
4. continue clarification/confirmation from the durable proposal;
5. final execution reads the same durable draft again.

Existing V3 conversations created before the V4 table existed are migrated lazily: the original preview becomes revision 1, then any already-modified current V3 draft can be appended as a newer revision.

## AI-off behavior

This V4 proposal layer has no AI dependency.

When AI is disabled, unavailable, rate-limited, timed out or malformed:

- poll identity and dates remain deterministic;
- approved defaults remain available;
- proposal revisions persist normally;
- supported deterministic corrections continue to work;
- unsupported language must degrade to grounded clarification/choices rather than provider errors.

## V4 completion criteria

V4 is **not complete** merely because the revision table exists.

Before moving the architecture target to V5, V4 should cover these product contracts:

1. durable proposal/revision source of truth with provenance;
2. V3 compatibility + restart recovery;
3. unified conversation ownership across neighboring workflows;
4. approved group defaults with explicit provenance and safe administration;
5. poll reconciliation / material-diff handling without blindly restarting the conversation;
6. minimal deterministic clarification and no-AI recovery choices;
7. stale/out-of-order/duplicate turn and multi-organizer concurrency tests;
8. production-style transcript/eval corpus for V4 behavior;
9. final execution revalidation against current authoritative poll/source evidence.

Only after these are solid should V5 add policy-driven Match Birth execution and make `HandedOff` to Match Lifecycle the conceptual happy terminal state.

## First V4 implementation slice

The first implementation slice establishes:

- append-only durable proposal revisions;
- poll snapshot retention;
- draft fingerprinting;
- field provenance;
- organizer-correction provenance;
- immutable poll-owned option identity guard;
- stale/equal-version write fencing;
- V3 draft cache synchronization;
- execution-time hydration from the latest V4 revision;
- restart/recovery regression coverage.

The next highest-value V4 campaign is **poll reconciliation / material diff**: when the source poll changes after preview, compare the new authoritative snapshot with the durable proposal, preserve still-valid organizer choices, and ask only for decisions invalidated by the material change.