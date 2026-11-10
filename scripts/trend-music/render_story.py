#!/usr/bin/env python3
import base64
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time
import urllib.parse
import urllib.request

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")

DURATION_SECONDS = 20
WIDTH = 720
HEIGHT = 1280
MAX_DOWNLOAD_BYTES = 64 * 1024 * 1024
MAX_DOWNLOAD_SECONDS = 75
FFMPEG = ""

class HttpsOnlyRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        resolved = urllib.parse.urljoin(req.full_url, newurl)
        if urllib.parse.urlparse(resolved).scheme.lower() != "https":
            raise RuntimeError("redirect to non-HTTPS media URL is not allowed")
        return super().redirect_request(req, fp, code, msg, headers, resolved)

HTTPS_OPENER = urllib.request.build_opener(HttpsOnlyRedirectHandler())

def env(name: str, required: bool = True) -> str:
    value = os.environ.get(name, "").strip()
    if required and not value:
        raise RuntimeError(f"missing required environment variable: {name}")
    return value

def truthy_env(name: str) -> bool:
    return os.environ.get(name, "").strip().lower() in {"1", "true", "yes", "on"}

def download(url: str, target: pathlib.Path, referer: str | None = None) -> None:
    if urllib.parse.urlparse(url).scheme.lower() != "https":
        raise RuntimeError("media URL must use HTTPS")
    headers = {"User-Agent": "Mozilla/5.0 (compatible; VolleyDraft-TrendMusic/1.0)"}
    if referer:
        headers["Referer"] = referer
    request = urllib.request.Request(url, headers=headers)
    started = time.monotonic()
    with HTTPS_OPENER.open(request, timeout=30) as response:
        if urllib.parse.urlparse(response.geturl()).scheme.lower() != "https":
            raise RuntimeError("media download resolved to a non-HTTPS URL")
        total = 0
        with target.open("wb") as output:
            while True:
                if time.monotonic() - started > MAX_DOWNLOAD_SECONDS:
                    raise RuntimeError("media download exceeded total time limit")
                chunk = response.read(1024 * 256)
                if not chunk:
                    break
                total += len(chunk)
                if total > MAX_DOWNLOAD_BYTES:
                    raise RuntimeError("media download exceeds 64 MiB")
                output.write(chunk)
    if target.stat().st_size == 0:
        raise RuntimeError("media download is empty")


def local_candidate_file(candidate: dict, field: str) -> pathlib.Path | None:
    raw = str(candidate.get(field) or "").strip()
    root_raw = os.environ.get("TREND_MUSIC_LOCAL_MEDIA_ROOT", "").strip()
    if not raw or not root_raw:
        return None
    root = pathlib.Path(root_raw).resolve()
    path = pathlib.Path(raw).resolve()
    try:
        path.relative_to(root)
    except ValueError as exc:
        raise RuntimeError(f"{field} is outside the allowed local media root") from exc
    if not path.is_file():
        raise RuntimeError(f"{field} does not exist")
    return path

def run_ffmpeg(args: list[str]) -> None:
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *args], check=True, timeout=180)

def safe_text(path: pathlib.Path, value: str, limit: int = 140) -> None:
    clean = "".join(ch for ch in value if ord(ch) <= 0xFFFF and ch not in {"\ufe0f", "\u200d"})
    path.write_text(" ".join(clean.split())[:limit], encoding="utf-8")

def safe_greeting(path: pathlib.Path, value: str) -> None:
    clean = "".join(ch for ch in value if ord(ch) <= 0xFFFF and ch not in {"\ufe0f", "\u200d"})
    clean = " ".join(clean.split())[:220]
    lines = textwrap.wrap(clean, width=34, break_long_words=False, break_on_hyphens=False)[:3]
    path.write_text("\n".join(lines), encoding="utf-8")

def safe_wrapped_text(path: pathlib.Path, value: str, width: int, max_lines: int, limit: int) -> None:
    clean = "".join(ch for ch in value if ord(ch) <= 0xFFFF and ch not in {"\ufe0f", "\u200d"})
    clean = " ".join(clean.split())[:limit]
    lines = textwrap.wrap(clean, width=width, break_long_words=True, break_on_hyphens=False)[:max_lines]
    path.write_text("\n".join(lines), encoding="utf-8")

