using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Posters;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class TeamPosterCollectionTests
{
    [Fact]
    public void All_ten_templates_render_valid_and_visually_distinct_pngs()
    {
        var teams = BuildTeams();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        for (var templateId = 1; templateId <= TeamPosterTemplateCatalog.Count; templateId++)
        {
            var bytes = TeamPosterRendererRegistry.Render(
                templateId,
                "CN 16/08 - KÈO TỐI",
                new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(7)),
                "Sân bóng chuyền Bình Trưng",
                teams);

            AssertPng(bytes);
            WritePreviewIfRequested(templateId, bytes);
            hashes.Add(Convert.ToHexString(SHA256.HashData(bytes)));
        }
        Assert.Equal(TeamPosterTemplateCatalog.Count, hashes.Count);
    }

    [Fact]
    public void Every_template_handles_shared_slots_long_names_and_missing_avatars()
    {
        var longCaptain = new TeamCardPlayer("Nguyễn Một Cái Tên Cực Kỳ Dài Để Test Layout", IsCaptain: true);
        var team = new TeamCardTeam(
            "ĐỘI HÌNH SIÊU DÀI KHÔNG ĐƯỢC TRÀN KHUNG",
            longCaptain.Name,
            9.25,
            [
                new TeamCardSlot(longCaptain.Name, [longCaptain], true),
                new TeamCardSlot("Shared", [new TeamCardPlayer("Người Chơi Không Có Avatar Số Một"), new TeamCardPlayer("Partner Ngoài Zalo")]),
                new TeamCardSlot("P3", [new TeamCardPlayer("Lê Văn Ba")]),
                new TeamCardSlot("P4", [new TeamCardPlayer("Phạm Thị Bốn")]),
                new TeamCardSlot("P5", [new TeamCardPlayer("Hoàng Năm")]),
                new TeamCardSlot("P6", [new TeamCardPlayer("Võ Sáu")])
            ]);

        for (var templateId = 1; templateId <= TeamPosterTemplateCatalog.Count; templateId++)
        {
            var bytes = TeamPosterRendererRegistry.Render(
                templateId,
                "GIẢI ĐẤU NỘI BỘ VOLLEY DRAFT 2026",
                DateTimeOffset.UtcNow,
                "Một địa điểm có tên rất dài để kiểm tra metadata",
                [team]);
            AssertPng(bytes);
        }
    }

    [Fact]
    public void Poster_one_is_court_index_but_remains_disabled_from_rotation()
    {
        Assert.Equal("Court Index", TeamPosterTemplateCatalog.GetDisplayName(1));
        Assert.False(TeamPosterTemplateCatalog.IsActive(1));

        var bytes = TeamPosterRendererRegistry.Render(
            1,
            "CN 16/08 - KÈO TỐI",
            new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(7)),
            "Sân bóng chuyền Bình Trưng",
            BuildTeams());

        AssertPng(bytes);
    }

    [Fact]
    public void New_deck_contains_only_active_templates_and_never_starts_with_previous_active_last()
    {
        Assert.Equal(2, TeamPosterTemplateCatalog.ActiveCount);
        Assert.Equal(new[] { 3, 4 }, TeamPosterTemplateCatalog.ActiveIds.Order());
        Assert.False(TeamPosterTemplateCatalog.IsActive(1));
        Assert.False(TeamPosterTemplateCatalog.IsActive(2));
        Assert.True(TeamPosterTemplateCatalog.IsActive(3));
        Assert.True(TeamPosterTemplateCatalog.IsActive(4));
        Assert.All(new[] { 5, 6, 7, 8, 9, 10 }, id => Assert.False(TeamPosterTemplateCatalog.IsActive(id)));

        foreach (var last in TeamPosterTemplateCatalog.ActiveIds)
        {
            var deck = TeamPosterDeckLogic.BuildShuffledDeck(last);
            Assert.Equal(TeamPosterTemplateCatalog.ActiveCount, deck.Count);
            Assert.Equal(TeamPosterTemplateCatalog.ActiveCount, deck.Distinct().Count());
            Assert.Equal(TeamPosterTemplateCatalog.ActiveIds.Order(), deck.Order());
            Assert.NotEqual(last, deck[0]);
        }

        var deckAfterDisabledPoster = TeamPosterDeckLogic.BuildShuffledDeck(1);
        Assert.Equal(TeamPosterTemplateCatalog.ActiveIds.Order(), deckAfterDisabledPoster.Order());
    }

    [Fact]
    public async Task Group_rotation_locks_session_and_uses_only_posters_three_and_four()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "MatchSessions" (
                "Id" TEXT PRIMARY KEY,
                "ZaloConnectionId" TEXT NULL,
                "ZaloGroupId" TEXT NULL
            );
            """);
        for (var index = 1; index <= 3; index++)
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"MatchSessions\" (\"Id\", \"ZaloConnectionId\", \"ZaloGroupId\") VALUES ({0}, {1}, {2})",
                $"s{index}", "conn-1", "group-1");
        }

        var firstCycle = new List<TeamPosterAssignment>();
        for (var index = 1; index <= TeamPosterTemplateCatalog.ActiveCount; index++)
            firstCycle.Add(await TeamPosterRotationStore.EnsureAssignedAsync(db, $"s{index}"));

        Assert.Equal(TeamPosterTemplateCatalog.ActiveCount, firstCycle.Select(item => item.TemplateId).Distinct().Count());
        Assert.All(firstCycle, item => Assert.True(TeamPosterTemplateCatalog.IsActive(item.TemplateId)));
        Assert.All(firstCycle, item => Assert.Equal(1, item.CycleNumber));

        var original = firstCycle[0];
        var repeated = await TeamPosterRotationStore.EnsureAssignedAsync(db, "s1");
        Assert.Equal(original.TemplateId, repeated.TemplateId);
        Assert.Equal(original.AssignedAt, repeated.AssignedAt);

        var nextCycle = await TeamPosterRotationStore.EnsureAssignedAsync(db, "s3");
        Assert.Equal(2, nextCycle.CycleNumber);
        Assert.NotEqual(firstCycle[^1].TemplateId, nextCycle.TemplateId);
        Assert.True(TeamPosterTemplateCatalog.IsActive(nextCycle.TemplateId));
    }

    [Fact]
    public async Task Standalone_session_claims_only_an_active_poster()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "MatchSessions" (
                "Id" TEXT PRIMARY KEY,
                "ZaloConnectionId" TEXT NULL,
                "ZaloGroupId" TEXT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"MatchSessions\" (\"Id\", \"ZaloConnectionId\", \"ZaloGroupId\") VALUES ({0}, NULL, NULL)",
            "standalone-1");

        var assignment = await TeamPosterRotationStore.EnsureAssignedAsync(db, "standalone-1");

        Assert.True(TeamPosterTemplateCatalog.IsActive(assignment.TemplateId));
        Assert.Contains(assignment.TemplateId, TeamPosterTemplateCatalog.ActiveIds);
    }

    private static IReadOnlyList<TeamCardTeam> BuildTeams() =>
    [
        BuildTeam("TEAM ALPHA", "Nguyễn Hoàng Long", 7.8, 1),
        BuildTeam("TEAM BRAVO", "To An", 8.1, 2),
        BuildTeam("TEAM CHARLIE", "Trần Quốc Việt", 7.9, 3)
    ];

    private static TeamCardTeam BuildTeam(string name, string captainName, double score, int seed)
    {
        var slots = new List<TeamCardSlot>();
        for (var index = 0; index < 6; index++)
        {
            var playerName = index == 0 ? captainName : $"Player {seed}-{index + 1}";
            var player = new TeamCardPlayer(playerName, IsCaptain: index == 0);
            slots.Add(new TeamCardSlot(playerName, [player], index == 0));
        }
        return new TeamCardTeam(name, captainName, score, slots);
    }

    private static void WritePreviewIfRequested(int templateId, byte[] bytes)
    {
        var directory = Environment.GetEnvironmentVariable("TEAM_POSTER_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, $"poster-{templateId:00}.png"), bytes);
    }

    private static void AssertPng(byte[] bytes)
    {
        Assert.True(bytes.Length > 1024);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(TeamPosterRendererRegistry.Width, bitmap.Width);
        Assert.Equal(TeamPosterRendererRegistry.Height, bitmap.Height);
    }
}
