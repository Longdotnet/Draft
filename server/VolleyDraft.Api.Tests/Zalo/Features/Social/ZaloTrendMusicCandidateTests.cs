using VolleyDraft.Api.Services;
using VolleyDraft.Api.Contracts;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Social;

public sealed class ZaloTrendMusicCandidateTests
{
    [Fact]
    public void Story_greeting_combines_morning_wish_with_music_invitation()
    {
        var text = ZaloTrendMusicService.StoryGreetingText(ZaloDailyGreetingKind.Morning);

        Assert.Contains("Chào buổi sáng", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("đoạn nhạc nhẹ nhàng", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ngày mới", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Length < 100);
    }

    [Fact]
    public void Story_greeting_combines_night_wish_with_bedtime_music_invitation()
    {
        var text = ZaloTrendMusicService.StoryGreetingText(ZaloDailyGreetingKind.Night);

        Assert.Contains("Chúc bạn ngủ ngon", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trước khi ngủ", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iu bạn", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Length < 100);
    }

    [Fact]
    public void Daily_greeting_history_prefers_canonical_copy_over_story_overlay_copy()
    {
        var request = new ZaloTrendMusicRenderCompleteRequest(
            "account", "group", "connection", "2026-11-10", "key", "ccmixter-1",
            "Come Home", "Gabriel Shellington", 1, "video", "thumb",
            GreetingText: "Khuya rồi nha. Nghe đoạn nhạc nhẹ nhàng này trước khi ngủ nha, iu bạn.",
            CanonicalGreetingText: "Ngủ ngoan nha mọi người. Mong đêm nay dịu dàng với mỗi người hơn một chút 🤍");

        Assert.Equal(
            "Ngủ ngoan nha mọi người. Mong đêm nay dịu dàng với mỗi người hơn một chút 🤍",
            ZaloTrendMusicService.GreetingHistoryText(request));
    }

    [Fact]
    public void Daily_greeting_history_keeps_backward_compatibility_for_old_renderer_callback()
    {
        var request = new ZaloTrendMusicRenderCompleteRequest(
            "account", "group", "connection", "2026-11-10", "key", "ccmixter-1",
            "Come Home", "Gabriel Shellington", 1, "video", "thumb",
            GreetingText: "Legacy greeting");

        Assert.Equal("Legacy greeting", ZaloTrendMusicService.GreetingHistoryText(request));
    }

    [Fact]
    public void Jamendo_parser_keeps_only_downloadable_content_id_free_permissive_tracks_in_weekly_order()
    {
        var json = """
            {
              "results": [
                {
                  "id": "100",
                  "name": "Allowed One",
                  "artist_name": "Artist A",
                  "image": "https://img.example/a.jpg",
                  "shareurl": "https://www.jamendo.com/track/100/allowed-one",
                  "audiodownload": "https://audio.example/a.mp3",
                  "audiodownload_allowed": true,
                  "content_id_free": true,
                  "license_ccurl": "http://creativecommons.org/licenses/by/4.0/"
                },
                {
                  "id": "200",
                  "name": "No Derivatives",
                  "artist_name": "Artist B",
                  "image": "https://img.example/b.jpg",
                  "shareurl": "https://www.jamendo.com/track/200/no-derivatives",
                  "audiodownload": "https://audio.example/b.mp3",
                  "audiodownload_allowed": true,
                  "content_id_free": true,
                  "license_ccurl": "https://creativecommons.org/licenses/by-nd/4.0/"
                },
                {
                  "id": "300",
                  "name": "CC Zero",
                  "artist_name": "Artist C",
                  "album_image": "https://img.example/c.jpg",
                  "shareurl": "https://www.jamendo.com/track/300/cc-zero",
                  "audiodownload": "https://audio.example/c.mp3",
                  "audiodownload_allowed": true,
                  "content_id_free": true,
                  "license_ccurl": "https://creativecommons.org/publicdomain/zero/1.0/"
                },
                {
                  "id": "400",
                  "name": "Content ID Risk",
                  "artist_name": "Artist D",
                  "image": "https://img.example/d.jpg",
                  "shareurl": "https://www.jamendo.com/track/400/content-id-risk",
                  "audiodownload": "https://audio.example/d.mp3",
                  "audiodownload_allowed": true,
                  "content_id_free": false,
                  "license_ccurl": "https://creativecommons.org/licenses/by/4.0/"
                }
              ]
            }
            """;

        var candidates = ZaloTrendMusicService.ParseJamendoCandidates(json, 5);

        Assert.Collection(candidates,
            first =>
            {
                Assert.Equal("jamendo-100", first.Id);
                Assert.Equal(1, first.Rank);
                Assert.Equal("audio", first.MediaType);
                Assert.Equal("CC BY 4.0", first.LicenseLabel);
                Assert.Equal("http://creativecommons.org/licenses/by/4.0/", first.LicenseUrl);
                Assert.Equal("https://www.jamendo.com/track/100/allowed-one", first.SourceUrl);
            },
            second =>
            {
                Assert.Equal("jamendo-300", second.Id);
                Assert.Equal(2, second.Rank);
                Assert.Equal("CC0 1.0", second.LicenseLabel);
            });
    }

    [Fact]
    public void Jamendo_parser_caps_candidates_and_rejects_non_https_media()
    {
        var json = """
            {
              "results": [
                {
                  "id": "1", "name": "Bad", "artist_name": "A",
                  "image": "https://img.example/1.jpg", "audiodownload": "http://audio.example/1.mp3",
                  "shareurl": "https://www.jamendo.com/track/1/bad",
                  "audiodownload_allowed": true, "content_id_free": true,
                  "license_ccurl": "https://creativecommons.org/licenses/by/4.0/"
                },
                {
                  "id": "2", "name": "Good", "artist_name": "B",
                  "image": "https://img.example/2.jpg", "audiodownload": "https://audio.example/2.mp3",
                  "shareurl": "https://www.jamendo.com/track/2/good",
                  "audiodownload_allowed": true, "content_id_free": true,
                  "license_ccurl": "https://creativecommons.org/licenses/by/4.0/"
                },
                {
                  "id": "3", "name": "Also Good", "artist_name": "C",
                  "image": "https://img.example/3.jpg", "audiodownload": "https://audio.example/3.mp3",
                  "shareurl": "https://www.jamendo.com/track/3/also-good",
                  "audiodownload_allowed": true, "content_id_free": true,
                  "license_ccurl": "https://creativecommons.org/licenses/by/4.0/"
                }
              ]
            }
            """;

        var candidates = ZaloTrendMusicService.ParseJamendoCandidates(json, 1);

        var only = Assert.Single(candidates);
        Assert.Equal("jamendo-2", only.Id);
    }

    [Fact]
    public void Jamendo_query_is_weekly_diverse_and_requests_downloadable_story_assets()
    {
        var uri = ZaloTrendMusicService.BuildJamendoUri("client id", 5);

        Assert.Contains("client_id=client%20id", uri);
        Assert.Contains("limit=25", uri);
        Assert.Contains("order=popularity_week", uri);
        Assert.Contains("groupby=artist_id", uri);
        Assert.Contains("content_id_free=true", uri);
        Assert.Contains("type=single%20albumtrack", uri);
        Assert.Contains("imagesize=600", uri);
        Assert.Contains("audiodlformat=mp32", uri);
        Assert.Contains("include=licenses", uri);
        Assert.DoesNotContain("audioformat=", uri);
    }

    [Fact]
    public void CcMixter_query_prefers_recent_ranked_full_remixes_with_attribution_license()
    {
        var uri = ZaloTrendMusicService.BuildCcMixterUri(5, "12 weeks ago");

        Assert.Contains("limit=20", uri);
        Assert.Contains("reqtags=remix", uri);
        Assert.Contains("lic=by", uri);
        Assert.Contains("sort=rank", uri);
        Assert.Contains("ord=DESC", uri);
        Assert.Contains("dataview=info", uri);
        Assert.Contains("sinced=12%20weeks%20ago", uri);
    }

    [Fact]
    public void CcMixter_parser_keeps_real_mp3_attribution_tracks_and_needs_no_artwork()
    {
        var json = """
            [{
              "upload_id": 71178,
              "upload_name": "Come Home",
              "user_real_name": "Gabriel Shellington",
              "file_page_url": "https://ccmixter.org/files/gabriel_shelligton/71178",
              "license_url": "http://creativecommons.org/licenses/by/3.0/",
              "qrank": 4,
              "files": [{
                "file_name": "gabriel_shelligton_-_Come_Home_1.mp3",
                "download_url": "https://ccmixter.org/content/gabriel_shelligton/gabriel_shelligton_-_Come_Home_1.mp3"
              }]
            }]
            """;

        var candidate = Assert.Single(ZaloTrendMusicService.ParseCcMixterCandidates(json, 5));
        Assert.Equal("ccmixter-71178", candidate.Id);
        Assert.Equal("Come Home", candidate.Title);
        Assert.Equal("Gabriel Shellington", candidate.Artist);
        Assert.Equal("audio", candidate.MediaType);
        Assert.Null(candidate.ArtworkUrl);
        Assert.Equal("CC BY 3.0", candidate.LicenseLabel);
        Assert.Equal("https://ccmixter.org/files/gabriel_shelligton/71178", candidate.SourceUrl);
    }

    [Fact]
    public void CcMixter_parser_rejects_non_mp3_and_non_permissive_licenses()
    {
        var json = """
            [
              {
                "upload_id": 1,
                "upload_name": "Wrong File",
                "user_real_name": "Artist",
                "file_page_url": "https://ccmixter.org/files/artist/1",
                "license_url": "http://creativecommons.org/licenses/by/3.0/",
                "files": [{"file_name":"track.wav","download_url":"https://ccmixter.org/content/artist/track.wav"}]
              },
              {
                "upload_id": 2,
                "upload_name": "NC",
                "user_real_name": "Artist",
                "file_page_url": "https://ccmixter.org/files/artist/2",
                "license_url": "http://creativecommons.org/licenses/by-nc/3.0/",
                "files": [{"file_name":"track.mp3","download_url":"https://ccmixter.org/content/artist/track.mp3"}]
              }
            ]
            """;

        Assert.Empty(ZaloTrendMusicService.ParseCcMixterCandidates(json, 5));
    }

    [Theory]
    [InlineData("https://creativecommons.org/licenses/by-sa/4.0/")]
    [InlineData("https://creativecommons.org/licenses/by-nc/4.0/")]
    [InlineData("https://creativecommons.org/licenses/by-nd/4.0/")]
    [InlineData("https://example.com/licenses/by/4.0/")]
    public void Jamendo_parser_rejects_non_permissive_or_unknown_licenses(string licenseUrl)
    {
        var json = $$"""
            {"results":[{
              "id":"10","name":"Blocked","artist_name":"Artist",
              "image":"https://img.example/10.jpg","shareurl":"https://www.jamendo.com/track/10/blocked",
              "audiodownload":"https://audio.example/10.mp3","audiodownload_allowed":true,"content_id_free":true,
              "license_ccurl":"{{licenseUrl}}"
            }]}
            """;

        Assert.Empty(ZaloTrendMusicService.ParseJamendoCandidates(json, 5));
    }

    [Fact]
    public void Jamendo_parser_rejects_non_https_source_page()
    {
        var json = """
            {"results":[{
              "id":"10","name":"Blocked","artist_name":"Artist",
              "image":"https://img.example/10.jpg","shareurl":"http://www.jamendo.com/track/10/blocked",
              "audiodownload":"https://audio.example/10.mp3","audiodownload_allowed":true,"content_id_free":true,
              "license_ccurl":"https://creativecommons.org/licenses/by/4.0/"
            }]}
            """;

        Assert.Empty(ZaloTrendMusicService.ParseJamendoCandidates(json, 5));
    }

    [Fact]
    public void Caption_includes_cc_by_attribution_and_adaptation_notice()
    {
        var request = new ZaloTrendMusicRenderCompleteRequest(
            "account", "group", "connection", "2026-11-10", "key", "jamendo-1",
            "Song", "Artist", 1, "video", "thumb",
            SourceUrl: "https://www.jamendo.com/track/1/song",
            LicenseUrl: "http://creativecommons.org/licenses/by/4.0/",
            LicenseLabel: "CC BY 4.0");

        var caption = ZaloTrendMusicService.BuildCaption(request);

        Assert.Contains("Nguồn: https://www.jamendo.com/track/1/song", caption);
        Assert.Contains("License: CC BY 4.0 http://creativecommons.org/licenses/by/4.0/", caption);
        Assert.Contains("Đã cắt/ghép thành video dọc 20s.", caption);
    }

    [Theory]
    [InlineData("test nhạc trend", true)]
    [InlineData("test nhạc", true)]
    [InlineData("thử nhạc trend", true)]
    [InlineData("test trend music", true)]
    [InlineData("nhạc trend", false)]
    public void Manual_test_command_requires_explicit_supported_phrase(string content, bool expected)
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account",
            "bot",
            "group",
            "message",
            "sender",
            "Tester",
            content,
            [],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(expected, ZaloTrendMusicService.IsManualTestCommand(incoming));
    }

    [Fact]
    public void Manual_test_command_requires_bot_mention()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account",
            "bot",
            "group",
            "message",
            "sender",
            "Tester",
            "test nhạc trend",
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(ZaloTrendMusicService.IsManualTestCommand(incoming));
    }

