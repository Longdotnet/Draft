# Security Policy

Draft handles integrations that may involve private group data and provider credentials. Please do not publish real credentials, cookies, IMEI values, API keys, encryption keys, or private user data in issues or pull requests.

## Reporting a security issue

If you find a vulnerability, open an issue only if the report can be fully reproduced with redacted or mock data. Do not include secrets or private user information.

If the problem requires sharing sensitive material, first open a minimal public issue describing only the affected component and ask the maintainer for a private contact path.

## Scope

Security-sensitive areas include:

- Zalo credential storage and bridge boundaries;
- authorization for session mutations;
- webhook/internal keys;
- encryption-key handling;
- cross-user or cross-group data leakage;
- duplicate execution caused by retries or multi-instance races.

Please include the affected component, impact, minimal reproduction, and whether the issue is reproducible with mock data.