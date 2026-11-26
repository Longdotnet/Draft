# Render Free owner UI experiment

This is an isolated text-only OpenClaw experiment. It is not the production
Draft API, and it does not receive Zalo messages yet. No provider credentials or
Draft database connection are passed to it. This host keeps OpenClaw state in a
temporary directory: do not rely on an OAuth login, device pairing, or selected
model surviving a restart. A durable, supported state store and a real OAuth
smoke test are required before enabling the Draft gateway.

## Owner access

1. In Render, open the existing `draft-openclaw-probe` service and add the secret
   environment variable `OPENCLAW_OWNER_PASSWORD`. Use a unique randomly generated
   password of at least 32 characters. Do not use a ChatGPT password or a Draft
   integration key. Never place the value in source, chat or logs.
2. Deploy this experiment branch. Without the variable the host remains a
   health-only probe. `/healthz`, `/startupz` and `/readyz` pass through the
   actual gateway health status; the root page is not a readiness check.
3. Open `https://draft-openclaw-probe.onrender.com/`, enter the owner password,
   and select **Mở Control UI**. The host invokes only the official local
   `openclaw dashboard --json` command and redirects to its single-use owner
   pairing link. It never returns the legacy shared gateway token.
4. In the native Control UI, **Settings > Models > Connect provider > OpenAI**
   is the documented provider connection flow. For a remote host, prefer the
   supported device-code option if available, and authorize only your own account.
   Do not sign in until the deployment is healthy and you understand the state
   persistence limitation. Model availability must be checked after authorization.

The gateway is loopback-only, explicit device pairing remains enabled, native
tools are denied, the OpenAI plugin is the only allowed provider plugin, and the
external Chat Completions API remains disabled. The owner portal is a small
access gate, not an OAuth implementation. It uses a secure HttpOnly session
cookie and the official owner handoff; no arbitrary shell command can be run.
The native gateway's admin interface remains privileged. Do not share the owner
password or device pairing links. Do not expose this experiment to Zalo users.

`node --test server/OpenClawHost/owner-ui.test.mjs` verifies the HTTP access
gate and mocked owner handoff. Live native UI, WebSocket pairing, memory
headroom, OAuth, and restart recovery require runtime testing. The previous
OpenClaw 2026.11.10 deployments were slow and an optimized attempt failed; a
30-second cold-start target is not established. No paid plan is requested.

Upstream references: https://docs.openclaw.ai/cli/dashboard and
https://docs.openclaw.ai/providers/openai/authentication .