def filter_path(path: pathlib.Path) -> str:
    value = path.resolve().as_posix().replace("\\", "/")
    value = value.replace(":", "\\:").replace("'", "\\'")
    return f"'{value}'"

def font_files() -> tuple[pathlib.Path, pathlib.Path]:
    windows = pathlib.Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
    if windows.exists():
        bold = windows / "arialbd.ttf"
        regular = windows / "arial.ttf"
        if bold.exists() and regular.exists():
            return bold, regular
    return (
        pathlib.Path("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"),
        pathlib.Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
    )

def draw_filters(
    title_file: pathlib.Path,
    artist_file: pathlib.Path,
    greeting_file: pathlib.Path,
    greeting_enabled: bool,
) -> str:
    bold, regular = font_files()
    bold_path = filter_path(bold)
    regular_path = filter_path(regular)
    title_path = filter_path(title_file)
    artist_path = filter_path(artist_file)
    greeting_path = filter_path(greeting_file)
    greeting = (
        f"drawtext=fontfile={bold_path}:textfile={greeting_path}:fontcolor=white:fontsize=30:"
        "line_spacing=7:x=42:y=60:fix_bounds=true,"
        if greeting_enabled else ""
    )
    return (
        greeting
        + f"drawtext=fontfile={bold_path}:textfile={title_path}:fontcolor=white:fontsize=44:line_spacing=6:x=42:y=910:fix_bounds=true,"
        + f"drawtext=fontfile={regular_path}:textfile={artist_path}:fontcolor=white@0.86:fontsize=27:x=42:y=1092:fix_bounds=true,"
        + "drawbox=x=42:y=1168:w=636:h=3:color=white@0.18:t=fill,"
        + "drawbox=x=42:y=1168:w='636*t/20':h=3:color=white@0.88:t=fill"
    )

def prepare_text_files(candidate: dict, spec: dict, work: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path]:
    title_file = work / "title.txt"
    artist_file = work / "artist.txt"
    greeting_file = work / "greeting.txt"
    safe_wrapped_text(title_file, str(spec.get("displayTitle") or candidate.get("title") or "Daily Mix"), 20, 2, 96)
    safe_wrapped_text(artist_file, str(spec.get("displayArtist") or candidate.get("artist") or "Zyro Volleyball Club"), 24, 1, 72)
    safe_greeting(greeting_file, str(spec.get("greetingText") or ""))
    return title_file, artist_file, greeting_file

def is_small_profile_artwork(url: str) -> bool:
    match = re.search(r"(?<!\d)(\d{2,3})x(\d{2,3})(?=\D|$)", url, re.IGNORECASE)
    if not match:
        return False
    return max(int(match.group(1)), int(match.group(2))) <= 180

def logo_file(work: pathlib.Path) -> pathlib.Path | None:
    encoded = (
        pathlib.Path(__file__).resolve().parents[2]
        / "server"
        / "VolleyDraft.Api"
        / "Assets"
        / "Branding"
        / "zyro-header-logo.b64"
    )
    if not encoded.exists():
        return None
    target = work / "zyro-logo.png"
    if not target.exists():
        target.write_bytes(base64.b64decode(encoded.read_text(encoding="utf-8").strip()))
    return target

def finish_with_logo(filters: str, base_label: str, logo_input: int | None) -> tuple[str, str]:
    separator = "" if filters.endswith(";") else ";"
    if logo_input is None:
        return filters + separator + f"[{base_label}]null[v]", "[v]"
    filters += (
        separator + f"[{logo_input}:v]scale=104:-1:force_original_aspect_ratio=decrease,format=rgba[logo];"
        f"[{base_label}][logo]overlay=W-w-28:H-h-22:format=auto[v]"
    )
    return filters, "[v]"

