#!/usr/bin/env python3
import base64
import hashlib
import json
import math
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time
import unicodedata
import urllib.parse
import urllib.request

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")

DEFAULT_DURATION_SECONDS = 20.0
DURATION_SECONDS = DEFAULT_DURATION_SECONDS
WIDTH = 1080
HEIGHT = 1920
MAX_DOWNLOAD_BYTES = 64 * 1024 * 1024
MAX_DOWNLOAD_SECONDS = 75
MAX_INLINE_VIDEO_BYTES = 8 * 1024 * 1024
VIDEO_TARGET_BYTES = int(7.25 * 1024 * 1024)
APPLE_MUSIC_HOST = "music.apple.com"
APPLE_AUDIO_HOST = "audio-ssl.itunes.apple.com"
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
    timeout = max(180, min(900, int(math.ceil(DURATION_SECONDS * 6))))
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *args], check=True, timeout=timeout)


def format_seconds(value: float) -> str:
    return f"{value:.3f}".rstrip("0").rstrip(".")


def fade_out_start(seconds: float) -> str:
    return format_seconds(max(0.0, DURATION_SECONDS - seconds))


def ffprobe_binary() -> str:
    configured = env("TREND_MUSIC_FFPROBE", required=False)
    if configured:
        return configured
    ffmpeg_path = pathlib.Path(FFMPEG)
    if ffmpeg_path.is_file():
        candidate = ffmpeg_path.with_name("ffprobe.exe" if ffmpeg_path.suffix.lower() == ".exe" else "ffprobe")
        if candidate.is_file():
            return str(candidate)
    return shutil.which("ffprobe") or ""


