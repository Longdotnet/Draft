# Contributing to Draft 🏐

Thanks for helping make Draft more useful and reliable for community volleyball groups.

You do **not** need to understand the whole system before contributing. Small, focused improvements are preferred.

## Good first contributions

- Reproduce a bug and turn it into a regression test.
- Improve setup docs, screenshots or error messages.
- Add coverage for a lifecycle edge case or retry path.
- Improve the Zalo mock flow so contributors can test without real credentials.
- Simplify a confusing organizer/mobile workflow without changing its invariants.
- Report a real scheduling, roster or drafting workflow that Draft does not handle well yet.

## Before a larger change

For changes that affect session state, authorization, drafting, reminders or Zalo behavior, please open an issue first. Describe:

1. the user scenario;
2. what currently happens;
3. what should happen instead;
4. which invariant must stay true.

This keeps design discussion centered on observable behavior rather than implementation preference.

## Project invariants

Contributions should preserve these rules:

- **Backend state is authoritative.** The database/application state owns authorization, counts and mutations.
- **AI is optional.** AI may classify or rewrite text, but it must not become the source of business truth.
- **Retries are safe.** Repeated deliveries, restarts and multiple instances should not duplicate authoritative work.
- **Failures become tests.** A confirmed bug should normally include a minimized regression case.
- **Secrets stay server-side.** Never expose Zalo credentials, cookies, IMEI values, API keys or encryption keys to the browser or repository.

## Local development

The easiest development path uses the mock Zalo bridge and SQLite. See the [Quick start](README.md#quick-start--safe-local-mode) section in the README.

Before opening a pull request, run the relevant checks:

```powershell
dotnet test server/VolleyDraft.Api.Tests/VolleyDraft.Api.Tests.csproj
dotnet build server/VolleyDraft.Api/VolleyDraft.Api.csproj
npm run build
npm --prefix server/ZaloBridge test
npm --prefix server/ZaloBridge run build
```

If your change only touches one area, focused tests are fine during development, but the final pull request should explain what was run.

## Pull requests

A useful pull request description answers four questions:

- **Problem:** what user or reliability problem does this solve?
- **Change:** what behavior changed?
- **Evidence:** what test or reproduction proves it?
- **Risk:** what nearby behavior could regress?

Keep unrelated cleanup out of the same pull request when possible. Smaller PRs are easier to review and safer to revert.

## Security and private data

Do not put real group credentials or private user data in issues, fixtures, screenshots or commits. Use sanitized examples or the mock bridge.

If a report contains a credential or private-data exposure, avoid posting the secret itself in a public issue. Describe the affected component and reproduction with redacted values.

## Questions

If you are unsure where to start, open an issue with the workflow you want to improve. A concrete real-world scenario is more useful than a broad feature request.

Thanks for contributing.