def render_video_candidate(candidate: dict, spec: dict, work: pathlib.Path, output: pathlib.Path) -> None:
    media = work / "source-video"
    download(candidate["mediaUrl"], media, candidate.get("sourceUrl"))
    title_file, artist_file, greeting_file = prepare_text_files(candidate, spec, work)
    greeting_enabled = bool(spec.get("greetingEnabled", False) and spec.get("greetingText"))
    logo = logo_file(work) if bool(spec.get("showLogo", True)) else None
    override_artwork_url = str(spec.get("artworkUrl") or "").strip()
    if override_artwork_url:
        artwork = work / "override-artwork"
        try:
            download(override_artwork_url, artwork)
            filters = (
                "[0:v]split=2[bg0][cover0];"
                "[bg0]scale=720:1280:force_original_aspect_ratio=increase,crop=720:1280,setsar=1,boxblur=28:2,eq=brightness=-0.28:saturation=0.72[bg];"
                "[cover0]scale=520:520:force_original_aspect_ratio=increase:flags=lanczos,crop=520:520,setsar=1[cover];"
                "[bg][cover]overlay=(W-w)/2:320,setsar=1[visual];"
                "[visual]drawbox=x=100:y=320:w=520:h=520:color=white@0.18:t=2[framed];"
                f"[framed]{draw_filters(title_file, artist_file, greeting_file, greeting_enabled)}[base]"
            )
            logo_input = 2 if logo is not None else None
            filters, video_map = finish_with_logo(filters, "base", logo_input)
            args = [
                "-loop", "1", "-i", str(artwork),
                "-stream_loop", "-1", "-i", str(media),
            ]
            if logo is not None:
                args.extend(["-loop", "1", "-i", str(logo)])
            args.extend([
                "-filter_complex", filters, "-map", video_map, "-map", "1:a:0?",
                "-t", str(DURATION_SECONDS), "-r", "30",
                "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
                "-b:v", "800k", "-maxrate", "1000k", "-bufsize", "1600k",
                "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
            ])
            run_ffmpeg(args)
            return
        except Exception as exc:
            print(f"override artwork unavailable; keeping source video: {type(exc).__name__}: {exc}")
    filters = (
        "[0:v]split=2[bg0][fg0];"
        "[bg0]scale=720:1280:force_original_aspect_ratio=increase,crop=720:1280,setsar=1,boxblur=22:2,eq=brightness=-0.22:saturation=0.78[bg];"
        "[fg0]scale=620:760:force_original_aspect_ratio=decrease,setsar=1[fg];"
        "[bg][fg]overlay=(W-w)/2:250,setsar=1[visual];"
        f"[visual]{draw_filters(title_file, artist_file, greeting_file, greeting_enabled)}[base]"
    )
    logo_input = 1 if logo is not None else None
    filters, video_map = finish_with_logo(filters, "base", logo_input)
    args = [
        "-stream_loop", "-1", "-i", str(media),
    ]
    if logo is not None:
        args.extend(["-loop", "1", "-i", str(logo)])
    args.extend([
        "-filter_complex", filters, "-map", video_map, "-map", "0:a:0?",
        "-t", str(DURATION_SECONDS), "-r", "30",
        "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
        "-b:v", "900k", "-maxrate", "1100k", "-bufsize", "1800k",
        "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
    ])
    run_ffmpeg(args)

