from __future__ import annotations

import asyncio
import base64
import io
import os
import threading
import time
from typing import Literal

from fastapi import FastAPI, Header, HTTPException
from PIL import Image, ImageFilter, ImageOps
from pydantic import BaseModel, Field

app = FastAPI(title="NPC11 VolleyVerse Art Worker", version="1.0.0")

MODEL_ID = os.getenv("NPC11_MODEL_ID", "Qwen/Qwen-Image-Edit-2511")
WORKER_KEY = os.getenv("NPC11_WORKER_KEY", "").strip()
OFFLOAD_MODE = os.getenv("NPC11_OFFLOAD", "model").strip().lower()
INFERENCE_STEPS = max(12, min(60, int(os.getenv("NPC11_STEPS", "40"))))
TRUE_CFG_SCALE = float(os.getenv("NPC11_TRUE_CFG_SCALE", "4.0"))
MAX_REFERENCE_EDGE = max(512, min(1536, int(os.getenv("NPC11_MAX_REFERENCE_EDGE", "1024"))))
MIN_REFERENCE_EDGE = max(256, min(768, int(os.getenv("NPC11_MIN_REFERENCE_EDGE", "512"))))

_pipeline = None
_pipeline_device = "unloaded"
_pipeline_lock = threading.Lock()
_gpu_lock = asyncio.Lock()


class ArtReference(BaseModel):
    role: str = "subject"
    mimeType: str
    imageBase64: str


class OutputSpec(BaseModel):
    width: int = Field(default=1024, ge=512, le=2048)
    height: int = Field(default=1365, ge=512, le=2048)
    format: Literal["png", "jpeg", "jpg"] = "png"


class ArtRequest(BaseModel):
    provider: str
    seed: int
    style: str
    prompt: str
    references: list[ArtReference]
    output: OutputSpec


class ArtResponse(BaseModel):
    success: bool
    imageBase64: str | None = None
    strategy: str | None = None
    error: str | None = None


def _require_key(value: str | None) -> None:
    if WORKER_KEY and value != WORKER_KEY:
        raise HTTPException(status_code=401, detail="Invalid NPC11 worker key")


def _provider_model(provider: str) -> str:
    normalized = provider.strip().lower()
    aliases = {
        "qwen": "Qwen/Qwen-Image-Edit-2511",
        "qwen-image-edit-2511": "Qwen/Qwen-Image-Edit-2511",
        "qwen-image-edit-2509": "Qwen/Qwen-Image-Edit-2509",
    }
    if normalized not in aliases:
        raise HTTPException(
            status_code=400,
            detail=(
                f"Unsupported provider '{provider}'. This worker supports qwen-image-edit-2511 "
                "and qwen-image-edit-2509."
            ),
        )
    requested_model = aliases[normalized]
    if requested_model != MODEL_ID:
        raise HTTPException(
            status_code=409,
            detail=f"Worker loaded for {MODEL_ID}; request asked for {requested_model}",
        )
    return requested_model


def _decode_reference(reference: ArtReference) -> Image.Image:
    try:
        raw = base64.b64decode(reference.imageBase64, validate=True)
        image = Image.open(io.BytesIO(raw))
        image = ImageOps.exif_transpose(image).convert("RGB")
    except Exception as exc:  # noqa: BLE001 - convert malformed image into a clean API error
        raise HTTPException(status_code=400, detail=f"Invalid reference image: {exc}") from exc

    original_short = min(image.size)
    short_edge = min(image.size)
    if short_edge < MIN_REFERENCE_EDGE:
        scale = MIN_REFERENCE_EDGE / short_edge
        image = image.resize(
            (max(1, round(image.width * scale)), max(1, round(image.height * scale))),
            Image.Resampling.LANCZOS,
        )

    long_edge = max(image.size)
    if long_edge > MAX_REFERENCE_EDGE:
        scale = MAX_REFERENCE_EDGE / long_edge
        image = image.resize(
            (max(1, round(image.width * scale)), max(1, round(image.height * scale))),
            Image.Resampling.LANCZOS,
        )

    # Zalo commonly supplies 120-160px avatars. A restrained pre-sharpen after Lanczos
    # improves the reference signal without hallucinating identity before the edit model.
    if original_short < 320:
        image = image.filter(ImageFilter.UnsharpMask(radius=1.2, percent=120, threshold=3))
    return image


