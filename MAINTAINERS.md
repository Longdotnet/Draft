# Maintainers

## Primary maintainer

- **Longdotnet** — project direction, backend/frontend integration, production reliability, Zalo workflows, regression/fuzz coverage, documentation and releases.

## Maintenance model

Draft is maintained around observable user workflows and durable invariants rather than feature count.

When a production or concurrency failure is confirmed, the preferred sequence is:

1. reduce the failure to a reproducible scenario;
2. state the invariant that was violated;
3. add a regression or deterministic fuzz case;
4. fix the systemic ownership/state boundary;
5. run adjacent and full checks before merge;
6. keep AI outside the authority path.

Examples of invariants used in maintenance work include:

- one player must not become authoritatively owned by two shared slots;
- a rejected mutation must not leak stale EF-tracked state into a later save;
- terminal delivery state must not resurrect after retry or restart;
- overlapping API instances must not both win the same authoritative claim;
- AI provider failures must degrade to deterministic application output rather than replace business truth.

## Contribution expectations

Contributors do not need to follow the same implementation approach, but changes should preserve the public behavior and invariants described in [CONTRIBUTING.md](CONTRIBUTING.md).

## Project stage

Draft is an actively evolving `0.1.0` project. Compatibility may still change while workflows are being hardened, but reliability regressions should be treated as bugs rather than expected MVP behavior.