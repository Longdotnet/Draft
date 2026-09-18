using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class MemberPassDraftTransitionRaceFuzzTests
{
    [Fact]
    public async Task Ambient_member_pass_and_manual_draft_transition_can_coexist_without_corrupting_draft_state()
    {
        for (var seed = 1; seed <= 96; seed += 1)
        {
            var connectionString = $"Data Source=member-pass-draft-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            var seeded = await SeedReadySessionAsync(options, seed);

            // Bootstrap the durable pass-slot schema before the race so this target
            // attacks product state ordering, not provider-specific schema creation.
            await using (var bootstrap = new VolleyDraftDbContext(options))
            {
                _ = await new ZaloOpenSlotOfferStore(bootstrap)
                    .ListClaimableAsync(seeded.ConnectionId, seeded.GroupId, "bootstrap");
            }

            await using var memberDb = new VolleyDraftDbContext(options);
            await using var draftDb = new VolleyDraftDbContext(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var memberDelay = seed % 4;
            var draftDelay = (seed / 4) % 4;
            var memberTask = RunMemberPassAsync(memberDb, seeded, start.Task, memberDelay, seed);
            var draftTask = RunDraftAsync(draftDb, seeded, start.Task, draftDelay);

            start.SetResult();
            var member = await memberTask;
            var draft = await draftTask;

            Assert.Null(member.Exception);
            Assert.Null(draft.Exception);

            await AssertAuthoritativeStateAsync(
                options,
                seeded,
                seed,
                memberDelay,
                draftDelay,
                member.ReplyKind,
                draft);
        }
    }

    [Fact]
    public async Task Race_then_fresh_context_retries_preserve_one_draft_while_pass_can_remain_active()
    {
        // Model a lost/uncertain client response followed by retries after a process restart.
        // Replaying a member pass must not create duplicate offers, while draft remains allowed
        // with an active offer and a repeated draft start must not create another draft round.
        for (var seed = 1; seed <= 64; seed += 1)
        {
            var connectionString = $"Data Source=member-pass-draft-retry-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var seeded = await SeedReadySessionAsync(options, 20_000 + seed);

            await using (var bootstrap = new VolleyDraftDbContext(options))
            {
                _ = await new ZaloOpenSlotOfferStore(bootstrap)
                    .ListClaimableAsync(seeded.ConnectionId, seeded.GroupId, "bootstrap");
            }

            await using var memberDb = new VolleyDraftDbContext(options);
            await using var draftDb = new VolleyDraftDbContext(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var memberDelay = seed % 5;
            var draftDelay = (seed * 3) % 5;
            var memberTask = RunMemberPassAsync(memberDb, seeded, start.Task, memberDelay, 20_000 + seed);
            var draftTask = RunDraftAsync(draftDb, seeded, start.Task, draftDelay);

            start.SetResult();
            var firstMember = await memberTask;
            var firstDraft = await draftTask;
            Assert.Null(firstMember.Exception);
            Assert.Null(firstDraft.Exception);

            await using var retryMemberDb = new VolleyDraftDbContext(options);
            await using var retryDraftDb = new VolleyDraftDbContext(options);
            MemberOutcome retryMember;
            DraftOutcome retryDraft;

            if (seed % 2 == 0)
            {
                retryMember = await RunMemberPassAsync(
                    retryMemberDb,
                    seeded,
                    Task.CompletedTask,
                    delayMilliseconds: 0,
                    seed: 30_000 + seed);
                retryDraft = await RunDraftAsync(
                    retryDraftDb,
                    seeded,
                    Task.CompletedTask,
                    delayMilliseconds: 0);
            }
            else
            {
                retryDraft = await RunDraftAsync(
                    retryDraftDb,
                    seeded,
                    Task.CompletedTask,
                    delayMilliseconds: 0);
                retryMember = await RunMemberPassAsync(
                    retryMemberDb,
                    seeded,
                    Task.CompletedTask,
                    delayMilliseconds: 0,
                    seed: 30_000 + seed);
            }

            Assert.Null(retryMember.Exception);
            Assert.Null(retryDraft.Exception);

            await using var verifier = new VolleyDraftDbContext(options);
            var session = await verifier.MatchSessions.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.SessionId);
            var activeRisk = await new ZaloOpenSlotRiskCounter(verifier)
                .CountActiveForSessionAsync(seeded.ConnectionId, seeded.GroupId, seeded.SessionId);
            var roundCount = await verifier.DraftRounds.AsNoTracking()
                .CountAsync(item => item.SessionId == seeded.SessionId);

            Assert.InRange(activeRisk, 0, 1);
            Assert.True(
                firstDraft.IsSuccess,
                $"seed={seed}; initial draft was blocked by pass flow: status={firstDraft.StatusCode}; activeRisk={activeRisk}");
            Assert.Equal(SessionStatus.Drafting, session.Status);
            Assert.Equal(1, roundCount);
            Assert.False(retryDraft.IsSuccess);
        }
    }

    private static async Task AssertAuthoritativeStateAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        SeededSession seeded,
        int seed,
        int memberDelay,
        int draftDelay,
        string? memberReplyKind,
        DraftOutcome draft)
    {
        await using var verifier = new VolleyDraftDbContext(options);
        var session = await verifier.MatchSessions.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SessionId);
        var activeRisk = await new ZaloOpenSlotRiskCounter(verifier)
            .CountActiveForSessionAsync(seeded.ConnectionId, seeded.GroupId, seeded.SessionId);
        var roundCount = await verifier.DraftRounds.AsNoTracking()
            .CountAsync(item => item.SessionId == seeded.SessionId);

        Assert.True(
            draft.IsSuccess,
            $"seed={seed}; fingerprint=cross-feature:active-pass-must-not-block-draft; " +
            $"memberDelay={memberDelay}; draftDelay={draftDelay}; memberReply={memberReplyKind}; " +
            $"draftStatus={draft.StatusCode}; activeRisk={activeRisk}; rounds={roundCount}");
        Assert.InRange(activeRisk, 0, 1);
        Assert.Equal(SessionStatus.Drafting, session.Status);
        Assert.Equal(1, roundCount);
    }

    private static async Task<MemberOutcome> RunMemberPassAsync(
        VolleyDraftDbContext db,
        SeededSession seeded,
        Task start,
        int delayMilliseconds,
        int seed)
    {
        try
        {
            await start;
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds);

            var incoming = new ZaloIncomingMessageEvent(
                accountId: "bot-account",
                botId: "bot-account",
                groupId: seeded.GroupId,
                messageId: $"member-pass-{seed}",
                senderId: seeded.OwnerZaloUserId,
                senderName: "Đặng Thế Nguyễn",
                content: "tui pass slot T6 nha",
                mentions: [],
                mentionedBot: false,
                sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var reply = await new ZaloMemberAssistService(db)
                .TryBuildAsync(seeded.ConnectionId, seeded.GroupId, incoming);
            return new MemberOutcome(reply?.Kind.ToString(), null);
        }
        catch (Exception exception)
        {
            return new MemberOutcome(null, exception);
        }
    }

    private static async Task<DraftOutcome> RunDraftAsync(
        VolleyDraftDbContext db,
        SeededSession seeded,
        Task start,
        int delayMilliseconds)
    {
        try
        {
            await start;
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds);
            var result = await new SessionDraftService(db)
                .StartDraftAsync(seeded.AdminId, seeded.SessionId);
            return new DraftOutcome(result.IsSuccess, result.StatusCode, null);
        }
        catch (Exception exception)
        {
            return new DraftOutcome(false, 500, exception);
        }
    }

    private static async Task<SeededSession> SeedReadySessionAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        int seed)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var adminId = $"fuzz-member-pass-draft-admin-{seed}";
        var connectionId = $"fuzz-member-pass-draft-conn-{seed}";
        const string groupId = "fuzz-member-pass-draft-group";
        const string ownerZaloUserId = "user-nguyen";
        db.Users.Add(new User
        {
            Id = adminId,
            DisplayName = "Fuzz Member Pass Draft Admin",
            Email = $"fuzz-member-pass-draft-{seed}@example.test",
            PasswordHash = "test"
        });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = connectionId,
            AdminUserId = adminId,
            AccountZaloId = "bot-account",
            DisplayName = "Npc",
            EncryptedCredentials = "test"
        });
        await db.SaveChangesAsync();

        var service = new SessionDraftService(db);
        var created = await service.CreateSessionAsync(adminId, new CreateSessionRequest("T6", 3, 2));
        Assert.True(created.IsSuccess, created.Error);
        var sessionId = created.Value!.Id;
        var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId);
        session.ZaloConnectionId = connectionId;
        session.ZaloGroupId = groupId;
        session.BotEnabled = true;
        session.StartTime = DateTimeOffset.UtcNow.AddDays(1);
        await db.SaveChangesAsync();

        var playerIds = new List<string>();
        for (var index = 1; index <= 6; index += 1)
        {
            var added = await service.AddPlayerAsync(
                adminId,
                sessionId,
                new AddPlayerRequest(
                    index == 1 ? "Đặng Thế Nguyễn" : $"P{index}",
                    PlayerRole.New,
                    PlayerLevel.New,
                    PlayerGender.Male));
            Assert.True(added.IsSuccess, added.Error);
            playerIds.Add(added.Value!.Id);
        }

        var profile = new PlayerProfile
        {
            Id = $"fuzz-member-pass-profile-{seed}",
            ZaloUserId = ownerZaloUserId,
            DisplayName = "Đặng Thế Nguyễn"
        };
        db.PlayerProfiles.Add(profile);
        var owner = await db.SessionPlayers.SingleAsync(item => item.Id == playerIds[0]);
        owner.PlayerProfileId = profile.Id;
        owner.PlayerProfile = profile;
        await db.SaveChangesAsync();

        var captains = await service.SetManualCaptainsAsync(
            adminId,
            sessionId,
            new ManualCaptainsRequest(playerIds.Take(3).ToList()));
        Assert.True(captains.IsSuccess, captains.Error);
        db.ChangeTracker.Clear();

        return new SeededSession(adminId, connectionId, groupId, sessionId, ownerZaloUserId);
    }

    private sealed record SeededSession(
        string AdminId,
        string ConnectionId,
        string GroupId,
        string SessionId,
        string OwnerZaloUserId);

    private sealed record MemberOutcome(string? ReplyKind, Exception? Exception);
    private sealed record DraftOutcome(bool IsSuccess, int StatusCode, Exception? Exception);
}