def _load_pipeline():
    global _pipeline, _pipeline_device
    if _pipeline is not None:
        return _pipeline

    with _pipeline_lock:
        if _pipeline is not None:
            return _pipeline

        import torch
        from diffusers import QwenImageEditPlusPipeline

        started = time.perf_counter()
        cuda = torch.cuda.is_available()
        dtype = torch.bfloat16 if cuda else torch.float32
        pipe = QwenImageEditPlusPipeline.from_pretrained(MODEL_ID, torch_dtype=dtype)
        pipe.set_progress_bar_config(disable=True)

        if cuda:
            torch.backends.cuda.matmul.allow_tf32 = True
            if OFFLOAD_MODE == "sequential":
                pipe.enable_sequential_cpu_offload()
                _pipeline_device = "cuda-sequential-offload"
            elif OFFLOAD_MODE == "model":
                pipe.enable_model_cpu_offload()
                _pipeline_device = "cuda-model-offload"
            else:
                pipe.to("cuda")
                _pipeline_device = "cuda"
        else:
            pipe.to("cpu")
            _pipeline_device = "cpu"

        _pipeline = pipe
        print(
            "[NPC11 worker] model-ready",
            {
                "model": MODEL_ID,
                "device": _pipeline_device,
                "seconds": round(time.perf_counter() - started, 2),
            },
            flush=True,
        )
        return _pipeline


def _fit_output(image: Image.Image, width: int, height: int) -> Image.Image:
    image = image.convert("RGB")
    source_ratio = image.width / image.height
    target_ratio = width / height
    if source_ratio > target_ratio:
        crop_width = round(image.height * target_ratio)
        left = max(0, (image.width - crop_width) // 2)
        image = image.crop((left, 0, left + crop_width, image.height))
    else:
        crop_height = round(image.width / target_ratio)
        top = max(0, (image.height - crop_height) // 2)
        image = image.crop((0, top, image.width, top + crop_height))
    return image.resize((width, height), Image.Resampling.LANCZOS)


def _run_inference(request: ArtRequest) -> bytes:
    import torch

    pipe = _load_pipeline()
    references = [_decode_reference(item) for item in request.references[:3]]
    if not references:
        raise HTTPException(status_code=400, detail="At least one reference image is required")

    negative_prompt = (
        "text, letters, watermark, logo, badge, card frame, UI, low quality, blurry face, "
        "deformed hands, extra fingers, duplicate limbs, duplicate subject, malformed object"
    )
    generator = torch.Generator(device="cpu").manual_seed(request.seed & 0x7FFFFFFF)
    with torch.inference_mode():
        result = pipe(
            image=references,
            prompt=request.prompt,
            generator=generator,
            true_cfg_scale=TRUE_CFG_SCALE,
            negative_prompt=negative_prompt,
            num_inference_steps=INFERENCE_STEPS,
            guidance_scale=1.0,
            num_images_per_prompt=1,
        ).images[0]

    result = _fit_output(result, request.output.width, request.output.height)
    buffer = io.BytesIO()
    if request.output.format in ("jpeg", "jpg"):
        result.save(buffer, format="JPEG", quality=94, optimize=True)
    else:
        result.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


@app.get("/health")
def health() -> dict[str, object]:
    return {
        "status": "ok",
        "model": MODEL_ID,
        "modelLoaded": _pipeline is not None,
        "device": _pipeline_device,
        "steps": INFERENCE_STEPS,
        "offload": OFFLOAD_MODE,
    }


@app.post("/v1/volleyverse/art", response_model=ArtResponse)
async def generate_art(
    request: ArtRequest,
    x_npc11_key: str | None = Header(default=None),
) -> ArtResponse:
    _require_key(x_npc11_key)
    _provider_model(request.provider)
    if not request.references:
        raise HTTPException(status_code=400, detail="At least one reference is required")

    started = time.perf_counter()
    async with _gpu_lock:
        try:
            data = await asyncio.to_thread(_run_inference, request)
        except HTTPException:
            raise
        except Exception as exc:  # noqa: BLE001 - worker must return fallback-friendly errors
            print("[NPC11 worker] inference-failed", repr(exc), flush=True)
            return ArtResponse(success=False, error=str(exc), strategy="qwen-fallback-error")

    elapsed_ms = round((time.perf_counter() - started) * 1000)
    print(
        "[NPC11 worker] art-ready",
        {
            "provider": request.provider,
            "style": request.style,
            "bytes": len(data),
            "elapsedMs": elapsed_ms,
        },
        flush=True,
    )
    return ArtResponse(
        success=True,
        imageBase64=base64.b64encode(data).decode("ascii"),
        strategy=f"qwen-image-edit:{MODEL_ID.rsplit('/', 1)[-1]}",
    )
