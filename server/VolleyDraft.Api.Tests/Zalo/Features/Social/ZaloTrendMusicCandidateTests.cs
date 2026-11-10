using VolleyDraft.Api.Services;
using VolleyDraft.Api.Contracts;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Social;

public sealed class ZaloTrendMusicCandidateTests
{
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
}
