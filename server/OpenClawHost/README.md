# OpenClaw owner-only text pilot on Render

This isolated service reuses the existing Draft scoped OpenClaw gateway. It
does not replace the Zalo Bridge. The gateway runs on loopback with a random
internal token, denies agent tools, and disables terminal/uploads. No API key,
Zalo credential, or Draft database connection is included in the child process.

The pinned extended-stable official slim image is a new runtime experiment;
the earlier 2026.11.10 build OOMed even after reverting to its previously live
configuration. Free Render's 512 MiB may still be insufficient. Health endpoints
continue to check native gateway readiness, not the reverse proxy alone.

## Owner interface

Set `OPENCLAW_OWNER_PASSWORD` in the service's Render Environment, a unique
random secret of at least 32 characters (not your ChatGPT password). Open the
service root and sign into the owner portal, then choose Mở Control UI. The
portal uses the official native one-time dashboard pairing flow. In the native
UI, select Settings > Models > Connect provider > OpenAI. Use only a sign-in
method authorized for your own account and intended use. Do not configure paid
API keys for this pilot. Confirm a text reply in the native UI before enabling
Draft. Free hosting uses ephemeral state: credentials, pairing and model
selection can be lost on restart; this is not a durable OAuth deployment.

## Draft text ingress (disabled by default)

Only after the required permission for ChatGPT plan access has been granted,
set `OPENCLAW_PLAN_ACCESS_APPROVED=true` and a separate randomly generated
`OPENCLAW_DRAFT_TEXT_KEY` (32+ characters). Never mark approval merely because
the UI opens or the interest form was submitted. The external endpoint accepts
only bounded non-streaming text requests addressed to `openclaw/default`, uses
its own secret, and does not forward session, model, tool, or identity headers.
The native gateway token is never shared with Draft.

After successful native UI and ingress smoke tests, configure the existing Draft
backend's `Ai__OpenClaw__Enabled`, `Ai__OpenClaw__Endpoint` (the HTTPS service
URL plus `/v1/chat/completions`), `Ai__OpenClaw__GatewayToken` (the *ingress* key),
`Ai__OpenClaw__ConnectionId`, `Ai__OpenClaw__GroupId`, and `Ai__OpenClaw__SenderId`.
Use only the authorized owner's exact sender and group; no shared subscription
access or unsolicited disclosure of group history. Set timeout to 25 seconds
for the initial text test; this is not an end-to-end latency guarantee. Existing
Draft fallback policy is unchanged. Do not enable paid fallback credentials if
additional charges are not accepted. No production configuration is modified
by deploying this host.

`node --test server/OpenClawHost/*.test.mjs` exercises the HTTP gate and text
proxy with mock native gateway calls. Real runtime, WebSocket pairing, OAuth,
memory and Zalo replies must be verified separately.
