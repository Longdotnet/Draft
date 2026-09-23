# Draft 🏐

**From a Zalo poll to balanced teams — without spreadsheet chaos.**

Draft is an open-source organizer for real weekly volleyball groups. It imports attendance from Zalo polls, keeps session state authoritative in the backend, automates reminders, and runs a one-phone captain draft at the court.

[**Product tour**](#see-the-real-workflow) · [Real-world feedback](https://github.com/Longdotnet/Draft/issues/419) · [Vietnamese guide](docs/USER_GUIDE_VI.md) · [Contributing](CONTRIBUTING.md) · [MIT License](LICENSE)

[![CI](https://github.com/Longdotnet/Draft/actions/workflows/ci.yml/badge.svg)](https://github.com/Longdotnet/Draft/actions/workflows/ci.yml) ![MIT License](https://img.shields.io/badge/license-MIT-2ea44f) ![.NET](https://img.shields.io/badge/backend-.NET-512BD4?logo=dotnet) ![React](https://img.shields.io/badge/frontend-React-61DAFB?logo=react&logoColor=111) ![PostgreSQL](https://img.shields.io/badge/database-PostgreSQL-4169E1?logo=postgresql&logoColor=white)

## See the real workflow

![Match Autopilot Center](docs/images/match-autopilot.webp)

> **Match Autopilot Center** — routine work stays in the bot/backend. The web UI surfaces the cases that actually need a human decision.

<table>
  <tr>
    <td width="62%" valign="top">
      <img src="docs/images/zalo-poll-import.webp" alt="Import attendance from a Zalo poll" />
      <br/><strong>1. Import a real Zalo poll</strong><br/>Turn group votes into a session roster instead of copying names by hand.
    </td>
    <td width="38%" valign="top">
      <img src="https://github.com/user-attachments/assets/b9c7dd82-c798-4842-abb4-7b1d1eb5d2b0" alt="One-phone live volleyball draft" />
      <br/><strong>2. Draft live on one phone</strong><br/>Captains pick blind bags at the court; no account or second device required.
    </td>
  </tr>
</table>

### 3. See the outcome

**Three balanced teams are the end product — not another admin dashboard.**

[🖼️ Final three-team result — screenshot placeholder (replace with a sanitized real result later)](docs/USER_GUIDE_VI.md#kết-quả-cuối)

After the last reveal, Draft produces the final team rosters so the organizer can copy or share them back to the group. The screenshot is intentionally deferred until a clean public example is available; the product flow is already documented in the Vietnamese guide.

## Why Draft exists

A weekly volleyball session sounds simple until the organizer is juggling a chat poll, attendance changes, player profiles, reminders, balanced captains, shared slots and last-minute exceptions.

| Before | With Draft |
| --- | --- |
| Copy voters from chat by hand | Import attendance from Zalo polls |
| Chase missing players manually | Schedule reminder automation |
| Balance teams from memory | Use role, level and gender-aware drafting |
| Rebuild everything after a change | Keep authoritative session state in the backend |
| Pass phones around with ad-hoc notes | Run the live draft in one focused mobile flow |

## Real-world footprint

Draft is built around a recurring community-volleyball workflow, not only synthetic demo data. The repository screenshots show:

- a linked Zalo poll view with **33** and **39** voters;
- an **18-effective-slot** weekly session in the Match Autopilot flow;
- production scheduler/incident handling that opens GitHub issues when verification fails.

These are examples of one real operating workflow, **not a claim of broad adoption**. Public, privacy-safe usage reports are collected in [#419](https://github.com/Longdotnet/Draft/issues/419).

## What makes the project interesting

- **Built around a real recurring workflow.** Draft grew from the day-to-day friction of organizing community volleyball sessions.
- **Backend truth, optional AI.** AI may classify a natural-language request or improve wording, but business state, authorization, counts and mutations come from deterministic application code and the database.
- **Failure-aware automation.** Reminder jobs, execution leases and state transitions are designed so retries and multiple instances do not become a second source of truth.
- **Regression-first maintenance.** Race conditions and lifecycle failures are turned into reproducible tests and fuzz/regression coverage instead of one-off patches.
- **Human fallback is explicit.** Automation handles what it can prove; ambiguous or unsafe cases are surfaced to the organizer.

## How it works

```text
Zalo group / poll
       │
       ▼
  ZaloBridge (Node.js)
       │
       ▼
Volley Draft API (.NET) ───────► PostgreSQL
       │                              │
       ├── reminders / automation     └── authoritative session state
       ├── member intelligence
       └── draft lifecycle
       │
       ├────────► React admin web
       └────────► one-phone live draft
```

The model is never the system of record. When AI is disabled, unavailable or rate-limited, deterministic data flows continue to work.

## Core features

| Area | What Draft does |
| --- | --- |
| **Attendance** | Imports voters from Zalo polls, deduplicates people and keeps roster changes synchronized |
| **Team drafting** | Balanced captains, blind-bag live draft, shared/rotating slots and keep-together groups |
| **Automation** | Natural-language reminders, scheduler wake-up flow and session readiness checks |
| **Zalo bot** | Roster queries, session selection, profile updates, draft operations and team-card output |
| **Reliability** | Execution leases, idempotency, race-condition regression tests and fuzz campaigns |
| **Privacy boundary** | Zalo credentials are encrypted server-side and are not returned to the browser |

## Quick start — safe local mode

You can explore the project without a real Zalo account by using the mock bridge.

**1. Zalo mock bridge**

```powershell
cd server/ZaloBridge
npm install
$env:ZALO_BRIDGE_MOCK="true"
$env:ZALO_BRIDGE_INTERNAL_KEY="development-zalo-bridge-key"
npm run dev
```

**2. API with SQLite**

```powershell
cd server/VolleyDraft.Api
$env:Database__Provider="Sqlite"
$env:ConnectionStrings__Default="Data Source=volley-draft.db"
dotnet run
```

**3. React app**

```powershell
npm install
npm run dev
```

## Maintainer principles

These rules guide changes to the project:

1. **The backend owns truth, authorization and mutation.**
2. **AI is an optional interpretation/presentation layer, never business authority.**
3. **A confirmed production failure should become a regression test.**
4. **Retries must be idempotent and safe across process restarts or multiple instances.**
5. **Credentials and provider-specific secrets stay behind the server boundary.**

## Contributing

Bug reports, reproduction cases, documentation improvements, reliability tests and focused features are welcome.

Start with [CONTRIBUTING.md](CONTRIBUTING.md). For a larger behavior change, open an issue first so the invariant and expected user flow are clear before code is written.

## Documentation

- 🇻🇳 [Detailed Vietnamese user guide](docs/USER_GUIDE_VI.md)
- 🤖 [Zalo Member Intelligence](docs/ZALO_MEMBER_INTELLIGENCE.md)
- 🚀 [Render deployment](docs/RENDER_DEPLOY.md)
- 🧭 [Maintenance model](MAINTAINERS.md)
- 🛡️ [Security policy](SECURITY.md)
- 🗒️ [Changelog](CHANGELOG.md)
- 📄 [MIT License](LICENSE)

## Project status

Draft is actively maintained and still evolving. The current package version is **0.1.0**. The focus is practical reliability for real group sessions rather than turning every workflow into AI.

Public usage feedback is collected in [issue #419](https://github.com/Longdotnet/Draft/issues/419). Reports should describe real use only and must not include private group data or credentials.

---

**Using Draft for a similar group?** Try it, break it, and open an issue with the workflow that failed. If the project is useful to you, a ⭐ helps other organizers and contributors find it.