#!/usr/bin/env python3
import base64
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import time
import urllib.request

DURATION_SECONDS = 20
WIDTH = 720
HEIGHT = 1280
MAX_DOWNLOAD_BYTES = 64 * 1024 * 1024
FFMPEG = ""

def env(name: str, required: bool = True) -> str:
    value = os.environ.get(name, "").strip()
    if required and not value:
        raise RuntimeError(f"missing required environment variable: {name}")
    return value

def truthy_env(name: str) -> bool:
    return os.environ.get(name, "").strip().lower() in {"1", "true", "yes", "on"}

def download(url: str, target: pathlib.Path, referer: str | None = None) -> None:
    headers = {"User-Agent": "Mozilla/5.0 (compatible; VolleyDraft-TrendMusic/1.0)"}
    if referer:
        headers["Referer"] = referer
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=30) as response:
        total = 0
        with target.open("wb") as output:
            while True:
                chunk = response.read(1024 * 256)
                if not chunk:
                    break
                total += len(chunk)
                if total > MAX_DOWNLOAD_BYTES:
                    raise RuntimeError("media download exceeds 64 MiB")
                output.write(chunk)
    if target.stat().st_size == 0:
        raise RuntimeError("media download is empty")

def run_ffmpeg(args: list[str]) -> None:
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *args], check=True, timeout=180)

def safe_text(path: pathlib.Path, value: str) -> None:
    path.write_text(" ".join(value.split())[:140], encoding="utf-8")

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

def draw_filters(title_file: pathlib.Path, artist_file: pathlib.Path, trend_file: pathlib.Path) -> str:
    bold, regular = font_files()
    bold_path = filter_path(bold)
    regular_path = filter_path(regular)
    title_path = filter_path(title_file)
    artist_path = filter_path(artist_file)
    trend_path = filter_path(trend_file)
    return (
        "drawbox=x=24:y=36:w=360:h=64:color=black@0.55:t=fill,"
        f"drawtext=fontfile={bold_path}:textfile={trend_path}:fontcolor=white:fontsize=27:x=44:y=53,"
        "drawbox=x=0:y=1010:w=720:h=270:color=black@0.58:t=fill,"
        f"drawtext=fontfile={bold_path}:textfile={title_path}:fontcolor=white:fontsize=42:x=42:y=1050:fix_bounds=true,"
        f"drawtext=fontfile={regular_path}:textfile={artist_path}:fontcolor=white@0.90:fontsize=30:x=42:y=1122:fix_bounds=true,"
        f"drawtext=fontfile={regular_path}:text='NPC MUSIC':fontcolor=white@0.65:fontsize=22:x=42:y=1198"
    )

def render_video_candidate(candidate: dict, work: pathlib.Path, output: pathlib.Path) -> None:
    media = work / "source-video"
    download(candidate["mediaUrl"], media, candidate.get("sourceUrl"))
    title_file, artist_file, trend_file = work / "title.txt", work / "artist.txt", work / "trend.txt"
    safe_text(title_file, candidate.get("title", "NPC PICK"))
    safe_text(artist_file, candidate.get("artist", ""))
    safe_text(trend_file, candidate.get("trendLabel") or f"TRENDING #{candidate.get('rank', '?')}")
    filters = (
        "[0:v]split=2[bg0][fg0];"
        "[bg0]scale=720:1280:force_original_aspect_ratio=increase,crop=720:1280,setsar=1,boxblur=18:2[bg];"
        "[fg0]scale=680:900:force_original_aspect_ratio=decrease,setsar=1[fg];"
        "[bg][fg]overlay=(W-w)/2:(H-h)/2-30,setsar=1[base];"
        f"[base]{draw_filters(title_file, artist_file, trend_file)}[v]"
    )
    run_ffmpeg([
        "-stream_loop", "-1", "-i", str(media),
        "-filter_complex", filters, "-map", "[v]", "-map", "0:a:0?",
        "-t", str(DURATION_SECONDS), "-r", "30",
        "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
        "-b:v", "900k", "-maxrate", "1100k", "-bufsize", "1800k",
        "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
    ])