def probe_media_duration_seconds(path: pathlib.Path) -> float:
    ffprobe = ffprobe_binary()
    if not ffprobe:
        raise RuntimeError("ffprobe is required to preserve media duration")
    result = subprocess.run(
        [
            ffprobe,
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            str(path),
        ],
        check=True,
        timeout=30,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        duration = float(result.stdout.strip())
    except ValueError as exc:
        raise RuntimeError("ffprobe returned an invalid media duration") from exc
    if not math.isfinite(duration) or duration <= 0:
        raise RuntimeError("ffprobe returned a non-positive media duration")
    return duration


def requested_media_duration_seconds(media_duration: float) -> float:
    return media_duration


def audio_bitrate_kbps() -> int:
    if DURATION_SECONDS <= 90:
        return 128
    if DURATION_SECONDS <= 180:
        return 96
    return 80


def video_bitrate_kbps(default_kbps: int) -> int:
    total_budget_kbps = VIDEO_TARGET_BYTES * 8 / max(DURATION_SECONDS, 1.0) / 1000
    audio_kbps = audio_bitrate_kbps()
    # Leave room for MP4 mux/container overhead and rate-control spikes.
    calculated = int(total_budget_kbps - audio_kbps - 24)
    return max(48, min(default_kbps, calculated))


def bitrate_args(default_video_kbps: int, default_audio_kbps: int = 128) -> list[str]:
    video_kbps = video_bitrate_kbps(default_video_kbps)
    audio_kbps = audio_bitrate_kbps() if DURATION_SECONDS > DEFAULT_DURATION_SECONDS else default_audio_kbps
    maxrate_kbps = max(video_kbps, int(video_kbps * 1.08))
    bufsize_kbps = max(video_kbps * 2, 96)
    return [
        "-b:v", f"{video_kbps}k",
        "-maxrate", f"{maxrate_kbps}k",
        "-bufsize", f"{bufsize_kbps}k",
        "-c:a", "aac",
        "-b:a", f"{audio_kbps}k",
    ]


def configure_duration_for_audio(audio: pathlib.Path) -> None:
    global DURATION_SECONDS
    media_duration = probe_media_duration_seconds(audio)
    DURATION_SECONDS = requested_media_duration_seconds(media_duration)
    print(
        "audio duration "
        f"source={format_seconds(media_duration)}s render={format_seconds(DURATION_SECONDS)}s"
    )


def apple_music_preview_from_html(html: str, track_id: str) -> str | None:
    if not track_id:
        return None
    match = re.search(
        rf'"id":"track-lockup - [^"]* - {re.escape(track_id)}".{{0,16000}}?"previewUrl":"([^"]+)"',
        html,
        re.DOTALL,
    )
    if match is None:
        return None
    raw = match.group(1)
    try:
        url = json.loads(f'"{raw}"')
    except json.JSONDecodeError:
        url = raw.replace("\\/", "/")
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme.lower() != "https" or (parsed.hostname or "").lower() != APPLE_AUDIO_HOST:
        return None
    return url


def apple_music_extended_preview(candidate: dict) -> str | None:
    source_url = str(candidate.get("sourceUrl") or "").strip()
    parsed = urllib.parse.urlparse(source_url)
    if parsed.scheme.lower() != "https" or (parsed.hostname or "").lower() != APPLE_MUSIC_HOST:
        return None
    candidate_id = str(candidate.get("id") or "").strip()
    track_id = candidate_id.removeprefix("itunes-") if candidate_id.startswith("itunes-") else ""
    if not track_id.isdigit():
        track_id = (urllib.parse.parse_qs(parsed.query).get("i") or [""])[0]
    if not track_id.isdigit():
        return None

    request = urllib.request.Request(
        source_url,
        headers={"User-Agent": "Mozilla/5.0 (compatible; VolleyDraft-TrendMusic/1.0)"},
    )
    try:
        with HTTPS_OPENER.open(request, timeout=30) as response:
            if (urllib.parse.urlparse(response.geturl()).hostname or "").lower() != APPLE_MUSIC_HOST:
                return None
            html = response.read(2 * 1024 * 1024 + 1)
            if len(html) > 2 * 1024 * 1024:
                return None
        return apple_music_preview_from_html(html.decode("utf-8", errors="ignore"), track_id)
    except Exception as exc:
        print(f"Apple Music extended preview unavailable; using lookup preview: {type(exc).__name__}: {exc}")
        return None

def clean_story_text(value: str, limit: int) -> str:
    normalized = unicodedata.normalize("NFC", value)
    # DejaVu/Noto Sans cover Vietnamese well, but monochrome drawtext on hosted
    # runners does not have a dependable emoji fallback. Strip symbol/control
    # glyphs instead of letting unsupported emoji turn into visible tofu squares.
    def supported(ch: str) -> bool:
        code = ord(ch)
        if ch in {"\ufe0f", "\u200d"}:
            return False
        if 0x1F000 <= code <= 0x1FAFF or 0x2600 <= code <= 0x27BF:
            return False
        return unicodedata.category(ch)[0] not in {"C"}

    clean = "".join(ch for ch in normalized if supported(ch))
    return " ".join(clean.split())[:limit]


def safe_text(path: pathlib.Path, value: str, limit: int = 140) -> None:
    path.write_text(clean_story_text(value, limit), encoding="utf-8")

def safe_greeting(path: pathlib.Path, value: str) -> tuple[pathlib.Path, ...]:
    clean = clean_story_text(value, 280)
    lines = textwrap.wrap(clean, width=28, break_long_words=False, break_on_hyphens=False)[:3]
    line_files: list[pathlib.Path] = []
    for index, line in enumerate(lines, start=1):
        line_path = path.with_name(f"{path.stem}-{index}{path.suffix}")
        line_path.write_bytes(line.encode("utf-8"))
        line_files.append(line_path)
    return tuple(line_files)

def safe_wrapped_text(path: pathlib.Path, value: str, width: int, max_lines: int, limit: int) -> None:
    clean = clean_story_text(value, limit)
    lines = textwrap.wrap(clean, width=width, break_long_words=True, break_on_hyphens=False)[:max_lines]
    path.write_text("\n".join(lines), encoding="utf-8")

def filter_path(path: pathlib.Path) -> str:
    value = path.resolve().as_posix().replace("\\", "/")
    value = value.replace(":", "\\:").replace("'", "\\'")
    return f"'{value}'"

def font_files() -> tuple[pathlib.Path, pathlib.Path]:
    configured_bold = os.environ.get("TREND_MUSIC_FONT_BOLD", "").strip()
    configured_regular = os.environ.get("TREND_MUSIC_FONT_REGULAR", "").strip()
    if configured_bold and configured_regular:
        bold, regular = pathlib.Path(configured_bold), pathlib.Path(configured_regular)
        if bold.is_file() and regular.is_file():
            return bold, regular
    windows = pathlib.Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
    if windows.exists():
        bold = windows / "arialbd.ttf"
        regular = windows / "arial.ttf"
        if bold.exists() and regular.exists():
            return bold, regular
    for bold, regular in (
        (
            pathlib.Path("/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf"),
            pathlib.Path("/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf"),
        ),
        (
            pathlib.Path("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"),
            pathlib.Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
        ),
    ):
        if bold.is_file() and regular.is_file():
            return bold, regular
    raise RuntimeError("a Vietnamese-capable TrueType font is required")

def draw_filters(
    title_file: pathlib.Path,
    artist_file: pathlib.Path,
    greeting_files: tuple[pathlib.Path, ...],
    greeting_enabled: bool,
) -> str:
    bold, regular = font_files()
    bold_path = filter_path(bold)
    regular_path = filter_path(regular)
    title_path = filter_path(title_file)
    artist_path = filter_path(artist_file)
    greeting = ""
    if greeting_enabled:
        greeting = "".join(
            f"drawtext=fontfile={bold_path}:textfile={filter_path(line_file)}:"
            "fontcolor=0xF8FAFC:fontsize=58:x=92:"
            f"y={190 + (index * 82)}:fix_bounds=true:shadowcolor=black@0.72:shadowx=3:shadowy=4,"
            for index, line_file in enumerate(greeting_files)
        )
    return (
        greeting
        + "drawbox=x=92:y=1414:w=104:h=6:color=0xF8FAFC@0.92:t=fill,"
        + f"drawtext=fontfile={bold_path}:textfile={title_path}:fontcolor=0xFFFFFF:fontsize=68:line_spacing=12:"
          "x=92:y=1460:fix_bounds=true:shadowcolor=black@0.72:shadowx=3:shadowy=4,"
        + f"drawtext=fontfile={regular_path}:textfile={artist_path}:fontcolor=0xE5E7EB@0.92:fontsize=38:"
          "x=94:y=1620:fix_bounds=true:shadowcolor=black@0.62:shadowx=2:shadowy=3"
    )

def prepare_text_files(
    candidate: dict,
    spec: dict,
    work: pathlib.Path,
) -> tuple[pathlib.Path, pathlib.Path, tuple[pathlib.Path, ...]]:
    title_file = work / "title.txt"
    artist_file = work / "artist.txt"
    greeting_file = work / "greeting.txt"
    safe_wrapped_text(title_file, str(spec.get("displayTitle") or candidate.get("title") or "Daily Mix"), 22, 2, 96)
    safe_wrapped_text(artist_file, str(spec.get("displayArtist") or candidate.get("artist") or "Zyro Volleyball Club"), 30, 1, 72)
    greeting_files = safe_greeting(greeting_file, str(spec.get("greetingText") or ""))
    return title_file, artist_file, greeting_files

def is_small_profile_artwork(url: str) -> bool:
    match = re.search(r"(?<!\d)(\d{2,3})x(\d{2,3})(?=\D|$)", url, re.IGNORECASE)
    if not match:
        return False
    return max(int(match.group(1)), int(match.group(2))) <= 180


def image_dimensions(path: pathlib.Path) -> tuple[int, int] | None:
    try:
        with path.open("rb") as stream:
            signature = stream.read(24)
            if len(signature) >= 24 and signature[:8] == b"\x89PNG\r\n\x1a\n":
                return int.from_bytes(signature[16:20], "big"), int.from_bytes(signature[20:24], "big")
            if len(signature) < 2 or signature[:2] != b"\xff\xd8":
                return None
            stream.seek(2)
            sof_markers = {
                0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7,
                0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF,
            }
            while True:
                prefix = stream.read(1)
                if not prefix:
                    return None
                if prefix != b"\xff":
                    continue
                marker = stream.read(1)
                while marker == b"\xff":
                    marker = stream.read(1)
                if not marker:
                    return None
                marker_value = marker[0]
                if marker_value in {0xD8, 0xD9} or 0xD0 <= marker_value <= 0xD7:
                    continue
                length_raw = stream.read(2)
                if len(length_raw) != 2:
                    return None
                segment_length = int.from_bytes(length_raw, "big")
                if segment_length < 2:
                    return None
                if marker_value in sof_markers:
                    payload = stream.read(5)
                    if len(payload) != 5:
                        return None
                    height = int.from_bytes(payload[1:3], "big")
                    width = int.from_bytes(payload[3:5], "big")
                    return width, height
                stream.seek(segment_length - 2, 1)
    except OSError:
        return None


def is_low_resolution_artwork(path: pathlib.Path, url: str = "") -> bool:
    dimensions = image_dimensions(path)
    if dimensions is not None:
        return min(dimensions) < 480
    return is_small_profile_artwork(url)


def curated_story_background(spec: dict, candidate: dict) -> pathlib.Path | None:
    period = str(spec.get("period") or "").strip().lower()
    root = pathlib.Path(__file__).resolve().parents[2] / "server" / "VolleyDraft.Api" / "Assets" / "SocialCards"
    if period == "morning":
        pool = [root / f"social-card-{index:02d}-hd.png" for index in range(1, 5)]
    elif period == "birthday":
        pool = [root / f"social-card-{index:02d}-hd.png" for index in range(1, 5)]
    elif period == "night":
        # Keep bedtime stories on calm bedroom/night imagery. night-card-01 is
        # an in-match volleyball action shot and clashes with the sleep copy.
        pool = [root / "Night" / f"night-card-{index:02d}-hd.png" for index in range(2, 4)]
    else:
        return None
    available = [path for path in pool if path.is_file()]
    if not available:
        return None
    seed = f"{period}|{candidate.get('id') or ''}|{candidate.get('title') or ''}".encode("utf-8")
    index = int.from_bytes(hashlib.sha256(seed).digest()[:4], "big") % len(available)
    return available[index]

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
    video_fade_start = fade_out_start(1.0)
    if logo_input is None:
        return (
            filters + separator
            + f"[{base_label}]fade=t=in:st=0:d=0.7,fade=t=out:st={video_fade_start}:d=1[v]",
            "[v]",
        )
    filters += (
        separator + f"[{logo_input}:v]scale=118:-1:force_original_aspect_ratio=decrease,format=rgba,"
        "colorkey=0xFFFFFF:0.18:0.08,colorchannelmixer=aa=0.90[logo];"
        f"[{base_label}][logo]overlay=W-w-70:64:format=auto[branded];"
        f"[branded]fade=t=in:st=0:d=0.7,fade=t=out:st={video_fade_start}:d=1[v]"
    )
    return filters, "[v]"


def story_artwork_filters(input_label: str, spec: dict, *, small_profile: bool = False) -> str:
    period = str(spec.get("period") or "").strip().lower()
    if period == "morning":
        grade = "eq=brightness=-0.10:contrast=1.04:saturation=0.96"
        veil = "black@0.24"
    elif period == "night":
        grade = "eq=brightness=-0.28:contrast=1.06:saturation=0.78"
        veil = "black@0.42"
    else:
        grade = "eq=brightness=-0.20:contrast=1.05:saturation=0.86"
        veil = "black@0.34"

    cover_size = 360 if small_profile else 680
    cover_x = (WIDTH - cover_size) // 2
    cover_y = 720 if small_profile else 600
    return (
        f"[{input_label}]split=2[bg0][cover0];"
        "[bg0]scale=1140:2020:force_original_aspect_ratio=increase:flags=lanczos,"
        f"crop={WIDTH}:{HEIGHT}:"
        "x='(in_w-out_w)/2+8*sin(2*PI*t/14)':y='(in_h-out_h)/2+8*cos(2*PI*t/16)',"
        f"setsar=1,boxblur=44:4,{grade},vignette=PI/5[bggraded];"
        f"[bggraded]drawbox=x=0:y=0:w={WIDTH}:h={HEIGHT}:color={veil}:t=fill,"
        f"drawbox=x=0:y=0:w={WIDTH}:h=560:color=black@0.34:t=fill,"
        f"drawbox=x=0:y=1370:w={WIDTH}:h=550:color=black@0.44:t=fill[bg];"
        f"[cover0]scale={cover_size}:{cover_size}:force_original_aspect_ratio=increase:flags=lanczos,"
        f"crop={cover_size}:{cover_size},setsar=1,eq=contrast=1.03:saturation=1.04[cover];"
        f"[bg][cover]overlay={cover_x}:'{cover_y}+3*sin(2*PI*t/7)':eval=frame,setsar=1[visual];"
    )


def curated_background_filters(input_label: str, spec: dict) -> str:
    period = str(spec.get("period") or "").strip().lower()
    if period == "morning":
        grade = "eq=brightness=-0.08:contrast=1.04:saturation=0.93"
        veil = "black@0.10"
    else:
        grade = "eq=brightness=-0.14:contrast=1.06:saturation=0.82"
        veil = "black@0.16"
    return (
        f"[{input_label}]scale=1140:2020:force_original_aspect_ratio=increase:flags=lanczos,"
        f"crop={WIDTH}:{HEIGHT}:"
        "x='(in_w-out_w)/2+8*sin(2*PI*t/14)':y='(in_h-out_h)/2+8*cos(2*PI*t/16)',"
        f"setsar=1,{grade},vignette=PI/5,"
        f"drawbox=x=0:y=0:w={WIDTH}:h={HEIGHT}:color={veil}:t=fill,"
        f"drawbox=x=0:y=0:w={WIDTH}:h=560:color=black@0.32:t=fill,"
        f"drawbox=x=0:y=1370:w={WIDTH}:h=550:color=black@0.42:t=fill[storybg];"
    )


def render_curated_audio_story(
    candidate: dict,
    spec: dict,
    audio: pathlib.Path,
    background: pathlib.Path,
    title_file: pathlib.Path,
    artist_file: pathlib.Path,
    greeting_files: tuple[pathlib.Path, ...],
    greeting_enabled: bool,
    logo: pathlib.Path | None,
    output: pathlib.Path,
) -> None:
    filters = (
        "[1:a]asplit=2[aout0][viz];"
        f"[aout0]afade=t=in:st=0:d=0.7,afade=t=out:st={fade_out_start(2.0)}:d=2[aout];"
        "[viz]anullsink;"
        + curated_background_filters("0:v", spec)
        + f"[storybg]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
    )
    logo_input = 2 if logo is not None else None
    filters, video_map = finish_with_logo(filters, "base", logo_input)
    args = [
        "-loop", "1", "-i", str(background), "-stream_loop", "-1", "-i", str(audio),
    ]
    if logo is not None:
        args.extend(["-loop", "1", "-i", str(logo)])
    args.extend([
        "-filter_complex", filters, "-map", video_map, "-map", "[aout]",
        "-t", str(DURATION_SECONDS), "-r", "30",
        "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
    ])
    args.extend(bitrate_args(1700, 128))
    args.extend(["-movflags", "+faststart", str(output)])
    run_ffmpeg(args)

def render_video_candidate(candidate: dict, spec: dict, work: pathlib.Path, output: pathlib.Path) -> None:
    media = work / "source-video"
    download(candidate["mediaUrl"], media, candidate.get("sourceUrl"))
    title_file, artist_file, greeting_files = prepare_text_files(candidate, spec, work)
    greeting_enabled = bool(spec.get("greetingEnabled", False) and spec.get("greetingText"))
    logo = logo_file(work) if bool(spec.get("showLogo", True)) else None
    override_artwork_url = str(spec.get("artworkUrl") or "").strip()
    if override_artwork_url:
        artwork = work / "override-artwork"
        try:
            download(override_artwork_url, artwork)
            filters = (
                story_artwork_filters("0:v", spec)
                + f"[visual]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
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
                "-b:v", "1700k", "-maxrate", "2100k", "-bufsize", "3400k",
                "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
            ])
            run_ffmpeg(args)
            return
        except Exception as exc:
            print(f"override artwork unavailable; keeping source video: {type(exc).__name__}: {exc}")
    filters = (
        "[0:v]split=2[bg0][fg0];"
        f"[bg0]scale={WIDTH}:{HEIGHT}:force_original_aspect_ratio=increase,crop={WIDTH}:{HEIGHT},"
        "setsar=1,boxblur=36:3,eq=brightness=-0.24:contrast=1.05:saturation=0.82,vignette=PI/5[bg];"
        "[fg0]scale=900:1080:force_original_aspect_ratio=decrease:flags=lanczos,setsar=1[fg];"
        "[bg][fg]overlay=(W-w)/2:520:eval=frame,setsar=1[visual];"
        f"[visual]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
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
        "-b:v", "1800k", "-maxrate", "2200k", "-bufsize", "3600k",
        "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
    ])
    run_ffmpeg(args)

