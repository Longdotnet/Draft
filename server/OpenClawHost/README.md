# Codex App Server capacity check (replaces failed OpenClaw experiment)

The directory name is retained because the existing Render service uses it as its
root. The Docker image now runs the pinned official Codex CLI, not OpenClaw.
The HTTP probe reports ready only after a real stdio initialize RPC succeeds.
The process uses a fresh isolated home and an explicit environment allowlist;
it never imports a user's existing Codex credentials or makes inference calls.
No agent tools, authentication endpoint or public chat endpoint is exposed.

The 30-second budget measures process initialization, not Render image pulling,
scheduling, wake-up latency or model response time. Linux health output includes
container memory readings where supported. A public status page is not a login UI.

Local protocol smoke test (use an official installed Codex executable):
`node codex-probe.mjs --smoke` (`CODEX_PROBE_BINARY` may point to the native binary).

The supported integration for using a ChatGPT plan in Codex App Server is
https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server .
For an app offered remotely, request the applicable hosted access first:
https://developers.openai.com/siwc/token-sharing-open-source . Personal self-hosted
VM usage has a separately documented credential-transfer procedure. Do not use
native Codex login to substitute for an app-specific authorized OAuth grant.
Keep the production Draft gateway disabled until an eligible grant, secure
persistent storage, and a real end-to-end test are verified. No API billing or
new paid service is configured by this experiment.