def render_audio_candidate(candidate: dict, work: pathlib.Path, output: pathlib.Path) -> None:
    audio, artwork = work / "source-audio", work / "artwork"
    download(candidate["mediaUrl"], audio, candidate.get("sourceUrl"))
    artwork_url = (candidate.get("artworkUrl") or "").strip()
    title_file, artist_file, trend_file = work / "title.txt", work / "artist.txt", work / "trend.txt"
    safe_text(title_file, candidate.get("title", "NPC PICK"))
    safe_text(artist_file, candidate.get("artist", ""))
    safe_text(trend_file, candidate.get("trendLabel") or f"TRENDING #{candidate.get('rank', '?')}")
    if not artwork_url:
        filters = (
            "[1:a]asplit=2[aout0][viz];"
            "[aout0]afade=t=in:st=0:d=0.7,afade=t=out:st=18:d=2[aout];"
            "[viz]showwaves=s=600x250:mode=cline:rate=30:colors=0x67e8f9,format=rgba[wave];"
            "[0:v]noise=alls=3:allf=t,"
            "drawbox=x=44:y=240:w=632:h=500:color=white@0.045:t=fill,"
            "drawbox=x=68:y=264:w=584:h=452:color=0x172554@0.58:t=fill,"
            "drawbox=x=68:y=264:w=584:h=4:color=0x67e8f9@0.75:t=fill[bg];"
            "[bg][wave]overlay=60:370[base];"
            f"[base]{draw_filters(title_file, artist_file, trend_file)}[v]"
        )
        run_ffmpeg([
            "-f", "lavfi", "-i", f"color=c=0x0f172a:s={WIDTH}x{HEIGHT}:r=30:d={DURATION_SECONDS}",
            "-stream_loop", "-1", "-i", str(audio),
            "-filter_complex", filters, "-map", "[v]", "-map", "[aout]",
            "-t", str(DURATION_SECONDS), "-r", "30",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-b:v", "720k", "-maxrate", "900k", "-bufsize", "1440k",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
        ])
        return
    download(artwork_url, artwork)
    filters = (
        "[0:v]split=2[bg0][cover0];"
        "[bg0]scale=720:1280:force_original_aspect_ratio=increase,crop=720:1280,setsar=1,boxblur=22:2[bg];"
        "[cover0]scale=560:560:force_original_aspect_ratio=decrease,setsar=1[cover];"
        "[bg][cover]overlay=(W-w)/2:260,setsar=1[base];"
        f"[base]{draw_filters(title_file, artist_file, trend_file)}[v]"
    )
    run_ffmpeg([
        "-loop", "1", "-i", str(artwork), "-stream_loop", "-1", "-i", str(audio),
        "-filter_complex", filters, "-map", "[v]", "-map", "1:a:0",
        "-t", str(DURATION_SECONDS), "-r", "30",
        "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
        "-b:v", "800k", "-maxrate", "1000k", "-bufsize", "1600k",
        "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(output),
    ])

def render_fallback(work: pathlib.Path, output: pathlib.Path) -> dict:
    title_file, artist_file, trend_file = work / "title.txt", work / "artist.txt", work / "trend.txt"
    safe_text(title_file, "Morning Pulse")
    safe_text(artist_file, "VolleyDraft NPC")
    safe_text(trend_file, "NPC ORIGINAL")
    filters = (
        "[1:a]asplit=2[raw][viz];"
        "[raw]volume=0.70,afade=t=in:st=0:d=1,afade=t=out:st=18:d=2[aout];"
        "[viz]showwaves=s=600x220:mode=cline:rate=30:colors=0x67e8f9,format=rgba[wave];"
        "[0:v]noise=alls=3:allf=t,"
        "drawbox=x=40:y=230:w=640:h=500:color=white@0.045:t=fill,"
        "drawbox=x=64:y=254:w=592:h=452:color=0x312e81@0.22:t=fill,"
        "drawbox=x=64:y=254:w=592:h=4:color=0x67e8f9@0.70:t=fill[bg];"
        "[bg][wave]overlay=60:360[visual];"
        f"[visual]{draw_filters(title_file, artist_file, trend_file)}[v]"
    )
    original_audio = (
        "aevalsrc=(0.62+0.38*sin(2*PI*2*t))*"
        "(0.065*sin(2*PI*110*t)+0.055*sin(2*PI*220*t)+"
        "0.040*sin(2*PI*277.18*t)+0.032*sin(2*PI*329.63*t)):"
        f"s=44100:d={DURATION_SECONDS}"
    )
    run_ffmpeg([
        "-f", "lavfi", "-i", f"color=c=0x111827:s={WIDTH}x{HEIGHT}:r=30:d={DURATION_SECONDS}",
        "-f", "lavfi", "-i", original_audio,
        "-filter_complex", filters, "-map", "[v]", "-map", "[aout]",
        "-t", str(DURATION_SECONDS), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        "-b:v", "650k", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", str(output),
    ])
    return {"id": "npc-original-fallback", "title": "Morning Pulse", "artist": "VolleyDraft NPC", "rank": 0}

def make_thumbnail(video: pathlib.Path, thumbnail: pathlib.Path) -> None:
    run_ffmpeg([
        "-ss", "00:00:01", "-i", str(video), "-frames:v", "1",
        "-vf", f"scale={WIDTH}:{HEIGHT}:force_original_aspect_ratio=decrease,pad={WIDTH}:{HEIGHT}:(ow-iw)/2:(oh-ih)/2",
        "-q:v", "3", str(thumbnail),
    ])

def deliver(video: pathlib.Path, thumbnail: pathlib.Path, selected: dict) -> None:
    payload = {
        "accountId": env("ACCOUNT_ID"), "groupId": env("GROUP_ID"), "connectionId": env("CONNECTION_ID"),
        "localDate": env("LOCAL_DATE"), "idempotencyKey": env("IDEMPOTENCY_KEY"),
        "candidateId": str(selected.get("id") or "music"), "title": str(selected.get("title") or "NPC PICK"),
        "artist": str(selected.get("artist") or ""), "rank": int(selected.get("rank") or 0),
        "sourceUrl": selected.get("sourceUrl"), "licenseUrl": selected.get("licenseUrl"),
        "licenseLabel": selected.get("licenseLabel"),
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
    candidates = json.loads(env("CANDIDATES_JSON"))
    if not isinstance(candidates, list):
        raise RuntimeError("CANDIDATES_JSON must be an array")
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
                    render_video_candidate(candidate, work, output)
                elif media_type == "audio":
                    render_audio_candidate(candidate, work, output)
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
            selected = render_fallback(work, output)
            print("all candidates failed; rendered deterministic NPC original fallback")
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
        deliver(output, thumbnail, selected)

if __name__ == "__main__":
    main()
