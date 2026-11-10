from __future__ import annotations

import json
import os
import pathlib
import hashlib
import shutil
import subprocess
import sys
import tempfile
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

SPOTDL_VERSION = "4.6.0"


def required(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise RuntimeError(f"{name} is required")
    return value


def api_base_from_scheduler_url(value: str) -> str:
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != "https" or not parsed.netloc:
        raise RuntimeError("SCHEDULER_URL must be an absolute HTTPS URL")
    suffix = "/api/internal/scheduler/tick"
    path = parsed.path.rstrip("/")
    if not path.endswith(suffix):
        raise RuntimeError(f"SCHEDULER_URL must end with {suffix}")
    base_path = path[: -len(suffix)]
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, base_path, "", "")).rstrip("/")


def api_base_from_callback_url(value: str) -> str:
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != "https" or not parsed.netloc:
        raise RuntimeError("TREND_MUSIC_CALLBACK_URL must be an absolute HTTPS URL")
    suffix = "/api/internal/trend-music/render-complete"
    path = parsed.path.rstrip("/")
    if not path.endswith(suffix):
        raise RuntimeError(f"TREND_MUSIC_CALLBACK_URL must end with {suffix}")
    base_path = path[: -len(suffix)]
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, base_path, "", "")).rstrip("/")


