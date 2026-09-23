# Draft 0.1.0 — release notes draft

> Reviewed against `main` on 2026-09-23. Claims below were checked against the repository's implementation/tests and current public screenshots.

> Prepared for the first public GitHub Release. The source package already declares version `0.1.0`; publishing the GitHub release/tag is a separate repository action.

## From a Zalo poll to balanced teams

Draft 0.1.0 is the first public release baseline for the project: an open-source volleyball session organizer built around a real weekly workflow rather than a generic tournament template.

The public screenshots in the repository show one operating workflow with Zalo polls at **33** and **39** voters and an **18-effective-slot** weekly session. These numbers document a real workflow; they are not presented as broad-adoption metrics.

### Highlights

- **Zalo poll → roster** — import attendance instead of copying voters by hand.
- **Balanced drafting** — captain selection and slot-aware team drafting use player profile data.
- **One-phone court flow** — captains can draft without individual accounts or multiple devices.
- **Shared / rotating slots** — model players who share one effective team slot.
- **Automation with human fallback** — reminders and lifecycle automation handle routine cases while exceptions are surfaced to the organizer.
- **Backend-authoritative state** — the database/application layer owns business truth, authorization and mutation.
- **AI remains optional** — provider failure or malformed AI output must not replace deterministic business answers.

### Reliability work

The 0.1.0 baseline includes substantial regression and fuzz coverage around:

- stale EF tracked state;
- shared-slot ownership races;
- multi-instance claims;
- inbound delivery retries and terminal non-resurrection;
- Auto Session cross-group ownership/isolation;
- AI provider failure and failover behavior;
- restart-shaped lifecycle transitions.

### Contributor entry points

- Mock Zalo bridge for development without real credentials.
- SQLite local mode.
- Contributor guide and issue templates.
- Security policy with explicit private-data boundaries.

### Known project stage

This is still a `0.1.0` project. User workflows are real and actively maintained, but compatibility may continue to evolve while the system is hardened. The public repository currently has limited adoption signals, so release notes intentionally avoid claims of broad usage.

### Links

- README: `README.md`
- Changelog: `CHANGELOG.md`
- Contributor guide: `CONTRIBUTING.md`
- Maintenance model: `MAINTAINERS.md`
- Security policy: `SECURITY.md`
- Vietnamese user guide: `docs/USER_GUIDE_VI.md`