def render_audio_candidate(candidate: dict, spec: dict, work: pathlib.Path, output: pathlib.Path) -> None:
    audio, artwork = work / "source-audio", work / "artwork"
    local_audio = local_candidate_file(candidate, "mediaPath")
    if local_audio is not None:
        shutil.copy2(local_audio, audio)
    else:
        download(candidate["mediaUrl"], audio, candidate.get("sourceUrl"))
    artwork_url = str(spec.get("artworkUrl") or candidate.get("artworkUrl") or "").strip()
    local_artwork = local_candidate_file(candidate, "artworkPath")
    title_file, artist_file, greeting_file = prepare_text_files(candidate, spec, work)
    greeting_enabled = bool(spec.get("greetingEnabled", False) and spec.get("greetingText"))
    logo = logo_file(work) if bool(spec.get("showLogo", True)) else None
    if local_artwork is not None:
        shutil.copy2(local_artwork, artwork)
    elif artwork_url:
        try:
            download(artwork_url, artwork, candidate.get("sourceUrl"))
        except Exception as exc:
            print(f"artwork unavailable; keeping audio with waveform: {type(exc).__name__}: {exc}")
            artwork_url = ""
    if local_artwork is None and not artwork_url:
        filters = (
            "[1:a]asplit=2[aout0][viz];"
            "[aout0]afade=t=in:st=0:d=0.7,afade=t=out:st=18:d=2[aout];"
            "[viz]showwaves=s=640x360:mode=cline:rate=30:colors=0xE5E7EB,format=rgba[wave];"
            "[0:v]noise=alls=2:allf=t,eq=brightness=-0.08:saturation=0.65[bg];"
            "[bg][wave]overlay=40:360[visual];"
            f"[visual]{draw_filters(title_file, artist_file, greeting_file, greeting_enabled)}[base]"
        )
        logo_input = 2 if logo is not None else None
        filters, video_map = finish_with_logo(filters, "base", logo_input)
        args = [
            "-f", "lavfi", "-i", f"color=c=0x0B1020:s={WIDTH}x{HEIGHT}:r=30:d={DURATION_SECONDS}",
            "-stream_loop", "-1", "-i", str(audio),
        ]
        if logo is not None:
            args.extend(["-loop", "1", "-i", str(logo)])
        args.extend([
            "-filter_complex", filters, "-map", video_map, "-map", "[aout]",
            "-t", str(DURATION_SECONDS), "-r", "30",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-b:v", "720k", "-maxrate", "900k", "-bufsize", "1440k",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
        ])
        run_ffmpeg(args)
        return
    try:
        if local_artwork is None and is_small_profile_artwork(artwork_url):
            filters = (
                "[0:v]split=2[bg0][portrait0];"
                "[bg0]scale=720:1280:force_original_aspect_ratio=increase,crop=720:1280,setsar=1,boxblur=34:3,eq=brightness=-0.34:saturation=0.62[bg];"
                "[portrait0]scale=300:300:force_original_aspect_ratio=decrease:flags=lanczos,setsar=1[portrait];"
                "[bg]drawbox=x=170:y=340:w=380:h=380:color=black@0.28:t=fill[panel];"
                "[panel][portrait]overlay=(W-w)/2:340+(380-h)/2,setsar=1[visual];"
                "[visual]drawbox=x=170:y=340:w=380:h=380:color=white@0.14:t=2[framed];"
                f"[framed]{draw_filters(title_file, artist_file, greeting_file, greeting_enabled)}[base]"
            )
        else:
            filters = (
                "[0:v]split=2[bg0][cover0];"
                "[bg0]scale=720:1280:force_original_aspect_ratio=increase,crop=720:1280,setsar=1,boxblur=28:2,eq=brightness=-0.28:saturation=0.72[bg];"
                "[cover0]scale=520:520:force_original_aspect_ratio=increase:flags=lanczos,crop=520:520,setsar=1[cover];"
                "[bg][cover]overlay=(W-w)/2:320,setsar=1[visual];"
                "[visual]drawbox=x=100:y=320:w=520:h=520:color=white@0.18:t=2[framed];"
                f"[framed]{draw_filters(title_file, artist_file, greeting_file, greeting_enabled)}[base]"
            )
        logo_input = 2 if logo is not None else None
        filters, video_map = finish_with_logo(filters, "base", logo_input)
        args = [
            "-loop", "1", "-i", str(artwork), "-stream_loop", "-1", "-i", str(audio),
        ]
        if logo is not None:
            args.extend(["-loop", "1", "-i", str(logo)])
        args.extend([
            "-filter_complex", filters, "-map", video_map, "-map", "1:a:0",
            "-t", str(DURATION_SECONDS), "-r", "30",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-b:v", "800k", "-maxrate", "1000k", "-bufsize", "1600k",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
        ])
        run_ffmpeg(args)
    except Exception as exc:
        print(f"artwork decode/render failed; keeping audio with waveform: {type(exc).__name__}: {exc}")
        fallback_candidate = dict(candidate)
        fallback_candidate["artworkUrl"] = None
        fallback_candidate["artworkPath"] = None
        fallback_spec = dict(spec)
        fallback_spec["artworkUrl"] = None
        render_audio_candidate(fallback_candidate, fallback_spec, work, output)