def claim_render(claim_url: str, key: str) -> dict | None:
    last_error: Exception | None = None
    for attempt in range(1, 5):
        request = urllib.request.Request(
            claim_url,
            method="POST",
            data=b"",
            headers={
                "accept": "application/json",
                "x-trend-music-key": key,
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                if response.status == 204:
                    return None
                if response.status != 200:
                    raise RuntimeError(f"render claim returned HTTP {response.status}")
                payload = json.loads(response.read().decode("utf-8"))
                if not isinstance(payload, dict):
                    raise RuntimeError("render claim returned an invalid JSON object")
                return payload
        except urllib.error.HTTPError as exc:
            if exc.code == 204:
                return None
            body = exc.read().decode("utf-8", errors="replace")[:500]
            last_error = RuntimeError(f"render claim returned HTTP {exc.code}: {body}")
            if exc.code in (401, 403, 404):
                raise last_error
        except (urllib.error.URLError, TimeoutError, RuntimeError, json.JSONDecodeError) as exc:
            last_error = exc

        if attempt < 4:
            time.sleep(attempt * 8)

    raise RuntimeError(f"could not claim trend-music render job: {last_error}")


def claim_field(payload: dict, name: str) -> str:
    value = payload.get(name)
    if not isinstance(value, str) or not value.strip():
        raise RuntimeError(f"render claim is missing {name}")
    return value.strip()


def optional_claim_field(payload: dict, name: str) -> str:
    value = payload.get(name)
    return value.strip() if isinstance(value, str) else ""


def requested_song_query(render_request: object) -> str:
    if not isinstance(render_request, dict):
        return ""
    value = render_request.get("songQuery")
    if not isinstance(value, str):
        return ""
    value = " ".join(value.split()).strip()
    return value[:120]


def run_checked(args: list[str], *, cwd: pathlib.Path | None = None, timeout: int = 240) -> subprocess.CompletedProcess[str]:
    child_env = os.environ.copy()
    child_env.setdefault("PYTHONUTF8", "1")
    child_env.setdefault("PYTHONIOENCODING", "utf-8")
    return subprocess.run(
        args,
        cwd=str(cwd) if cwd is not None else None,
        env=child_env,
        check=True,
        timeout=timeout,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )


def ensure_spotdl() -> None:
    try:
        run_checked([sys.executable, "-m", "spotdl", "--version"], timeout=30)
        return
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, FileNotFoundError):
        pass
    print(f"installing spotDL {SPOTDL_VERSION} for explicit song request")
    result = run_checked(
        [
            sys.executable,
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            f"spotdl=={SPOTDL_VERSION}",
        ],
        timeout=240,
    )
    if result.stdout:
        print(result.stdout[-2000:])


def spotdl_download(query: str, work: pathlib.Path) -> pathlib.Path:
    ensure_spotdl()
    base_command = [sys.executable, "-m", "spotdl"]
    ffmpeg = os.environ.get("TREND_MUSIC_FFMPEG", "").strip() or (shutil.which("ffmpeg") or "")
    if ffmpeg:
        base_command.extend(["--ffmpeg", ffmpeg])
    command = [*base_command, "download", query]

    def downloaded_audio_files() -> list[pathlib.Path]:
        return sorted(
            [
                *work.glob("*.mp3"),
                *work.glob("*.m4a"),
                *work.glob("*.opus"),
                *work.glob("*.ogg"),
                *work.glob("*.flac"),
            ],
            key=lambda path: path.stat().st_mtime_ns,
            reverse=True,
        )

    try:
        result = run_checked(command, cwd=work, timeout=300)
        if result.stdout:
            print(result.stdout[-3000:])
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as first_error:
        print(f"spotDL first download attempt failed: {type(first_error).__name__}")
        # Some spotDL/yt-dlp failures are reported with a successful process exit.
        # Fall through to the file-presence check below so those cases get the
        # exact same Deno bootstrap + retry as a non-zero exit.

    audio_files = downloaded_audio_files()
    if not audio_files:
        print("spotDL produced no audio file; installing Deno and retrying once")
        deno = run_checked(
            [sys.executable, "-m", "spotdl", "--download-deno"],
            cwd=work,
            timeout=120,
        )
        if deno.stdout:
            print(deno.stdout[-1500:])
        retry = run_checked(command, cwd=work, timeout=300)
        if retry.stdout:
            print(retry.stdout[-3000:])
        audio_files = downloaded_audio_files()

    if not audio_files:
        # GitHub-hosted runners are periodically blocked by music.youtube.com even
        # when Deno is available. Keep Spotify/spotDL metadata resolution but switch
        # the audio provider to regular YouTube before giving up. This still lets
        # spotDL do its normal title/artist matching; it is not an arbitrary fallback
        # to a different track.
        print("YouTube Music still produced no audio; retrying with spotDL YouTube provider")
        youtube_command = [*base_command, "download", query, "--audio", "youtube"]
        youtube_retry = run_checked(youtube_command, cwd=work, timeout=300)
        if youtube_retry.stdout:
            print(youtube_retry.stdout[-3000:])
        audio_files = downloaded_audio_files()

    if not audio_files:
        raise RuntimeError("spotDL completed without an audio file")
    return audio_files[0]


def extract_spotdl_metadata(audio_path: pathlib.Path, work: pathlib.Path, query: str) -> tuple[str, str, pathlib.Path | None]:
    try:
        from mutagen.id3 import ID3

        tags = ID3(audio_path)
        title_frame = tags.get("TIT2")
        artist_frame = tags.get("TPE1")
        title = str(title_frame).strip() if title_frame is not None else ""
        artist = str(artist_frame).strip() if artist_frame is not None else ""
        artwork = None
        for frame in tags.values():
            if getattr(frame, "FrameID", "") != "APIC" or not getattr(frame, "data", b""):
                continue
            mime = str(getattr(frame, "mime", "")).lower()
            suffix = ".png" if "png" in mime else ".jpg"
            artwork = work / f"spotdl-cover{suffix}"
            artwork.write_bytes(frame.data)
            break
    except Exception as exc:
        print(f"could not read spotDL ID3 metadata: {type(exc).__name__}: {exc}")
        title, artist, artwork = "", "", None

    if not title:
        title = query
    if not artist:
        artist = "Không rõ nghệ sĩ"
    else:
        artist = artist.replace("/", ", ")

    def search_key(value: str) -> str:
        decomposed = unicodedata.normalize("NFKD", value)
        plain = "".join(ch for ch in decomposed if not unicodedata.combining(ch))
        return " ".join("".join(ch if ch.isalnum() else " " for ch in plain.casefold()).split())

    query_key = search_key(query)
    title_key = search_key(title)
    # spotDL/Spotify often appends version/OST qualifiers to the track title. When the
    # requested title is a clear prefix, keep the user's concise title in the Zalo story.
    if len(query_key) >= 4 and title_key.startswith(query_key):
        title = query
    return title[:120], artist[:120], artwork


def build_requested_candidate(query: str, work: pathlib.Path) -> dict:
    audio_path = spotdl_download(query, work)
    title, artist, artwork = extract_spotdl_metadata(audio_path, work, query)
    candidate_id = hashlib.sha256(f"{title}\n{artist}".encode("utf-8")).hexdigest()[:20]
    return {
        "id": f"spotdl-{candidate_id}",
        "title": title,
        "artist": artist,
        "rank": 1,
        "mediaType": "audio",
        "mediaUrl": "",
        "mediaPath": str(audio_path.resolve()),
        "artworkUrl": None,
        "artworkPath": str(artwork.resolve()) if artwork is not None else None,
        "trendLabel": "REQUESTED SONG",
        "sourceUrl": None,
        "licenseUrl": None,
        "licenseLabel": None,
    }


def post_progress(
    progress_url: str,
    key: str,
    claim: dict,
    state: str,
    *,
    title: str | None = None,
    artist: str | None = None,
) -> None:
    payload = {
        "accountId": claim_field(claim, "accountId"),
        "groupId": claim_field(claim, "groupId"),
        "connectionId": claim_field(claim, "connectionId"),
        "localDate": claim_field(claim, "localDate"),
        "idempotencyKey": claim_field(claim, "idempotencyKey"),
        "state": state,
        "title": title,
        "artist": artist,
        "claimToken": claim_field(claim, "claimToken"),
    }
    request = urllib.request.Request(
        progress_url,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        method="POST",
        headers={
            "Content-Type": "application/json",
            "x-trend-music-key": key,
            "User-Agent": "VolleyDraft-TrendMusic-Renderer/1.0",
        },
    )
    with urllib.request.urlopen(request, timeout=45) as response:
        if not (200 <= response.status < 300):
            raise RuntimeError(f"render progress returned HTTP {response.status}")


def main() -> int:
    scheduler_key = required("SCHEDULER_KEY")
    internal_key = os.environ.get("TREND_MUSIC_CALLBACK_KEY", "").strip() or scheduler_key
    callback_url = os.environ.get("TREND_MUSIC_CALLBACK_URL", "").strip()
    if callback_url:
        api_base = api_base_from_callback_url(callback_url)
    else:
        scheduler_url = required("SCHEDULER_URL")
        api_base = api_base_from_scheduler_url(scheduler_url)
        callback_url = f"{api_base}/api/internal/trend-music/render-complete"
    claim_url = f"{api_base}/api/internal/trend-music/render-claim"
    progress_url = f"{api_base}/api/internal/trend-music/render-progress"

    claim = claim_render(claim_url, internal_key)
    if claim is None:
        print("no pending trend-music render job")
        return 0

    candidates_json = claim_field(claim, "candidatesJson")
    parsed_render = json.loads(candidates_json)
    if isinstance(parsed_render, list):
        candidate_count = len(parsed_render)
    elif isinstance(parsed_render, dict) and isinstance(parsed_render.get("candidates"), list):
        candidate_count = len(parsed_render["candidates"])
    else:
        raise RuntimeError("claimed candidatesJson must contain a candidate array or render envelope")

    song_query = requested_song_query(parsed_render)
    local_media_root: pathlib.Path | None = None
    if song_query:
        local_media_root = pathlib.Path(tempfile.mkdtemp(prefix="trend-music-spotdl-"))
        try:
            candidate = build_requested_candidate(song_query, local_media_root)
        except Exception as exc:
            try:
                post_progress(progress_url, internal_key, claim, "failed", title=song_query)
            except Exception as progress_exc:
                print(f"failed to report requested-song failure: {type(progress_exc).__name__}: {progress_exc}")
            raise RuntimeError(f"requested song could not be prepared: {type(exc).__name__}: {exc}") from exc

        if not isinstance(parsed_render, dict):
            raise RuntimeError("requested song render must use an envelope")
        parsed_render["candidates"] = [candidate]
        candidates_json = json.dumps(parsed_render, ensure_ascii=False)
        candidate_count = 1
        try:
            post_progress(
                progress_url,
                internal_key,
                claim,
                "matched",
                title=str(candidate["title"]),
                artist=str(candidate["artist"]),
            )
        except Exception as progress_exc:
            # Progress text is useful UX, but a transient Zalo text failure must not discard
            # a song that was already matched/downloaded successfully.
            print(f"failed to report requested-song match: {type(progress_exc).__name__}: {progress_exc}")
        print(f"matched requested song: {candidate['title']} — {candidate['artist']}")

    env = os.environ.copy()
    env.update(
        {
            "ACCOUNT_ID": claim_field(claim, "accountId"),
            "GROUP_ID": claim_field(claim, "groupId"),
            "CONNECTION_ID": claim_field(claim, "connectionId"),
            "LOCAL_DATE": claim_field(claim, "localDate"),
            "IDEMPOTENCY_KEY": claim_field(claim, "idempotencyKey"),
            "CLAIM_TOKEN": claim_field(claim, "claimToken"),
            "CALLBACK_URL": callback_url,
            "CALLBACK_KEY": internal_key,
            "CANDIDATES_JSON": candidates_json,
        }
    )
    if local_media_root is not None:
        env["TREND_MUSIC_LOCAL_MEDIA_ROOT"] = str(local_media_root.resolve())
    renderer = pathlib.Path(__file__).with_name("render_story.py")
    print(
        "claimed trend-music render job",
        env["IDEMPOTENCY_KEY"],
        f"candidates={candidate_count}",
    )
    try:
        subprocess.run([sys.executable, str(renderer)], env=env, check=True)
        return 0
    finally:
        if local_media_root is not None:
            shutil.rmtree(local_media_root, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
