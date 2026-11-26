# OpenClaw text pilot

Draft already has an OpenAI-compatible gateway and a trusted caller scope. This pilot
adds a scoped, optional OpenClaw profile for `GeneralChat` only. Do not replace the
global `Ai:Endpoint` or provider pool. The existing routing, context, rate limits and
Zalo transport remain authoritative. The earlier direct SIWC pilot remains available
when OpenClaw is disabled; the two primary routes are not chained.

## Prerequisites and cost boundary

- A dedicated remote Linux host with persistent storage, or a separate container on
  such a host. No Draft database, Zalo session, source checkout, Docker socket, ERP
  tools, other users' OpenClaw state, or unrelated credentials should be mounted.
- Use the maintained upstream OpenClaw release `2026.11.10` (review security updates
  before installation). Its npm package requires Node 24.16+ in the 24.x series or
  Node 26.1+. Use the official prebuilt Docker image if the host is container-based.
- Log in with the authorized individual's ChatGPT/Codex account via OpenClaw's own
  supported OAuth flow. This is a single-user test, not a shared subscription service.
  Do not provision an API-key profile, enable automatic credit purchases, copy browser
  cookies, reuse unrelated OAuth tokens or share a personal grant with other users.
- Render Free has ephemeral storage, sleep and no shell/SSH access. The upstream
  Render guide uses a paid Starter service with a disk. Do not provision that paid
  service without approval. A free web instance alone is not durable OAuth hosting.

## Setup on a dedicated host (native installation example)

Follow https://docs.openclaw.ai/install and https://docs.openclaw.ai/providers/openai/setup.
The example config at `examples/openclaw-text-pilot.json` is for a new, isolated
instance, not a patch for a personal OpenClaw installation. Keep the state directory
private. Use an approved host secret manager or service environment to supply a
random `OPENCLAW_GATEWAY_TOKEN` (at least 32 random bytes); never commit it.

1. Install the pinned official package, with its supported Node version:
   `npm install -g openclaw@2026.11.10`.
2. Set up a fresh `openclaw.json` using the example. In a fresh dedicated OS account,
   the default path is `~/.openclaw/openclaw.json`. The template intentionally
   contains no auth secrets. Validate with `openclaw config validate`.
3. The authorized account owner runs `openclaw models auth login --provider openai --method oauth`
   on that host and completes the official device-code flow. Confirm availability with
   `openclaw models list --provider openai`. Set `agents.defaults.model.primary` to
   an available subscription model if the template's model is unavailable. No API key
   or fallback model should be configured in this OpenClaw instance.
4. Recheck configuration after login, especially `tools.deny`, empty model fallbacks,
   uploads, terminal and heartbeat. Run `openclaw config validate` and
   `openclaw security audit --deep`. Start the gateway with `openclaw gateway` using
   the dedicated service account and persistent state. Configure service supervision.
5. Verify a real text request locally, using the gateway token as the Bearer credential,
   `POST /v1/chat/completions`, `model: "openclaw/default"`, and normal messages.
   Verify the selected account/model uses subscription auth. Before admitting even
   the pilot user, verify effective native tool restrictions with the pinned runtime;
   no shell, file, network connector, hosted plugin, scheduled job, or source access
   may be available to chat requests. If verification fails, keep Draft disabled.
6. Expose only the exact Chat Completions endpoint to Draft through an authenticated
   HTTPS reverse proxy, preferably private connectivity or an ingress allowlist. Do
   not expose the Gateway's WebSocket/admin/control UI or accept anonymous requests.
   A gateway token grants operator-level access, not a narrow API scope. Keep the
   token server-side, use a separate instance, and never let Zalo users choose model,
   session headers, gateway URL, or tools.

For Docker, follow the official upstream deployment with the versioned image
`ghcr.io/openclaw/openclaw:2026.11.10`; the container needs its own persistent state,
`lan` binding within the container and a loopback-only published port behind the
TLS proxy. Do not attach the Docker daemon socket. This guide does not create or
start a paid service, or claim that the host's effective restrictions are verified.

## Draft configuration (Render)

After the isolated OpenClaw instance has passed the real smoke test, provision:

| Setting | Value |
| --- | --- |
| `Ai__OpenClaw__Enabled` | `true` only after verification; default false |
| `Ai__OpenClaw__Endpoint` | Dedicated HTTPS URL ending in `/v1/chat/completions` |
| `Ai__OpenClaw__GatewayToken` | Protected OpenClaw gateway token, NOT an OpenAI key |
| `Ai__OpenClaw__ConnectionId` | Trusted Draft connection ID for the test group |
| `Ai__OpenClaw__GroupId` | Consented test group ID |
| `Ai__OpenClaw__SenderId` | Exact authorized Zalo sender UID |
| `Ai__OpenClaw__TimeoutSeconds` | Default 40 (5–60) |
| `Ai__OpenClaw__QuotaProbeSeconds` | Default 300 (60–3600), local probe delay, not quota reset |

Draft passes text and its existing context, with no session override or user field.
OpenClaw assigns a fresh session per request. Group context may contain other members'
messages; use a test group whose members have consented to processing. OpenClaw keeps
runtime/session data on disk; set an appropriate retention policy on the dedicated host.

The existing Draft provider pool handles known quota, rate limits and transient
network/upstream failures subject to the original per-turn budget. Permission failures,
explicit policy failures, unknown/opaque 500 responses and incomplete tool responses
do not trigger fallback. No new provider billing, key purchases or account rotation
are configured. Disable `Ai__OpenClaw__Enabled` to roll back to the existing behavior.

## Validation

`dotnet test server/VolleyDraft.Api.Tests/VolleyDraft.Api.Tests.csproj --filter FullyQualifiedName~OpenClawTextGatewayTests`
uses mocked HTTP responses. Live OAuth, config validation against the running pinned
release, effective native restrictions, restart persistence, actual quota error mapping
and end-to-end Zalo delivery still need a real isolated host. Do not claim these are
tested based on the adapter unit tests.

Upstream references:
- https://docs.openclaw.ai/gateway/openai-http-api
- https://docs.openclaw.ai/providers/openai/authentication
- https://docs.openclaw.ai/plugins/codex-harness-reference/restricted-turns
- https://docs.openclaw.ai/install/docker
- https://docs.openclaw.ai/install/render
- https://render.com/docs/free