def render_fallback(spec: dict, work: pathlib.Path, output: pathlib.Path) -> dict:
    selected = {"id": "zyro-daily-fallback", "title": "Zyro Daily Mix", "artist": "Zyro Volleyball Club", "rank": 0}
    title_file, artist_file, greeting_file = prepare_text_files(selected, spec, work)
    greeting_enabled = bool(spec.get("greetingEnabled", False) and spec.get("greetingText"))
    logo = logo_file(work) if bool(spec.get("showLogo", True)) else None
    original_audio = (
        "aevalsrc=(0.62+0.38*sin(2*PI*2*t))*"
        "(0.065*sin(2*PI*110*t)+0.055*sin(2*PI*220*t)+"
        "0.040*sin(2*PI*277.18*t)+0.032*sin(2*PI*329.63*t)):"
        f"s=44100:d={DURATION_SECONDS}"
    )
    artwork_url = str(spec.get("artworkUrl") or "").strip()
    if artwork_url:
        artwork = work / "fallback-artwork"
        try:
            download(artwork_url, artwork)
            filters = (
                "[1:a]volume=0.70,afade=t=in:st=0:d=1,afade=t=out:st=18:d=2[aout];"
                "[0:v]split=2[bg0][cover0];"
                "[bg0]scale=720:1280:force_original_aspect_ratio=increase,crop=720:1280,setsar=1,boxblur=28:2,eq=brightness=-0.28:saturation=0.72[bg];"
                "[cover0]scale=520:520:force_original_aspect_ratio=increase:flags=lanczos,crop=520:520,setsar=1[cover];"
                "[bg][cover]overlay=(W-w)/2:320,setsar=1[visual];"
                "[visual]drawbox=x=100:y=320:w=520:h=520:color=white@0.18:t=2[framed];"
                f"[framed]{draw_filters(title_file, artist_file, greeting_file, greeting_enabled)}[base]"
            )
            logo_input = 2 if logo is not None else None
            filters, video_map = finish_with_logo(filters, "base", logo_input)
            args = [
                "-loop", "1", "-i", str(artwork),
                "-f", "lavfi", "-i", original_audio,
            ]
            if logo is not None:
                args.extend(["-loop", "1", "-i", str(logo)])
            args.extend([
                "-filter_complex", filters, "-map", video_map, "-map", "[aout]",
                "-t", str(DURATION_SECONDS), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-b:v", "700k", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", str(output),
            ])
            run_ffmpeg(args)
            return selected
        except Exception as exc:
            print(f"fallback artwork unavailable; using waveform: {type(exc).__name__}: {exc}")
    filters = (
        "[1:a]asplit=2[raw][viz];"
        "[raw]volume=0.70,afade=t=in:st=0:d=1,afade=t=out:st=18:d=2[aout];"
        "[viz]showwaves=s=640x360:mode=cline:rate=30:colors=0xE5E7EB,format=rgba[wave];"
        "[0:v]noise=alls=2:allf=t,eq=brightness=-0.08:saturation=0.65[bg];"
        "[bg][wave]overlay=40:360[visual];"
        f"[visual]{draw_filters(title_file, artist_file, greeting_file, greeting_enabled)}[base]"
    )
    logo_input = 2 if logo is not None else None
    filters, video_map = finish_with_logo(filters, "base", logo_input)
    args = [
        "-f", "lavfi", "-i", f"color=c=0x111827:s={WIDTH}x{HEIGHT}:r=30:d={DURATION_SECONDS}",
        "-f", "lavfi", "-i", original_audio,
    ]
    if logo is not None:
        args.extend(["-loop", "1", "-i", str(logo)])
    args.extend([
        "-filter_complex", filters, "-map", video_map, "-map", "[aout]",
        "-t", str(DURATION_SECONDS), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        "-b:v", "650k", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", str(output),
    ])
    run_ffmpeg(args)
    return selected

def make_thumbnail(video: pathlib.Path, thumbnail: pathlib.Path) -> None:
    run_ffmpeg([
        "-ss", "00:00:01", "-i", str(video), "-frames:v", "1",
        "-vf", f"scale={WIDTH}:{HEIGHT}:force_original_aspect_ratio=decrease,pad={WIDTH}:{HEIGHT}:(ow-iw)/2:(oh-ih)/2",
        "-q:v", "3", str(thumbnail),
    ])

