from __future__ import annotations

import importlib.util
import io
import json
import pathlib
import tempfile
import unittest
from unittest import mock


MODULE_PATH = pathlib.Path(__file__).with_name("claim_and_render.py")
SPEC = importlib.util.spec_from_file_location("claim_and_render", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
claim_and_render = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(claim_and_render)


class FakeResponse:
    def __init__(self, body: bytes, *, url: str, status: int = 200):
        self._body = io.BytesIO(body)
        self._url = url
        self.status = status

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        return False

    def read(self, size: int = -1) -> bytes:
        return self._body.read(size)

    def geturl(self) -> str:
        return self._url


class RequestedSongAppleFallbackTests(unittest.TestCase):
    def test_strong_title_match_rejects_prefix_collisions_and_alternate_versions(self) -> None:
        query = "Gói con tim làm quà"
        self.assertTrue(
            claim_and_render.strong_requested_title_match(
                query,
                "Gói Con Tim Làm Quà (feat. Mounter) [Bò Sữa Bay Original Soundtrack]",
            )
        )
        self.assertTrue(
            claim_and_render.strong_requested_title_match(
                query,
                "Gói Con Tim Làm Quà (Film Version)",
            )
        )
        self.assertFalse(
            claim_and_render.strong_requested_title_match(
                query,
                "Gói Con Tim Làm Quà Remix",
            )
        )
        self.assertFalse(
            claim_and_render.strong_requested_title_match(
                query,
                "Gói Con Tim Làm Quà Karaoke",
            )
        )
        self.assertFalse(
            claim_and_render.strong_requested_title_match(
                "Hello",
                "Hello Goodbye",
            )
        )

    def test_itunes_preview_downloads_strong_match_and_prefers_original_soundtrack(self) -> None:
        query = "Gói con tim làm quà"
        preview_original = "https://audio-ssl.itunes.apple.com/itunes-assets/original.m4a"
        preview_film = "https://audio-ssl.itunes.apple.com/itunes-assets/film.m4a"
        payload = {
            "results": [
                {
                    "trackId": 11,
                    "trackName": "Gói Con Tim Làm Quà (Film Version)",
                    "artistName": "Mounter",
                    "previewUrl": preview_film,
                    "trackViewUrl": "https://music.apple.com/vn/album/film/11",
                },
                {
                    "trackId": 22,
                    "trackName": "Gói Con Tim Làm Quà (feat. Mounter) [Bò Sữa Bay Original Soundtrack]",
                    "artistName": "Nguyễn Hùng",
                    "previewUrl": preview_original,
                    "trackViewUrl": "https://music.apple.com/vn/album/original/22",
                    "artworkUrl100": "https://is1-ssl.mzstatic.com/image/thumb/cover/100x100bb.jpg",
                },
                {
                    "trackId": 33,
                    "trackName": "Phép Màu",
                    "artistName": "MAYDAYs",
                    "previewUrl": "https://audio-ssl.itunes.apple.com/itunes-assets/wrong.m4a",
                },
            ]
        }
        seen_urls: list[str] = []

        def fake_urlopen(request, timeout=0):
            url = request.full_url
            seen_urls.append(url)
            if url.startswith(claim_and_render.ITUNES_SEARCH_URL):
                return FakeResponse(
                    json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                    url=url,
                )
            if url == preview_original:
                return FakeResponse(b"apple-preview-audio", url=url)
            self.fail(f"unexpected URL opened: {url}")

        with tempfile.TemporaryDirectory() as temp:
            with mock.patch.object(claim_and_render.urllib.request, "urlopen", side_effect=fake_urlopen):
                candidate = claim_and_render.itunes_preview_candidate(query, pathlib.Path(temp))

            self.assertEqual("itunes-22", candidate["id"])
            self.assertEqual(query, candidate["title"])
            self.assertEqual("Nguyễn Hùng", candidate["artist"])
            self.assertEqual("Apple Music preview", candidate["licenseLabel"])
            self.assertTrue(pathlib.Path(candidate["mediaPath"]).read_bytes())
            self.assertEqual(preview_original, seen_urls[-1])

    def test_itunes_preview_rejects_weak_or_unrelated_title(self) -> None:
        query = "Gói con tim làm quà"
        payload = {
            "results": [
                {
                    "trackId": 33,
                    "trackName": "Phép Màu",
                    "artistName": "MAYDAYs",
                    "previewUrl": "https://audio-ssl.itunes.apple.com/itunes-assets/wrong.m4a",
                }
            ]
        }
        search_calls = 0

        def fake_urlopen(request, timeout=0):
            nonlocal search_calls
            search_calls += 1
            return FakeResponse(
                json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                url=request.full_url,
            )

        with tempfile.TemporaryDirectory() as temp:
            with mock.patch.object(claim_and_render.urllib.request, "urlopen", side_effect=fake_urlopen):
                with self.assertRaisesRegex(RuntimeError, "no confident title match"):
                    claim_and_render.itunes_preview_candidate(query, pathlib.Path(temp))
        self.assertEqual(1, search_calls)

    def test_build_requested_candidate_uses_itunes_only_after_spotdl_failure(self) -> None:
        expected = {
            "id": "itunes-22",
            "title": "Gói con tim làm quà",
            "artist": "Nguyễn Hùng",
            "mediaType": "audio",
            "mediaPath": "preview.m4a",
        }
        with tempfile.TemporaryDirectory() as temp:
            with (
                mock.patch.object(
                    claim_and_render,
                    "spotdl_download",
                    side_effect=RuntimeError("hosted YouTube blocked"),
                ),
                mock.patch.object(
                    claim_and_render,
                    "itunes_preview_candidate",
                    return_value=expected,
                ) as apple,
            ):
                actual = claim_and_render.build_requested_candidate(
                    "Gói con tim làm quà",
                    pathlib.Path(temp),
                )
        self.assertIs(expected, actual)
        apple.assert_called_once()

    def test_spotdl_metadata_rejects_unrelated_resolved_title(self) -> None:
        class FakeFrame:
            def __init__(self, value: str):
                self.value = value

            def __str__(self) -> str:
                return self.value

        class FakeTags(dict):
            def values(self):
                return []

        fake_tags = FakeTags(
            TIT2=FakeFrame("Hello Goodbye"),
            TPE1=FakeFrame("Wrong Artist"),
        )
        with tempfile.TemporaryDirectory() as temp:
            audio = pathlib.Path(temp) / "song.mp3"
            audio.write_bytes(b"fake")
            with mock.patch.dict(
                "sys.modules",
                {"mutagen.id3": mock.Mock(ID3=mock.Mock(return_value=fake_tags))},
            ):
                with self.assertRaisesRegex(RuntimeError, "non-matching title"):
                    claim_and_render.extract_spotdl_metadata(
                        audio,
                        pathlib.Path(temp),
                        "Hello",
                    )


if __name__ == "__main__":
    unittest.main()
