# NPC11 VolleyVerse local art worker

This worker turns the Zalo reference image into **character artwork only**. The Draft API still renders all Vietnamese text, stats, badges and card UI with SkiaSharp, so a generative model can never corrupt card text.

## Default model

`Qwen/Qwen-Image-Edit-2511` (Apache-2.0) is the default quality profile because it improves subject consistency/image drift over 2509 and supports reference-image editing. It is a large model, so disk/RAM/VRAM requirements are significant.

For machines that cannot run Qwen-Image-Edit-2511, keep `Npc11__AiEnabled=false` and Draft will render the improved deterministic avatar card. A smaller ComfyUI/FLUX.2 Klein adapter can be added later without changing the Draft API contract.

## Windows / NVIDIA quick start

1. Install Python 3.11 and a CUDA-enabled PyTorch build that matches your NVIDIA driver from the official PyTorch instructions.
2. From this folder:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
pip install -r requirements.txt
Copy-Item .env.example .env
```

3. Export the values from `.env` (or set them in PowerShell), then start:

```powershell
uvicorn app:app --host 0.0.0.0 --port 8189
```

4. Test:

```text
GET http://127.0.0.1:8189/health
```

The model is loaded lazily on the first generation request, not at process startup.

## Draft API environment

```text
Npc11__AiEnabled=true
Npc11__ArtProvider=qwen-image-edit-2511
Npc11__ArtWorkerBaseUrl=https://YOUR-TUNNEL-HOST
Npc11__ArtWorkerKey=the-same-secret-as-NPC11_WORKER_KEY
Npc11__ArtTimeoutSeconds=45
```

Use an HTTPS tunnel from Render to this worker if the GPU machine is on your home network. Do not expose the port directly without the worker key/firewall.

## Low-resolution Zalo avatars

Zalo frequently returns 120-160px profile images. Before Qwen sees the reference, the worker:

- fixes EXIF orientation;
- enlarges the short edge to at least 512px with Lanczos;
- caps the long edge at 1024px;
- applies a restrained unsharp mask only for very small sources.

This does **not** recover true lost identity detail, but it gives the edit model a cleaner reference tensor while avoiding aggressive fake sharpening.

## Safety/fallback behavior

- GPU inference is serialized to avoid VRAM spikes.
- Worker errors return `success=false` instead of breaking the bot.
- Draft caches successful AI cards separately from fallback cards.
- If the worker is off, slow, or out of VRAM, `@Npc 11` still produces a deterministic avatar card.