def deliver(video: pathlib.Path, thumbnail: pathlib.Path, selected: dict, spec: dict) -> None:
    payload = {
        "accountId": env("ACCOUNT_ID"), "groupId": env("GROUP_ID"), "connectionId": env("CONNECTION_ID"),
        "localDate": env("LOCAL_DATE"), "idempotencyKey": env("IDEMPOTENCY_KEY"),
        "candidateId": str(selected.get("id") or "music"), "title": str(selected.get("title") or "Daily Mix"),
        "artist": str(selected.get("artist") or ""), "rank": int(selected.get("rank") or 0),
        "sourceUrl": selected.get("sourceUrl"), "licenseUrl": selected.get("licenseUrl"),
        "licenseLabel": selected.get("licenseLabel"),
        "greetingText": str(spec.get("greetingText") or "") if isinstance(spec, dict) else "",
        "claimToken": env("CLAIM_TOKEN", required=False) or None,
        "videoBase64": base64.b64encode(video.read_bytes()).decode("ascii"),
        "thumbnailBase64": base64.b64encode(thumbnail.read_bytes()).decode("ascii"),
        "durationMs": DURATION_SECONDS * 1000, "width": WIDTH, "height": HEIGHT,
    }
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    last_error = None
    for attempt in range(1, 5):
        request = urllib.request.Request(
            env("CALLBACK_URL"), data=body, method="POST",
            headers={"Content-Type": "application/json", "x-trend-music-key": env("CALLBACK_KEY"),
                     "User-Agent": "VolleyDraft-TrendMusic-Renderer/1.0"})
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                if 200 <= response.status < 300:
                    print(f"callback accepted with HTTP {response.status}")
                    return
                last_error = RuntimeError(f"callback HTTP {response.status}")
        except Exception as exc:
            last_error = exc
        if attempt < 4:
            time.sleep(attempt * 10)
    raise RuntimeError(f"callback failed after retries: {last_error}")

def main() -> None:
    global FFMPEG
    FFMPEG = env("TREND_MUSIC_FFMPEG", required=False) or (shutil.which("ffmpeg") or "")
    if not FFMPEG or not pathlib.Path(FFMPEG).exists() and shutil.which(FFMPEG) is None:
        raise RuntimeError("ffmpeg is required")
    render_request = json.loads(env("CANDIDATES_JSON"))
    if isinstance(render_request, list):
        candidates = render_request
        spec = {
            "period": "manual",
            "greetingEnabled": False,
            "greetingText": "",
            "displayTitle": None,
            "displayArtist": None,
            "artworkUrl": None,
            "showLogo": True,
        }
    elif isinstance(render_request, dict) and isinstance(render_request.get("candidates"), list):
        candidates = render_request["candidates"]
        spec = render_request.get("spec") if isinstance(render_request.get("spec"), dict) else {}
    else:
        raise RuntimeError("CANDIDATES_JSON must be an array or render envelope")
    with tempfile.TemporaryDirectory(prefix="trend-music-") as temp:
        root, selected = pathlib.Path(temp), None
        output = root / "story.mp4"
        for index, candidate in enumerate(candidates[:8], start=1):
            if not isinstance(candidate, dict):
                continue
            try:
                work = root / f"candidate-{index}"
                work.mkdir(parents=True, exist_ok=True)
                media_type = str(candidate.get("mediaType") or "").lower()
                if media_type == "video":
                    render_video_candidate(candidate, spec, work, output)
                elif media_type == "audio":
                    render_audio_candidate(candidate, spec, work, output)
                else:
                    raise RuntimeError(f"unsupported mediaType: {media_type}")
                if output.stat().st_size > 8 * 1024 * 1024:
                    raise RuntimeError("rendered MP4 exceeds bridge 8 MiB limit")
                selected = candidate
                print(f"rendered candidate #{index}: {candidate.get('title')}")
                break
            except Exception as exc:
                print(f"candidate #{index} failed: {type(exc).__name__}: {exc}")
                if output.exists():
                    output.unlink()
        if selected is None:
            work = root / "fallback"
            work.mkdir(parents=True, exist_ok=True)
            selected = render_fallback(spec, work, output)
            print("all candidates failed; rendered deterministic Zyro fallback")
        thumbnail = root / "story.jpg"
        make_thumbnail(output, thumbnail)
        output_dir = env("TREND_MUSIC_OUTPUT_DIR", required=False)
        if output_dir:
            destination = pathlib.Path(output_dir)
            destination.mkdir(parents=True, exist_ok=True)
            shutil.copy2(output, destination / "story.mp4")
            shutil.copy2(thumbnail, destination / "story.jpg")
            (destination / "selected.json").write_text(
                json.dumps(selected, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
        if truthy_env("TREND_MUSIC_SKIP_CALLBACK"):
            print("callback skipped by TREND_MUSIC_SKIP_CALLBACK")
            return
        deliver(output, thumbnail, selected, spec)

if __name__ == "__main__":
    main()
