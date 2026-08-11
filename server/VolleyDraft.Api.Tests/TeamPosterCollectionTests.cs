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
    public void Redesigned_posters_one_two_and_three_are_active_with_poster_four()
    {
        Assert.Equal("Court Index", TeamPosterTemplateCatalog.GetDisplayName(1));
        Assert.Equal("Hall of Champions", TeamPosterTemplateCatalog.GetDisplayName(2));
        Assert.Equal("Orbit League", TeamPosterTemplateCatalog.GetDisplayName(3));

        Assert.True(TeamPosterTemplateCatalog.IsActive(1));
        Assert.True(TeamPosterTemplateCatalog.IsActive(2));
        Assert.True(TeamPosterTemplateCatalog.IsActive(3));
        Assert.True(TeamPosterTemplateCatalog.IsActive(4));

        foreach (var templateId in new[] { 1, 2, 3 })
        {
            var bytes = TeamPosterRendererRegistry.Render(
                templateId,
                "CN 16/08 - KÈO TỐI",
                new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(7)),
                "Sân bóng chuyền Bình Trưng",
                BuildTeams());
            AssertPng(bytes);
        }
    }

    [Fact]
    public void New_deck_contains_only_the_four_active_redesigned_posters_and_avoids_immediate_repeat()
    {
        Assert.Equal(4, TeamPosterTemplateCatalog.ActiveCount);
        Assert.Equal(new[] { 1, 2, 3, 4 }, TeamPosterTemplateCatalog.ActiveIds.Order());
        Assert.All(new[] { 1, 2, 3, 4 }, id => Assert.True(TeamPosterTemplateCatalog.IsActive(id)));
        Assert.All(new[] { 5, 6, 7, 8, 9, 10 }, id => Assert.False(TeamPosterTemplateCatalog.IsActive(id)));

        foreach (var last in TeamPosterTemplateCatalog.ActiveIds)
        {
            var deck = TeamPosterDeckLogic.BuildShuffledDeck(last);
            Assert.Equal(TeamPosterTemplateCatalog.ActiveCount, deck.Count);
            Assert.Equal(TeamPosterTemplateCatalog.ActiveCount, deck.Distinct().Count());
            Assert.Equal(TeamPosterTemplateCatalog.ActiveIds.Order(), deck.Order());
            Assert.NotEqual(last, deck[0]);
        }

        var deckAfterDisabledPoster = TeamPosterDeckLogic.BuildShuffledDeck(9);
        Assert.Equal(TeamPosterTemplateCatalog.ActiveIds.Order(), deckAfterDisabledPoster.Order());
    }

    [Fact]
    public void Legacy_two_poster_remaining_deck_is_still_valid_until_the_next_four_poster_cycle()
    {
        var remaining = TeamPosterDeckLogic.NormalizeRemainingDeck("[3,4,9]");
        Assert.Equal(new[] { 3, 4 }, remaining);

        var freshDeck = TeamPosterDeckLogic.BuildShuffledDeck(remaining[^1]);
        Assert.Equal(new[] { 1, 2, 3, 4 }, freshDeck.Order());
        Assert.NotEqual(remaining[^1], freshDeck[0]);
    }

    [Fact]
    public async Task Group_rotation_locks_session_and_uses_all_four_active_posters_before_repeat()
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
        for (var index = 1; index <= TeamPosterTemplateCatalog.ActiveCount + 1; index++)
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"MatchSessions\" (\"Id\", \"ZaloConnectionId\", \"ZaloGroupId\") VALUES ({0}, {1}, {2})",
                $"s{index}", "conn-1", "group-1");
        }

        var firstCycle = new List<TeamPosterAssignment>();
        for (var index = 1; index <= TeamPosterTemplateCatalog.ActiveCount; index++)
            firstCycle.Add(await TeamPosterRotationStore.EnsureAssignedAsync(db, $"s{index}"));

        Assert.Equal(TeamPosterTemplateCatalog.ActiveCount, firstCycle.Select(item => item.TemplateId).Distinct().Count());
        Assert.Equal(TeamPosterTemplateCatalog.ActiveIds.Order(), firstCycle.Select(item => item.TemplateId).Order());
        Assert.All(firstCycle, item => Assert.True(TeamPosterTemplateCatalog.IsActive(item.TemplateId)));
        Assert.All(firstCycle, item => Assert.Equal(1, item.CycleNumber));

        var original = firstCycle[0];
        var repeated = await TeamPosterRotationStore.EnsureAssignedAsync(db, "s1");
        Assert.Equal(original.TemplateId, repeated.TemplateId);
        Assert.Equal(original.AssignedAt, repeated.AssignedAt);

        var nextCycle = await TeamPosterRotationStore.EnsureAssignedAsync(db, $"s{TeamPosterTemplateCatalog.ActiveCount + 1}");
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