    [Theory]
    [InlineData("lấy nhạc \"Gói con tim làm quà\"", "Gói con tim làm quà")]
    [InlineData("lay nhac \"Goi con tim lam qua\"", "Goi con tim lam qua")]
    [InlineData("lấy nhạc Gói con tim làm quà", "Gói con tim làm quà")]
    [InlineData("lấy nhạc “Gói con tim làm quà”", "Gói con tim làm quà")]
    public void Requested_song_command_extracts_query(string content, string expected)
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account",
            "bot",
            "group",
            "message",
            "sender",
            "Tester",
            content,
            [],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(expected, ZaloTrendMusicService.ParseRequestedSongQuery(incoming));
    }

    [Theory]
    [InlineData("lấy nhạc \"Gói con tim làm quà\"", false)]
    [InlineData("phát nhạc \"Gói con tim làm quà\"", true)]
    public void Requested_song_command_requires_supported_phrase_and_bot_mention(string content, bool mentionedBot)
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account",
            "bot",
            "group",
            "message",
            "sender",
            "Tester",
            content,
            [],
            mentionedBot,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Null(ZaloTrendMusicService.ParseRequestedSongQuery(incoming));
    }

    [Fact]
    public void CcMixter_artwork_prefers_open_graph_then_twitter_and_rejects_external_hosts()
    {
        const string source = "https://ccmixter.org/files/artist/123";
        var html = """
            <html><head>
              <meta name="twitter:image" content="https://ccmixter.org/twitter.jpg">
              <meta property="og:image" content="/content/artist/cover.jpg">
            </head></html>
            """;

        Assert.Equal(
            "https://ccmixter.org/content/artist/cover.jpg",
            ZaloTrendMusicService.ExtractSourceArtworkUrl(html, source));
        Assert.Null(ZaloTrendMusicService.ExtractSourceArtworkUrl(
            "<meta property=\"og:image\" content=\"https://evil.example/cover.jpg\">",
            source));
        Assert.Null(ZaloTrendMusicService.ExtractSourceArtworkUrl(
            "<meta property=\"og:image\" content=\"http://ccmixter.org/cover.jpg\">",
            source));
    }

    [Fact]
    public void CcMixter_artwork_uses_twitter_image_from_real_track_shape()
    {
        const string html = """
            <html><head><meta name="twitter:image" content="https://ccmixter.org/tt34699.jpeg"></head></html>
            """;

        Assert.Equal(
            "https://ccmixter.org/tt34699.jpeg",
            ZaloTrendMusicService.ExtractSourceArtworkUrl(
                html,
                "https://ccmixter.org/files/gabriel_shelligton/71178"));
    }

    [Fact]
    public void CcMixter_artwork_prefers_social_card_over_tiny_artist_credit_image()
    {
        const string html = """
            <html><head>
              <meta name="twitter:image" content="https://ccmixter.org/tt34699.jpeg">
            </head><body>
              <div class="box"><img src="https://ccmixter.org/content/gabriel_shelligton/profile93x94.jpg" style="float:right" />
              <table id="credit_info"><tr><td>credits</td></tr></table></div>
            </body></html>
            """;

        Assert.Equal(
            "https://ccmixter.org/tt34699.jpeg",
            ZaloTrendMusicService.ExtractSourceArtworkUrl(
                html,
                "https://ccmixter.org/files/gabriel_shelligton/71178"));
    }

    [Fact]
    public void CcMixter_artwork_uses_artist_credit_image_as_fallback_when_social_card_is_missing()
    {
        const string html = """
            <html><body>
              <div class="box"><img src="https://ccmixter.org/content/artist/profile93x94.jpg" />
              <table id="credit_info"><tr><td>credits</td></tr></table></div>
            </body></html>
            """;

        Assert.Equal(
            "https://ccmixter.org/content/artist/profile93x94.jpg",
            ZaloTrendMusicService.ExtractSourceArtworkUrl(
                html,
                "https://ccmixter.org/files/artist/123"));
    }

    [Fact]
    public void One_shot_override_parses_natural_night_command_and_preserves_custom_text()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account", "bot", "group", "message", "sender", "Tester",
            "tối nay, lời chúc: Chúc cả nhà ngủ ngon, mai chiến tiếp, tiêu đề: Kèo đêm nay, nghệ sĩ: Zyro VC, không logo",
            [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var parsed = ZaloTrendMusicService.ParseOneShotOverrideCommand(incoming);

        Assert.NotNull(parsed);
        Assert.Equal("night", parsed.Period);
        Assert.Equal("Chúc cả nhà ngủ ngon, mai chiến tiếp", parsed.GreetingText);
        Assert.Equal("Kèo đêm nay", parsed.DisplayTitle);
        Assert.Equal("Zyro VC", parsed.DisplayArtist);
        Assert.False(parsed.ShowLogo);
        Assert.False(parsed.UseQuotedArtwork);
    }

    [Fact]
    public void One_shot_override_can_disable_greeting_and_use_quoted_artwork()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account", "bot", "group", "message", "sender", "Tester",
            "sáng mai dùng ảnh này, không lời chúc",
            [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote(
                "quoted", "someone", "Member", "", "photo", null,
                "{\"thumbUrl\":\"https://photo.zalo.example/thumb.jpg\",\"oriUrl\":\"https://photo.zalo.example/image.jpg\"}"));

        var parsed = ZaloTrendMusicService.ParseOneShotOverrideCommand(incoming);

        Assert.NotNull(parsed);
        Assert.Equal("morning", parsed.Period);
        Assert.True(parsed.GreetingDisabled);
        Assert.True(parsed.UseQuotedArtwork);
        Assert.Equal(
            "https://photo.zalo.example/image.jpg",
            ZaloTrendMusicService.ExtractHttpsUrlFromAttachment(incoming.Quote!.Attachment));
    }

    [Fact]
    public void One_shot_override_accepts_field_without_comma_and_negative_logo_phrase()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account", "bot", "group", "message", "sender", "Tester",
            "nhạc tối lời chúc: ngủ ngon cả nhà, không có logo",
            [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var parsed = ZaloTrendMusicService.ParseOneShotOverrideCommand(incoming);

        Assert.NotNull(parsed);
        Assert.Equal("night", parsed.Period);
        Assert.Equal("ngủ ngon cả nhà", parsed.GreetingText);
        Assert.False(parsed.ShowLogo);
    }

    [Fact]
    public void One_shot_override_period_comes_from_command_prefix_not_greeting_body()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account", "bot", "group", "message", "sender", "Tester",
            "sáng mai lời chúc: Tối nay nhớ ngủ sớm nha",
            [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var parsed = ZaloTrendMusicService.ParseOneShotOverrideCommand(incoming);

        Assert.NotNull(parsed);
        Assert.Equal("morning", parsed.Period);
        Assert.Equal("Tối nay nhớ ngủ sớm nha", parsed.GreetingText);
    }

    [Fact]
    public void One_shot_override_does_not_treat_plain_image_words_inside_greeting_as_artwork_command()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account", "bot", "group", "message", "sender", "Tester",
            "tối nay lời chúc: cả nhà xem ảnh này vui ghê",
            [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var parsed = ZaloTrendMusicService.ParseOneShotOverrideCommand(incoming);

        Assert.NotNull(parsed);
        Assert.False(parsed.UseQuotedArtwork);
    }

    [Fact]
    public void One_shot_override_requires_bot_mention()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "account", "bot", "group", "message", "sender", "Tester",
            "tối nay không logo", [], false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Null(ZaloTrendMusicService.ParseOneShotOverrideCommand(incoming));
    }

    [Fact]
    public void Caption_has_real_track_identity_without_npc_labels()
    {
        var request = new ZaloTrendMusicRenderCompleteRequest(
            "account", "group", "connection", "2026-11-10", "key", "ccmixter-1",
            "Come Home", "Gabriel Shellington", 1, "video", "thumb");

        var caption = ZaloTrendMusicService.BuildCaption(request);

        Assert.StartsWith("🎵 Come Home — Gabriel Shellington", caption);
        Assert.DoesNotContain("NPC", caption, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PICK", caption, StringComparison.OrdinalIgnoreCase);
    }
}
