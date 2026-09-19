# Changelog

This project follows a lightweight changelog while the product is still evolving quickly.

## Unreleased

### Documentation / open-source readiness

- Added MIT licensing and contributor/security guidance.
- Redesigned the repository landing page around the real user workflow.
- Added real product screenshots and preserved the detailed Vietnamese guide.

## 0.1.0 — current package version

The current `0.1.0` line represents the working MVP and the reliability work that has accumulated around real weekly volleyball sessions.

### Session and drafting

- Create and manage volleyball sessions and player rosters.
- Select balanced captains and run a one-phone blind-bag style live draft.
- Support rotating/shared slots and keep-together preferences.
- Preserve authoritative draft/session state in the backend.

### Zalo workflow

- Import attendance from Zalo polls and keep roster identity deduplicated.
- Support session-aware bot commands and reminder workflows.
- Keep provider credentials behind the server boundary.
- Provide deterministic business answers even when AI is unavailable.

### Reliability

- Added execution/claim fences for overlapping and multi-instance mutations.
- Added idempotency protections for repeated inbound deliveries.
- Promoted confirmed lifecycle failures into permanent regression coverage.
- Added deterministic stateful fuzz campaigns for slot ownership, stale EF state, Auto Session ownership, provider failure, retries and restarts.

### Philosophy

- Database/application state remains the source of truth.
- AI is optional for interpretation or wording and never owns business authorization or mutation.

> Note: `0.1.0` is the version declared in `package.json`. A GitHub Release/tag can be published separately when the maintainer chooses the release snapshot.