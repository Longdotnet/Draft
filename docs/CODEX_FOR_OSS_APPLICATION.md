# Codex for Open Source — Application Pack

> Prepared against the public repository state on 2026-09-23. Keep claims grounded in public evidence; do not inflate stars, adoption, contributors, or usage.

## Form selections

- **Role:** Primary maintainer
- **Interested in:** API credits for my project
- **Codex Security:** reasonable to select as well because the project handles authorization, provider credentials, webhook/internal keys, privacy boundaries and multi-instance mutation safety
- **Repository:** `https://github.com/Longdotnet/Draft`
- **OpenAI Organization ID:** fill from the applicant's OpenAI account before submission

## 1. Why does this repository qualify?

**407/500 characters**

```text
Draft is an actively maintained OSS organizer for real weekly volleyball groups. Public screenshots show Zalo polls with 33/39 voters and an 18-effective-slot session. As primary maintainer, I have 404 merged PRs in the repo and handle production incident triage, regression tests, stateful fuzzing, idempotency and multi-instance race fixes. It fills a practical gap for chat-first community sports groups.
```

### Evidence behind the claim

- GitHub maintainer activity query returned **412 PRs** by Longdotnet in this repository, **404 merged**.
- README screenshots publicly show Zalo poll cards with **33** and **39** voters.
- Match Autopilot screenshot publicly shows an **18/18 effective-slot** session.
- Production scheduler failures create/refresh public reliability incidents; recent incidents are triaged in Issues.
- Repository contains permanent stateful fuzz/regression coverage for stale EF state, shared-slot races, inbound restart/lease behavior and Auto Session isolation.

## 2. How will you use API credits?

**367/500 characters**

```text
I would use API credits for OSS maintenance: PR review, issue triage, bug minimization, targeted regression/fuzz-case generation, security and concurrency review, release preparation, and maintainer automation. Codex would reduce repetitive maintenance work while Draft’s .NET/database backend remains authoritative for application state, authorization and mutations.
```

## 3. Anything else we should know?

**330/500 characters**

```text
Draft is small in public star/fork metrics, so I am not claiming broad adoption. The application is based on a real recurring workflow plus substantial maintenance responsibility. Confirmed production failures are routinely turned into permanent regression coverage, and AI remains optional rather than a source of business truth.
```

## Submission gate

### Strong enough already

- [x] Public repository
- [x] MIT license
- [x] Primary-maintainer evidence
- [x] Substantial merged PR history
- [x] Production incident triage
- [x] CI + regression/fuzz coverage
- [x] Contributor/security/maintainer documentation
- [x] Real-workflow screenshots
- [x] Application answers stay below the 500-character form limits

### High-value gaps before submitting

- [ ] Publish the first GitHub Release/tag `v0.1.0` from the reviewed main branch
- [ ] Set GitHub About description and topics
- [ ] Keep the demo unfeatured while Render services are suspended, or restore a reliably reachable deployment
- [ ] Prefer at least one genuine privacy-safe user report in #419 if available; never fabricate adoption

## Decision rule

Do **not** delay indefinitely chasing star count. Submit once the first release and repository metadata are in place. Genuine user feedback strengthens the usage signal, but fabricated or solicited-for-metrics activity would weaken credibility.

## Current public weaknesses to disclose rather than hide

- Public star/fork counts are small.
- The project does not yet demonstrate broad ecosystem adoption.
- Hosting can be unavailable when free services are suspended.
- `v0.1.0` is an actively evolving baseline rather than a mature stable release.

The application should therefore lead with **real recurring use + unusually strong maintenance responsibility**, not popularity.