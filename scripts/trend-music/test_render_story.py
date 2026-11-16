from __future__ import annotations

import importlib.util
import os
import pathlib
import tempfile
import unittest


MODULE_PATH = pathlib.Path(__file__).with_name("render_story.py")
SPEC = importlib.util.spec_from_file_location("render_story", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
render_story = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(render_story)


class StoryRenderContractTests(unittest.TestCase):
    def test_requested_song_preserves_full_source_duration(self) -> None:
        self.assertEqual(20.0, render_story.requested_media_duration_seconds(20.0))
        self.assertEqual(45.0, render_story.requested_media_duration_seconds(45.0))
        self.assertEqual(60.0, render_story.requested_media_duration_seconds(60.0))
        self.assertEqual(187.2, render_story.requested_media_duration_seconds(187.2))

    def test_automatic_story_preserves_apple_preview_duration(self) -> None:
        original_duration = render_story.DURATION_SECONDS
        original_probe = render_story.probe_media_duration_seconds
        original_key = os.environ.get("IDEMPOTENCY_KEY")
        try:
            os.environ["IDEMPOTENCY_KEY"] = "trend-music:daily:test"
            render_story.DURATION_SECONDS = render_story.DEFAULT_DURATION_SECONDS
            render_story.probe_media_duration_seconds = lambda _: 30.024

            render_story.configure_duration_for_audio(pathlib.Path("preview.m4a"))

            self.assertAlmostEqual(30.024, render_story.DURATION_SECONDS, places=3)
        finally:
            render_story.DURATION_SECONDS = original_duration
            render_story.probe_media_duration_seconds = original_probe
            if original_key is None:
                os.environ.pop("IDEMPOTENCY_KEY", None)
            else:
                os.environ["IDEMPOTENCY_KEY"] = original_key

    def test_apple_music_page_prefers_extended_track_preview(self) -> None:
        html = (
            '{"id":"track-lockup - 6808519782 - 6808519787","title":"LAVIEM",'
            '"previewUrl":"https://audio-ssl.itunes.apple.com/itunes-assets/AudioPreview211/'
            'v4/example/mzaf_example.plus.aac.ep.m4a"}'
        )

        self.assertEqual(
            "https://audio-ssl.itunes.apple.com/itunes-assets/AudioPreview211/v4/example/mzaf_example.plus.aac.ep.m4a",
            render_story.apple_music_preview_from_html(html, "6808519787"),
        )

    def test_long_preview_bitrate_scales_down_instead_of_cutting_duration(self) -> None:
        original_duration = render_story.DURATION_SECONDS
        original_key = os.environ.get("IDEMPOTENCY_KEY")
        try:
            os.environ["IDEMPOTENCY_KEY"] = "trend-music:request:test"
            render_story.DURATION_SECONDS = 45.0
            bitrate_45 = render_story.video_bitrate_kbps(1700)
            render_story.DURATION_SECONDS = 180.0
            bitrate_180 = render_story.video_bitrate_kbps(1700)
        finally:
            render_story.DURATION_SECONDS = original_duration
            if original_key is None:
                os.environ.pop("IDEMPOTENCY_KEY", None)
            else:
                os.environ["IDEMPOTENCY_KEY"] = original_key

        self.assertGreater(bitrate_45, bitrate_180)
        self.assertGreaterEqual(bitrate_180, 48)

    def test_story_uses_full_hd_vertical_canvas(self) -> None:
        self.assertEqual(1080, render_story.WIDTH)
        self.assertEqual(1920, render_story.HEIGHT)

    def test_story_text_keeps_vietnamese_and_strips_unsupported_emoji_symbols(self) -> None:
        cleaned = render_story.clean_story_text(
            "Chúc bạn ngủ ngon nha 🌙🤍 nghe nhạc nhẹ trước khi ngủ.",
            200,
        )

        self.assertIn("Chúc bạn ngủ ngon nha", cleaned)
        self.assertIn("nghe nhạc nhẹ trước khi ngủ", cleaned)
        self.assertNotIn("🌙", cleaned)
        self.assertNotIn("🤍", cleaned)

    def test_greeting_wrap_is_bounded_for_story_safe_zone(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            target = pathlib.Path(temp) / "greeting.txt"
            line_files = render_story.safe_greeting(
                target,
                "Chúc bạn ngủ ngon nha. Nghe đoạn nhạc nhẹ nhàng này trước khi ngủ nha, iu bạn. "
                "Mong ngày mai thức dậy với một chiếc mood thật dễ thương.",
            )
            lines = [path.read_text(encoding="utf-8") for path in line_files]

        self.assertLessEqual(len(lines), 3)
        self.assertTrue(all(len(line) <= 28 for line in lines))

    def test_greeting_drawtext_never_receives_newline_characters(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            line_files = render_story.safe_greeting(
                root / "greeting.txt",
                "Chào buổi sáng nha. Nghe chút nhạc nhẹ, mở đầu ngày mới thật dễ thương.",
            )

            self.assertEqual(3, len(line_files))
            for path in line_files:
                payload = path.read_bytes()
                self.assertNotIn(b"\n", payload)
                self.assertNotIn(b"\r", payload)

    def test_image_dimensions_detects_small_ccmixter_style_jpeg_without_pillow(self) -> None:
        # Minimal JPEG SOF0 stream; enough to exercise the stdlib-only dimension parser.
        jpeg = bytes.fromhex("FFD8 FFC0 0011 08 00C8 00C8 03011100021100031100 FFD9")
        with tempfile.TemporaryDirectory() as temp:
            target = pathlib.Path(temp) / "artwork.jpg"
            target.write_bytes(jpeg)

            self.assertEqual((200, 200), render_story.image_dimensions(target))
            self.assertTrue(render_story.is_low_resolution_artwork(target))

    def test_curated_background_is_available_for_automatic_story_periods(self) -> None:
        candidate = {"id": "ccmixter-71178", "title": "Come Home"}
        self.assertIsNotNone(render_story.curated_story_background({"period": "morning"}, candidate))
        night = render_story.curated_story_background({"period": "night"}, candidate)
        self.assertIsNotNone(night)
        self.assertIn(night.name, {"night-card-02-hd.png", "night-card-03-hd.png"})
        self.assertIsNone(render_story.curated_story_background({"period": "manual"}, candidate))


if __name__ == "__main__":
    unittest.main()
