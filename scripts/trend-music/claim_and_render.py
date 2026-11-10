from __future__ import annotations

import json
import os
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


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


def main() -> int:
    scheduler_url = required("SCHEDULER_URL")
    scheduler_key = required("SCHEDULER_KEY")
    api_base = api_base_from_scheduler_url(scheduler_url)
    claim_url = f"{api_base}/api/internal/trend-music/render-claim"
    callback_url = f"{api_base}/api/internal/trend-music/render-complete"

    claim = claim_render(claim_url, scheduler_key)
    if claim is None:
        print("no pending trend-music render job")
        return 0

    candidates_json = claim_field(claim, "candidatesJson")
    parsed_candidates = json.loads(candidates_json)
    if not isinstance(parsed_candidates, list):
        raise RuntimeError("claimed candidatesJson must contain a JSON array")

    env = os.environ.copy()
    env.update(
        {
            "ACCOUNT_ID": claim_field(claim, "accountId"),
            "GROUP_ID": claim_field(claim, "groupId"),
            "CONNECTION_ID": claim_field(claim, "connectionId"),
            "LOCAL_DATE": claim_field(claim, "localDate"),
            "IDEMPOTENCY_KEY": claim_field(claim, "idempotencyKey"),
            "CALLBACK_URL": callback_url,
            "CALLBACK_KEY": scheduler_key,
            "CANDIDATES_JSON": candidates_json,
        }
    )
    renderer = pathlib.Path(__file__).with_name("render_story.py")
    print(
        "claimed trend-music render job",
        env["IDEMPOTENCY_KEY"],
        f"candidates={len(parsed_candidates)}",
    )
    subprocess.run([sys.executable, str(renderer)], env=env, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
