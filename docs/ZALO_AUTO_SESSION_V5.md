# Zalo Auto Session V5 — Policy-driven Match Birth Autopilot

## Product invariant

Auto Session must remain useful when AI is disabled, unavailable, rate-limited, timed out, or returns malformed output.

AI may help interpret language. Deterministic backend state owns truth, authorization, policy, mutation, idempotency, and lifecycle ownership.

## Target lifecycle

```text
Zalo poll
  -> deterministic observation/classification
  -> authoritative schedule extraction
  -> durable MatchProposal revisions + provenance
  -> only genuinely missing/ambiguous clarification
  -> group-policy evaluation
  -> safe execution revalidation
  -> MatchSession create/link/sync
  -> durable HandedOff ownership to Match Lifecycle
```

`Created` is retained only where Conversation V3 compatibility still needs it. `HandedOff` is the conceptual successful terminal state.

## Authority order

For a field used during match birth, authority is intentionally explicit:

1. authoritative poll identity / option identity / explicit calendar evidence;
2. explicit organizer correction for a mutable field;
3. current admin-approved group default;
4. deterministic inference allowed by domain rules;
5. optional AI interpretation, which must still pass deterministic validation.

AI confidence never authorizes a write and never upgrades a guess into an approved default.

## Durable provenance

The V4 proposal ledger remains the durable source used by V5. Current provenance includes:

- option identity: `poll_option`;
- start time from an option: `poll_option_explicit_time`;
- start time from the poll title: `poll_title_explicit_time`;
- approved group default: `approved_group_default`;
- a preview value that no longer matches approved policy: `stale_or_unapproved_default`;
- organizer-owned edits: `organizer_correction` with actor and intent;
- initial selection: `poll_option_default_selected`.

The proposal source baseline is updated after accepted poll/policy reconciliation so restart/deploy does not repeat the same material diff forever.

## Approved-default policy gate

Immediately before mutation, Auto Session re-evaluates current group policy together with the durable proposal.

For location and team size:

- if the organizer did not change the field, the current admin-approved default owns the value;
- if the approved default changed after preview, the proposal is refreshed deterministically and the new source baseline is persisted;
- if the organizer explicitly corrected the field, that organizer decision is preserved instead of being overwritten by a later default change;
- if the field still belongs to policy but no approved default exists, execution fails closed and the organizer is asked only for that missing decision.

This policy path is AI-free.

Example:

```text
preview source:   Sân UTE
organizer draft:  Sân UTE
current policy:   Sân B
=> safe policy refresh to Sân B
```

But:

```text
preview source:   Sân UTE
organizer draft:  Sân A
current policy:   Sân B
=> preserve Sân A (organizer correction owns the field)
```

If the current policy has no approved location and the organizer never corrected it, the bot must not silently reuse the old preview location. It asks only for the location (for example `sân UTE`) and keeps the rest of the proposal intact.

## Poll revalidation

Current poll state is fetched immediately before mutation.

The deterministic revalidation boundary owns:

- closed/anonymous poll failure;
- explicit date and weekday consistency;
- option identity;
- option addition/removal;
- source-time changes;
- vote-only drift;
- approved location/team-size policy refresh;
- organizer correction preservation.

Material poll changes still require the appropriate organizer confirmation. Safe approved-default refreshes are non-material policy changes and do not require another broad review.

## Restart and concurrency

Policy reconciliation is persisted in the same append-only proposal revision ledger.

A successful policy refresh becomes the next source baseline. After restart, the next revalidation observes the refreshed baseline and does not repeatedly rediscover the same change.

Conversation-version compare-and-swap and proposal revision uniqueness remain in force, so a stale organizer/process cannot overwrite a newer decision.

## Minimal clarification

A policy failure must ask only for the missing decision. Do not send a generic provider error or restart the entire poll conversation.

Examples:

- missing approved location -> ask for location or admin default;
- missing approved team size -> ask for team/capacity configuration or admin default;
- added poll option -> leave it unselected and offer the current deterministic selector;
- explicit poll time changed -> show the new time and supported correction syntax;
- unsupported natural wording with AI unavailable -> present grounded current choices/syntax.

## Lifecycle handoff

After match creation/linking, Auto Session records durable proposal-level ownership handoff to Match Lifecycle. Partial handoff fails closed. Recovery may retry missing handoffs by proposal + session identity; another proposal cannot borrow a lifecycle snapshot merely because it references the same session.

Once the proposal is authoritatively `HandedOff`, post-creation recruiting, waitlist/pass-slot, profile completion, draft readiness, and drafted-state behavior belongs to Match Lifecycle rather than Auto Session.

## Next policy work

The remaining policy work should continue to reduce organizer effort without weakening authority boundaries:

- make start-time/default-capacity policy semantics as explicit as location/team-size policy;
- distinguish manual-confirm versus intentionally pre-authorized group modes in persisted admin policy;
- extend deterministic clarification to current grounded candidate selectors rather than phrase piles;
- grow production conversation evals for policy change, restart, AI-off, stale prompts, and concurrent organizers.
