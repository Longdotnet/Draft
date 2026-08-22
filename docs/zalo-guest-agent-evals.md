# Zalo Guest Agent conversation evals

This suite is the regression contract for the client-like recruitment/guest agent. It is intentionally narrower than the whole backend test suite and is designed to be cheap enough to run while changing guest semantics, task memory, recruitment or roster-event behavior.

Run it with:

```bash
dotnet test server/VolleyDraft.Api.Tests/VolleyDraft.Api.Tests.csproj --filter Category=GuestAgentEval
```

## Rules for new agent features

1. AI may interpret language, but only grounded backend state grants mutation authority.
2. A direct mention or ordinary group message must never become outside-group guest mutation authority.
3. Immediate outside-group add requires a grounded recruitment-broadcast reply and the live guest-signup window.
4. Conditional language such as `nếu 19h vẫn thiếu thì +2` is a durable future condition, never an immediate +2 fallback.
5. Tentative guest intent does not occupy a roster slot. Confirmation re-reads live capacity and decides Active versus Waitlisted.
6. Replacement is one atomic domain action; there must not be an observable temporary recruitment hole.
7. Reservation IDs supplied by semantic AI are accepted only when they belong to the grounded sponsor/session snapshot.
8. Existing waitlist has priority over later conditional additions when capacity becomes available.
9. Poll/roster changes are observed separately from notification cooldown. Debounce must suppress a drop-and-recover bounce and coalesce multiple drops.
10. A same-count roster fingerprint change is a replacement/change event, not a missing-slot incident.
11. Conditional/local clock interpretation is resolved against the grounded session and Vietnam time, not an arbitrary AM/PM guess.
12. Any technical AI fallback must fail closed rather than convert conditional/ambiguous language into a different mutation.

## Scenario catalog

| ID | Conversation / world event | Required invariant |
| --- | --- | --- |
| E01 | direct `+1` without recruitment reply | reject mutation authority |
| E02 | reply `+2` before guest window | reject immediate add |
| E03 | `nếu 19h vẫn thiếu thì +2` | legacy parser cannot produce immediate Add |
| E04 | same conditional phrase on grounded recruitment reply | schedule is valid even before add window; non-recruitment anchor fails |
| E05 | AI returns fabricated reservation ID | target is rejected against grounding snapshot |
| E06 | `chắc Minh đi` then `Minh chốt đi` | tentative leaves roster unchanged; confirmation occupies one slot |
| E07 | `Minh nghỉ, Huy thay Minh` plus retry | stable roster count and no duplicate replacement |
| E08 | 18→17→18 inside debounce | no recruitment incident |
| E09 | 15→14→13 inside debounce | one coalesced 15→13 incident |
| E10 | count remains 15 but fingerprint changes | no missing-slot incident |
| E11 | `nếu 7h...` for evening session after morning 07:00 | resolve to 19:00 local when that is the valid pre-match interpretation |
| E12 | structured planner JSON for conditional action | remains ScheduleConditionalGuests through parse + validation |

The catalog should grow whenever a production bug reveals a new invariant. A bug fix is incomplete until its reproducer becomes either a `GuestAgentEval` scenario or a lower-level test referenced by a scenario here.
