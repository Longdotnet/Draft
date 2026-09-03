using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGuestWorldModelTaskStackTests
{
    private static readonly ZaloSemanticActionSettings Settings = new(
        Enabled: true,
        MinimumConfidence: .85,
        MaxContextMessages: 12,
        MaxUserCallsPerMinute: 4,
        MaxGroupCallsPerMinute: 20);

    [Fact]
    public async Task TaskStack_AllowsSameSenderToKeepTwoSessionTasksActive()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloConversationTaskStackStore(db);
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            "guest:s-t7:PendingAddQuantity", "g1", "u1", "RecruitmentGuest", "PendingAddQuantity",
            "s-t7", "T7", "{\"sessionId\":\"s-t7\"}", "[\"quantity\"]", "[]",
            "m1", "m1", now.AddMinutes(15));
        await store.UpsertAsync(
            "guest:s-cn:GuestProfile", "g1", "u1", "RecruitmentGuest", "GuestProfile",
            "s-cn", "CN", "{\"sessionId\":\"s-cn\"}", "[\"gender:#1\"]", "[]",
            "m2", "m2", now.AddMinutes(60));

        var tasks = await store.LoadActiveAsync("g1", "u1", "RecruitmentGuest");

        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, item => item.SessionId == "s-t7" && item.Intent == "PendingAddQuantity");
        Assert.Contains(tasks, item => item.SessionId == "s-cn" && item.Intent == "GuestProfile");
        Assert.Equal("s-t7", ZaloConversationTaskStackStore.SelectForMessage(tasks, "T7 tui chốt 2 bạn")?.SessionId);
        Assert.Equal("s-cn", ZaloConversationTaskStackStore.SelectForMessage(tasks, "CN bạn đó nữ nha")?.SessionId);
    }

    [Fact]
    public async Task TaskStack_UpsertSameTaskVersionsInsteadOfCreatingDuplicate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloConversationTaskStackStore(db);

        var first = await store.UpsertAsync(
            "guest:s1:GuestProfile", "g1", "u1", "RecruitmentGuest", "GuestProfile", "s1", "T7",
            "{}", "[\"gender:#1\"]", "[]", "m1", "m1", DateTimeOffset.UtcNow.AddMinutes(30));
        var second = await store.UpsertAsync(
            "guest:s1:GuestProfile", "g1", "u1", "RecruitmentGuest", "GuestProfile", "s1", "T7",
            "{}", "[\"gender:#2\"]", "[]", "m1", "m2", DateTimeOffset.UtcNow.AddMinutes(30));
        var tasks = await store.LoadActiveAsync("g1", "u1", "RecruitmentGuest");

        Assert.Single(tasks);
        Assert.Equal(first.Version + 1, second.Version);
        Assert.Equal("m2", tasks[0].LastMessageId);
        Assert.Contains("#2", tasks[0].MissingArgumentsJson);
    }

    [Fact]
    public void EntityResolver_ResolvesNaturalOrdinalsToGroundedReservations()
    {
        var snapshot = Snapshot([
            Guest("r1", 1, "Minh", PlayerGender.Male),
            Guest("r2", 2, "Huy", PlayerGender.Female)
        ]);

        var first = ZaloSemanticGuestEntityResolver.Resolve(Reference("bạn đầu"), snapshot);
        var second = ZaloSemanticGuestEntityResolver.Resolve(Reference("bạn thứ hai"), snapshot);

        Assert.Equal(ZaloSemanticGuestEntityResolutionStatus.Resolved, first.Status);
        Assert.Equal("r1", first.Guest?.ReservationId);
        Assert.Equal(ZaloSemanticGuestEntityResolutionStatus.Resolved, second.Status);
        Assert.Equal("r2", second.Guest?.ReservationId);
    }

    [Fact]
    public void EntityResolver_RecentPronounResolvesOnlyWhenUnique()
    {
        var unique = Snapshot([Guest("r1", 1, "Minh", PlayerGender.Male)], ZaloSemanticGuestAnchorKind.RecentGuestMutation);
        var ambiguous = Snapshot([
            Guest("r1", 1, "Minh", PlayerGender.Male),
            Guest("r2", 2, "Huy", PlayerGender.Female)
        ], ZaloSemanticGuestAnchorKind.RecentGuestMutation);

        Assert.Equal("r1", ZaloSemanticGuestEntityResolver.Resolve(Reference("nó"), unique).Guest?.ReservationId);
        Assert.Equal(
            ZaloSemanticGuestEntityResolutionStatus.Ambiguous,
            ZaloSemanticGuestEntityResolver.Resolve(Reference("nó"), ambiguous).Status);
    }

    [Fact]
    public void EntityResolver_FabricatedReservationIdNeverFallsBackToOnlyGuest()
    {
        var snapshot = Snapshot([Guest("real", 1, "Minh", PlayerGender.Male)]);
        var item = new ZaloSemanticGuestPlanItem(
            "Minh", "fake", 1, null, 0, null, 0, null, 0, null, 0, .99);

        var result = ZaloSemanticGuestEntityResolver.Resolve(item, snapshot);

        Assert.Equal(ZaloSemanticGuestEntityResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Guest);
    }

    [Fact]
    public void Validator_BindsBanThuHaiToTheSecondGroundedGuest()
    {
        var snapshot = Snapshot([
            Guest("r1", 1, "Minh", PlayerGender.Male),
            Guest("r2", 2, "Huy", null)
        ], ZaloSemanticGuestAnchorKind.ActiveGuestConversation);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.UpdateGuestProfiles,
            .99,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem(
                "bạn thứ hai", null, null, null, 0, PlayerGender.Female, .99,
                null, 0, null, 0, .99)],
            false,
            string.Empty,
            "natural ordinal profile update");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.True(validation.Accepted);
        Assert.Single(validation.Items);
        Assert.Equal("r2", validation.Items[0].ReservationId);
        Assert.Equal(PlayerGender.Female, validation.Items[0].Gender);
    }

    [Fact]
    public void Validator_DuplicateNameStaysAmbiguousInsteadOfGuessing()
    {
        var snapshot = Snapshot([
            Guest("r1", 1, "Minh", PlayerGender.Male),
            Guest("r2", 2, "Minh", PlayerGender.Female)
        ], ZaloSemanticGuestAnchorKind.ActiveGuestConversation);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.CancelGuests,
            .99,
            1,
            .99,
            [Reference("Minh")],
            false,
            string.Empty,
            "cancel Minh");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_cancel_target_ambiguous", validation.Reason);
    }

    private static ZaloSemanticGuestPlanItem Reference(string reference) => new(
        reference, null, null, null, 0, null, 0, null, 0, null, 0, .99);

    private static ZaloSemanticGuestGroundingGuest Guest(
        string id,
        int sequence,
        string name,
        PlayerGender? gender) => new(
            id, sequence, name, gender, PlayerLevel.New, null, ZaloGuestReservationStatus.Active.ToString());

    private static ZaloSemanticGuestGroundingSnapshot Snapshot(
        IReadOnlyList<ZaloSemanticGuestGroundingGuest> guests,
        ZaloSemanticGuestAnchorKind anchor = ZaloSemanticGuestAnchorKind.ActiveGuestConversation)
    {
        var now = DateTimeOffset.UtcNow;
        return new ZaloSemanticGuestGroundingSnapshot(
            "s1", "T7", now.AddHours(1), 17, 18, true,
            "u1", "Long", anchor, "recruit-1", guests, [], now,
            now.ToOffset(TimeSpan.FromHours(7)));
    }
}
