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

The help response is sent with line breaks. These commands do not require an AI key.

## Local AI config

Edit `server/VolleyDraft.Api/appsettings.Development.json` and restart the API:

```json
"Ai": {
  "Endpoint": "https://your-provider.example/v1/chat/completions",
  "ApiKey": "your-secret-key",
  "Model": "your-model-id"
}
```

The AI settings are used only for free-form questions. Do not put the key in frontend code or in a `VITE_*` variable.

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

## Render environment variables

API service `Draft`:

```text
Zalo__BridgeBaseUrl=https://draft-zalo-bridge.onrender.com
Zalo__BridgeInternalKey=<same-value-as-ZALO_BRIDGE_INTERNAL_KEY>
Zalo__CredentialEncryptionKey=<stable-secret>
Zalo__WebhookUrl=https://volley-draft.onrender.com/api/internal/zalo/events
Zalo__WebhookKey=<webhook-shared-secret>
Ai__Endpoint=<ai-provider-endpoint>
Ai__ApiKey=<ai-provider-key>
Ai__Model=<ai-model-id>
```

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
