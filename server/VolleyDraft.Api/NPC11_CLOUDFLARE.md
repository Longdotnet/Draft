# NPC11 Cloudflare Workers AI

NPC11 uses Cloudflare Workers AI as the default always-on image provider. The Draft API calls Cloudflare directly; no local GPU worker, tunnel, or always-on PC is required.

## Render environment variables

```text
Npc11__AiEnabled=true
Npc11__ArtProvider=cloudflare-flux2-klein-4b
Npc11__Cloudflare__AccountId=<cloudflare-account-id>
Npc11__Cloudflare__ApiToken=<workers-ai-api-token>
Npc11__Cloudflare__Model=@cf/black-forest-labs/flux-2-klein-4b
Npc11__Cloudflare__TimeoutSeconds=45
```

Create the API token from Cloudflare Workers AI. The token needs Workers AI Read and Workers AI Edit permissions. Never commit the token to the repository.

## Runtime behavior

1. Draft resolves the target Zalo member and downloads the avatar.
2. The reference is normalized to JPEG and resized to fit below Cloudflare's 512x512 reference-image limit.
3. Draft sends multipart form data directly to the Workers AI REST endpoint with the avatar as `input_image_0`.
4. FLUX.2 Klein 4B returns Base64 image artwork.
5. Draft validates/decode-checks the output, then SkiaSharp renders all Vietnamese copy/stats/UI on top.
6. AI artwork and fallback cards use separate cache keys.
7. Missing credentials, quota errors, HTTP failures, timeouts, invalid output, or model failures always fall back to the deterministic avatar card.

## Optional local quality mode

The existing `server/Npc11ArtWorker` remains supported. To use it instead, set `Npc11__ArtProvider` to a non-`cloudflare-*` provider and configure `Npc11__ArtWorkerBaseUrl`/`Npc11__ArtWorkerKey`. Cloudflare remains the recommended default for 24/7 operation.