def render_audio_candidate(candidate: dict, spec: dict, work: pathlib.Path, output: pathlib.Path) -> None:
    audio, artwork = work / "source-audio", work / "artwork"
    local_audio = local_candidate_file(candidate, "mediaPath")
    if local_audio is not None:
        shutil.copy2(local_audio, audio)
    else:
        media_url = apple_music_extended_preview(candidate) or candidate["mediaUrl"]
        download(media_url, audio, candidate.get("sourceUrl"))
    configure_duration_for_audio(audio)
    artwork_url = str(spec.get("artworkUrl") or candidate.get("artworkUrl") or "").strip()
    local_artwork = local_candidate_file(candidate, "artworkPath")
    title_file, artist_file, greeting_files = prepare_text_files(candidate, spec, work)
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
        curated = curated_story_background(spec, candidate)
        if curated is not None:
            render_curated_audio_story(
                candidate, spec, audio, curated,
                title_file, artist_file, greeting_files, greeting_enabled, logo, output)
            return
        filters = (
            "[1:a]asplit=2[aout0][viz];"
            f"[aout0]afade=t=in:st=0:d=0.7,afade=t=out:st={fade_out_start(2.0)}:d=2[aout];"
            "[viz]showwaves=s=920x420:mode=cline:rate=30:colors=0xE5E7EB,format=rgba[wave];"
            "[0:v]noise=alls=2:allf=t,eq=brightness=-0.08:saturation=0.65[bg];"
            "[bg][wave]overlay=80:650[visual];"
            f"[visual]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
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
        ])
        args.extend(bitrate_args(1500, 128))
        args.extend(["-movflags", "+faststart", str(output)])
        run_ffmpeg(args)
        return
    try:
        curated = curated_story_background(spec, candidate)
        if curated is not None and is_low_resolution_artwork(artwork, artwork_url):
            dimensions = image_dimensions(artwork)
            print(
                "low-resolution artwork hidden from hero; using curated story background "
                f"dimensions={dimensions or 'unknown'} source={artwork_url or 'local'}"
            )
            render_curated_audio_story(
                candidate, spec, audio, curated,
                title_file, artist_file, greeting_files, greeting_enabled, logo, output)
            return
        if is_low_resolution_artwork(artwork, artwork_url):
            filters = (
                story_artwork_filters("0:v", spec, small_profile=True)
                + f"[visual]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
            )
        else:
            filters = (
                story_artwork_filters("0:v", spec)
                + f"[visual]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
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
        ])
        args.extend(bitrate_args(1700, 128))
        args.extend(["-movflags", "+faststart", str(output)])
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
    title_file, artist_file, greeting_files = prepare_text_files(selected, spec, work)
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
                f"[1:a]volume=0.70,afade=t=in:st=0:d=1,afade=t=out:st={fade_out_start(2.0)}:d=2[aout];"
                + story_artwork_filters("0:v", spec)
                + f"[visual]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
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
                "-t", str(DURATION_SECONDS), "-r", "30",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-b:v", "1600k", "-maxrate", "2000k", "-bufsize", "3200k",
                "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", str(output),
            ])
            run_ffmpeg(args)
            return selected
        except Exception as exc:
            print(f"fallback artwork unavailable; using waveform: {type(exc).__name__}: {exc}")
    curated = curated_story_background(spec, selected)
    if curated is not None:
        filters = (
            "[1:a]asplit=2[raw][viz];"
            f"[raw]volume=0.70,afade=t=in:st=0:d=1,afade=t=out:st={fade_out_start(2.0)}:d=2[aout];"
            "[viz]anullsink;"
            + curated_background_filters("0:v", spec)
            + f"[storybg]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
        )
        logo_input = 2 if logo is not None else None
        filters, video_map = finish_with_logo(filters, "base", logo_input)
        args = [
            "-loop", "1", "-i", str(curated),
            "-f", "lavfi", "-i", original_audio,
        ]
        if logo is not None:
            args.extend(["-loop", "1", "-i", str(logo)])
        args.extend([
            "-filter_complex", filters, "-map", video_map, "-map", "[aout]",
            "-t", str(DURATION_SECONDS), "-r", "30",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-b:v", "1600k", "-maxrate", "2000k", "-bufsize", "3200k",
            "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", str(output),
        ])
        run_ffmpeg(args)
        return selected
    filters = (
        "[1:a]asplit=2[raw][viz];"
        f"[raw]volume=0.70,afade=t=in:st=0:d=1,afade=t=out:st={fade_out_start(2.0)}:d=2[aout];"
        "[viz]showwaves=s=920x420:mode=cline:rate=30:colors=0xE5E7EB,format=rgba[wave];"
        "[0:v]noise=alls=2:allf=t,eq=brightness=-0.08:saturation=0.65[bg];"
        "[bg][wave]overlay=80:650[visual];"
        f"[visual]{draw_filters(title_file, artist_file, greeting_files, greeting_enabled)}[base]"
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
        "-t", str(DURATION_SECONDS), "-r", "30",
        "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        "-b:v", "1400k", "-maxrate", "1800k", "-bufsize", "2800k",
        "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", str(output),
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
        "canonicalGreetingText": str(spec.get("canonicalGreetingText") or "") if isinstance(spec, dict) else "",
        "claimToken": env("CLAIM_TOKEN", required=False) or None,
        "videoBase64": base64.b64encode(video.read_bytes()).decode("ascii"),
        "thumbnailBase64": base64.b64encode(thumbnail.read_bytes()).decode("ascii"),
        "durationMs": int(round(DURATION_SECONDS * 1000)), "width": WIDTH, "height": HEIGHT,
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
    global FFMPEG, DURATION_SECONDS
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
                DURATION_SECONDS = DEFAULT_DURATION_SECONDS
                work = root / f"candidate-{index}"
                work.mkdir(parents=True, exist_ok=True)
                media_type = str(candidate.get("mediaType") or "").lower()
                if media_type == "video":
                    render_video_candidate(candidate, spec, work, output)
                elif media_type == "audio":
                    render_audio_candidate(candidate, spec, work, output)
                else:
                    raise RuntimeError(f"unsupported mediaType: {media_type}")
                if output.stat().st_size > MAX_INLINE_VIDEO_BYTES:
                    raise RuntimeError("rendered MP4 exceeds bridge 8 MiB limit")
                selected = candidate
                print(f"rendered candidate #{index}: {candidate.get('title')}")
                break
            except Exception as exc:
                print(f"candidate #{index} failed: {type(exc).__name__}: {exc}")
                if output.exists():
                    output.unlink()
        if selected is None:
            DURATION_SECONDS = DEFAULT_DURATION_SECONDS
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
