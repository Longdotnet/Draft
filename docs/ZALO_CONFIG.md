# Zalo bot configuration

## Number commands

The bot replies to these commands after the user selects the bot in Zalo's mention popup:

```text
@bot help
@bot 1  -> match time and location
@bot 2  -> check whether the sender is in the roster
@bot 3  -> location and parking instructions, plus the configured image
@bot 4  -> missing slots
@bot 5  -> upcoming matches
```

The help response is sent with line breaks. These commands still work without an AI key; when AI style is enabled, the API may use AI only to rewrite the final wording and falls back to the deterministic answer on failure.

## Local AI config

### Groq-only mode (opt-in)

Create an API key in a Groq **Free** organization at https://console.groq.com/keys.
Do not upgrade billing. Set these server-side environment variables (never commit a key):

```text
Ai__Groq__Enabled=true
Ai__Groq__ApiKey=<Groq Free API key>
```

This mode replaces the entire AI provider pool and disables the experimental OpenClaw
and ChatGPT-plan wrappers. It uses only `https://api.groq.com/openai/v1/chat/completions`
with `openai/gpt-oss-120b`, low reasoning effort, bounded completion tokens and JSON
object mode for existing structured workloads. No retries or alternate paid models
are attempted. Missing credentials fail closed; quota/rate errors use existing cooldown
and feature-level deterministic handling. Only enable after checking the account is
still Free and authorizing the data sent to Groq. An API key alone does not prove billing
status. Disabling the flag restores the previous provider routing.

The morning/night greeting card copy generators use their existing deterministic text
fallback in Groq-only mode rather than calling the legacy AI endpoint. Image providers
are configured separately and are not switched by this text-only setting.

Live limits: https://console.groq.com/docs/rate-limits. Rate limits are organization-wide;
separate keys do not create separate free quotas. Do not use additional accounts to bypass limits.

The text/semantic AI path uses an ordered provider pool. Feature code still talks only to `IZaloAiGateway`; it does not choose a concrete provider or model.

The repository defaults currently try these OpenRouter slots in order:

```text
1. nvidia/nemotron-3.5-super-vl:free
2. google/gemma-4-26b-a4b-it:free
3. cohere/north-mini-code:free
4. nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free
5. nvidia/nemotron-3-ultra-550b-a55b:free
```

Workloads that require JSON output (`IntentClassification`, `StructuredExtraction` and `DomainNarration`) are pinned to the Super VL / Gemma pair because those OpenRouter models support `response_format`. General chat, social reply and safe rewrite can fail over across the wider five-model pool.

Each pool slot can have an independent API key. When a slot key is blank, the gateway inherits the legacy `Ai:ApiKey`. Exact duplicate endpoint/key/model slots are collapsed automatically, so a deployment with only one shared key does not waste calls retrying the exact same model credential twice. Once separate keys are configured, repeated model slots remain distinct failover credentials.

For a local override, edit `server/VolleyDraft.Api/appsettings.Development.json` and restart the API. The legacy single-provider format remains supported when no `Ai:Providers` entries are configured:

```json
"Ai": {
  "Endpoint": "https://your-provider.example/v1/chat/completions",
  "ApiKey": "your-secret-key",
  "Model": "your-model-id"
}
```

The AI settings are used for semantic classification/extraction and optional natural wording. Business facts and actions still come from .NET/database. Do not put keys in frontend code, a `VITE_*` variable, or committed source configuration.

`Npc11` AI art is a separate image-generation path. It is disabled by default (`Npc11:AiEnabled=false`) and is not part of the text-model failover pool.

## Public image on Render

The project contains `public/images/choguixe.jpg`. Vite copies that file into the static build.

Local URL:

```text
http://localhost:5173/images/choguixe.jpg
```

Render URL for the current frontend service:

```text
https://volley-draft.onrender.com/images/choguixe.jpg
```

Use that HTTPS URL in the admin `URL ảnh vị trí / sơ đồ gửi xe` field. A path such as `C:\Users\ADMIN\Downloads\choguixe.jpg` works only on the local computer and cannot be downloaded by Render or Zalo users.

## Provider traffic and realtime reconciliation

The connected Zalo listener is the primary realtime path for group messages and `update_board` poll events. The API intentionally keeps a short deterministic worker tick for time-sensitive reminders/social jobs without turning every tick into Zalo provider polling.

Default reconciliation cadences are:

```text
Zalo__WorkerLoopSeconds=45
Zalo__ListenerReconcileSeconds=45
AutoSession__SafetyReconcileSeconds=900
```

`Zalo__ListenerReconcileSeconds` is a lightweight bridge/listener recovery check. The bridge reuses an already-active listener with the same credential fingerprint, so the 45-second default does not log in to Zalo again or poll provider data when the listener is healthy. `AutoSession__SafetyReconcileSeconds` is the expensive missed-poll-event safety net. Poll board websocket events are still processed immediately and do not wait 15 minutes.

The values are deliberately bounded in code: the short worker loop and listener recovery check cannot be configured below 30 seconds, and full Auto Session safety reconciliation cannot be configured below 5 minutes. Realtime behavior still belongs to the listener/event path; the short listener check only re-establishes a missing bridge listener and does not poll Zalo data while the listener is healthy.

## Render environment variables

API service `Draft`:

```text
Zalo__BridgeBaseUrl=https://draft-zalo-bridge.onrender.com
Zalo__BridgeInternalKey=<same-value-as-ZALO_BRIDGE_INTERNAL_KEY>
Zalo__CredentialEncryptionKey=<stable-secret>
Zalo__WebhookUrl=https://volley-draft.onrender.com/api/internal/zalo/events
Zalo__WebhookKey=<webhook-shared-secret>
Zalo__WorkerLoopSeconds=45
Zalo__ListenerReconcileSeconds=45
AutoSession__SafetyReconcileSeconds=900

# Existing shared OpenRouter credential. Pool slots inherit this when their own key is blank.
Ai__ApiKey=<shared-openrouter-key>

# Optional independent credentials for all five failover slots.
Ai__Providers__0__ApiKey=<key-for-slot-1>
Ai__Providers__1__ApiKey=<key-for-slot-2>
Ai__Providers__2__ApiKey=<key-for-slot-3>
Ai__Providers__3__ApiKey=<key-for-slot-4>
Ai__Providers__4__ApiKey=<key-for-slot-5>

ZaloBot__AiStyleEnabled=true
```

The repository supplies the current OpenRouter endpoint and five model IDs through `appsettings.json`. Existing `Ai__Endpoint` and `Ai__Model` variables may remain during rollout for legacy compatibility, but a valid `Ai:Providers` pool takes precedence for `IZaloAiGateway` calls.

Bridge service `draft-zalo-bridge`:

```text
ZALO_BRIDGE_INTERNAL_KEY=<same-value-as-Zalo__BridgeInternalKey>
ZALO_BRIDGE_MOCK=false
```

Frontend service `volley-draft`:

```text
VITE_API_BASE_URL=https://volley-draft.onrender.com
```

After changing Render variables, redeploy both API and bridge. Never change `Zalo__CredentialEncryptionKey` after Zalo credentials have been saved, otherwise old credentials cannot be decrypted.
