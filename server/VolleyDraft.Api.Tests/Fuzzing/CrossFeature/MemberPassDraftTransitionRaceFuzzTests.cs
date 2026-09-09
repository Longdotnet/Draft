using Microsoft.AspNetCore.Http;
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
    public async Task Ambient_member_pass_and_manual_draft_transition_never_leave_drafting_with_active_pass_risk()
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

            await using var verifier = new VolleyDraftDbContext(options);
            var session = await verifier.MatchSessions.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.SessionId);
            var activeRisk = await new ZaloOpenSlotRiskCounter(verifier)
                .CountActiveForSessionAsync(seeded.ConnectionId, seeded.GroupId, seeded.SessionId);
            var roundCount = await verifier.DraftRounds.AsNoTracking()
                .CountAsync(item => item.SessionId == seeded.SessionId);

            Assert.False(
                session.Status == SessionStatus.Drafting && activeRisk > 0,
                $"seed={seed}; fingerprint=cross-feature:ambient-pass-opened-across-draft-transition; " +
                $"memberDelay={memberDelay}; draftDelay={draftDelay}; memberReply={member.ReplyKind}; " +
                $"draftStatus={draft.StatusCode}; activeRisk={activeRisk}; rounds={roundCount}");

            if (activeRisk > 0)
            {
                Assert.False(draft.IsSuccess);
                Assert.Equal(StatusCodes.Status409Conflict, draft.StatusCode);
                Assert.Equal(SessionStatus.CaptainSelection, session.Status);
                Assert.Equal(0, roundCount);
            }

            if (draft.IsSuccess)
            {
                Assert.Equal(SessionStatus.Drafting, session.Status);
                Assert.Equal(0, activeRisk);
                Assert.Equal(1, roundCount);
            }
        }
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
