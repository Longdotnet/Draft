using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotMarketplaceRecoveryTests
{
    [Fact]
    public async Task Rescue_does_not_advance_nudge_when_bridge_did_not_send()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            now.AddHours(2), now.AddMinutes(-1));

        var handler = new RecordingHandler(sent: false);
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.NudgedCount);
        Assert.Equal(1, handler.SendCount);
        var offer = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(0, offer.NudgeCount);
        Assert.Null(offer.LastNudgeAt);
        Assert.True(offer.NextNudgeAt > now);
    }

    [Fact]
    public async Task Rescue_suppresses_nudges_but_keeps_offer_claimable_when_owner_is_already_unvoted()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            now.AddHours(2), now.AddMinutes(-1));

        var owner = await fixture.Db.SessionPlayers.SingleAsync(item => item.Id == "owner-player");
        owner.IsPresent = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var handler = new RecordingHandler(sent: true);
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now);

        Assert.Equal(0, result.ClosedCount);
        Assert.Equal(0, result.NudgedCount);
        Assert.Equal(0, handler.SendCount);
        var active = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, active.Status);
        Assert.Equal(0, active.NudgeCount);
        Assert.Null(active.NextNudgeAt);
        Assert.Empty(await store.ListDueRescueAsync(now.AddMinutes(1)));
        Assert.True(await store.TryClaimAsync(
            active,
            "someone",
            "Người nhận",
            "claim-after-owner-unvoted",
            now.AddMinutes(20)));
        Assert.NotNull(await store.LoadPendingClaimAsync("conn", "g1", "someone"));
    }

    [Fact]
    public async Task Rescue_uses_live_selected_poll_option_when_session_roster_is_stale()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var now = DateTimeOffset.UtcNow;
        await AddPollSourceAsync(fixture, now);

        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T5", "m1",
            now.AddHours(2), now.AddMinutes(-1));

        // The persisted roster still has the owner, but the live T5 option no longer does.
        var handler = new RecordingHandler(
            sent: true,
            pollOptions:
            [
                new BridgePollOption("option-t5", "Thứ 5", 1, ["claimant"]),
                new BridgePollOption("option-t6", "Thứ 6", 1, ["owner"])
            ],
            uniqueVoteCount: 2);
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now);

        Assert.Equal(1, handler.PollFetchCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, result.NudgedCount);
        Assert.Equal(0, result.FailedCount);
        var active = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, active.Status);
        Assert.Equal(0, active.NudgeCount);
        Assert.Null(active.NextNudgeAt);
    }

    [Fact]
    public async Task Rescue_sends_nudge_when_owner_still_votes_in_the_selected_poll_option()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var now = DateTimeOffset.UtcNow;
        await AddPollSourceAsync(fixture, now);

        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T5", "m1",
            now.AddHours(2), now.AddMinutes(-1));

        var handler = new RecordingHandler(
            sent: true,
            pollOptions:
            [
                new BridgePollOption("option-t5", "Thứ 5", 2, ["owner", "claimant"]),
                new BridgePollOption("option-t6", "Thứ 6", 0, [])
            ],
            uniqueVoteCount: 2);
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now);

        Assert.Equal(1, handler.PollFetchCount);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, result.NudgedCount);
        var active = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(1, active.NudgeCount);
    }

    [Fact]
    public async Task Rescue_retries_without_sending_when_live_poll_cannot_be_verified()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var now = DateTimeOffset.UtcNow;
        await AddPollSourceAsync(fixture, now);

        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T5", "m1",
            now.AddHours(2), now.AddMinutes(-1));

        var handler = new RecordingHandler(sent: true, pollStatus: HttpStatusCode.BadGateway);
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now);

        Assert.Equal(1, handler.PollFetchCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, result.NudgedCount);
        Assert.Equal(1, result.FailedCount);
        var active = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, active.Status);
        Assert.True(active.NextNudgeAt > now);
    }

    [Fact]
    public async Task Rescue_releases_timed_out_claim_instead_of_completing_when_owner_unvoted()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var baseNow = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            baseNow.AddHours(2), baseNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(
            offer,
            "claimant",
            "Vivian",
            "m2",
            baseNow.AddMinutes(1)));

        var owner = await fixture.Db.SessionPlayers.SingleAsync(item => item.Id == "owner-player");
        owner.IsPresent = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var handler = new RecordingHandler(sent: true);
        var result = await CreateService(fixture.Db, handler)
            .RunDueAsync(baseNow.AddMinutes(2));

        Assert.Equal(0, result.ClosedCount);
        Assert.Equal(1, result.ClaimReleasedCount);
        Assert.Equal(0, result.NudgedCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Null(await store.LoadPendingClaimAsync("conn", "g1", "claimant"));
        var reopened = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, reopened.Status);
        Assert.Null(reopened.ClaimantZaloUserId);
        Assert.Null(reopened.NextNudgeAt);
    }

    [Fact]
    public async Task Stale_applying_reopens_when_canonical_roster_still_has_owner()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var baseNow = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            baseNow.AddHours(2), baseNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(offer, "claimant", "Vivian", "m2", baseNow.AddMinutes(10)));
        Assert.True(await store.TryBeginApplyAsync(offer.Id, "claimant"));

        var result = await CreateService(fixture.Db, new RecordingHandler(sent: true))
            .RunDueAsync(baseNow.AddMinutes(10));

        Assert.Equal(1, result.ClaimReleasedCount);
        Assert.Null(await store.LoadPendingClaimAsync("conn", "g1", "claimant"));
        var reopened = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, reopened.Status);
        Assert.Null(reopened.ClaimantZaloUserId);
    }

    [Fact]
    public async Task Stale_applying_closes_completed_when_canonical_postdraft_transfer_is_already_visible()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var baseNow = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            baseNow.AddHours(2), baseNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(offer, "claimant", "Vivian", "m2", baseNow.AddMinutes(10)));
        Assert.True(await store.TryBeginApplyAsync(offer.Id, "claimant"));

        var owner = await fixture.Db.SessionPlayers.SingleAsync(item => item.Id == "owner-player");
        owner.IsPresent = false;
        var claimantProfile = new PlayerProfile
        {
            Id = "claimant-profile",
            ZaloUserId = "claimant",
            DisplayName = "Vivian",
            Gender = PlayerGender.Female,
            DefaultRole = PlayerRole.Defense,
            DefaultLevel = PlayerLevel.Average
        };
        var claimant = new SessionPlayer
        {
            Id = "claimant-player",
            SessionId = "s1",
            PlayerProfileId = claimantProfile.Id,
            PlayerProfile = claimantProfile,
            DisplayName = "Vivian",
            IsPresent = true,
            Gender = PlayerGender.Female,
            Role = PlayerRole.Defense,
            Level = PlayerLevel.Average,
            Score = 2,
            IsCaptainEligible = false
        };
        fixture.Db.PlayerProfiles.Add(claimantProfile);
        fixture.Db.SessionPlayers.Add(claimant);
        var oldLink = await fixture.Db.DraftSlotPlayers.SingleAsync(item => item.Id == "owner-link");
        fixture.Db.DraftSlotPlayers.Remove(oldLink);
        fixture.Db.DraftSlotPlayers.Add(new DraftSlotPlayer
        {
            Id = "claimant-link",
            DraftSlotId = "owner-slot",
            SessionPlayerId = claimant.Id,
            SessionPlayer = claimant,
            RotationOrder = 1
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await CreateService(fixture.Db, new RecordingHandler(sent: true))
            .RunDueAsync(baseNow.AddMinutes(10));

        Assert.Equal(1, result.ClosedCount);
        Assert.Null(await store.LoadPendingClaimAsync("conn", "g1", "claimant"));
        Assert.Empty(await store.ListOwnedActiveAsync("conn", "g1", "owner"));
        Assert.Empty(await store.ListClaimableAsync("conn", "g1", "someone"));
    }

    [Fact]
    public async Task Stale_applying_stays_locked_when_canonical_state_is_ambiguous()
    {
        await using var fixture = await Fixture.CreateFinishedAsync();
        var baseNow = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            baseNow.AddHours(2), baseNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(offer, "claimant", "Vivian", "m2", baseNow.AddMinutes(10)));
        Assert.True(await store.TryBeginApplyAsync(offer.Id, "claimant"));

        var owner = await fixture.Db.SessionPlayers.SingleAsync(item => item.Id == "owner-player");
        owner.IsPresent = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await CreateService(fixture.Db, new RecordingHandler(sent: true))
            .RunDueAsync(baseNow.AddMinutes(10));

        Assert.True(result.SkippedCount >= 1);
        var pending = await store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal(ZaloOpenSlotOfferStatus.Applying, pending!.Status);
        Assert.Empty(await store.ListClaimableAsync("conn", "g1", "someone"));
    }

    private static ZaloOpenSlotRescueService CreateService(VolleyDraftDbContext db, RecordingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:MemberAssist:Rescue:Enabled"] = "true",
                ["ZaloBot:Ambient:MemberAssist:Rescue:MaxNudges"] = "3",
                ["ZaloBot:Ambient:MemberAssist:Rescue:GroupCooldownMinutes"] = "10",
                ["ZaloBot:Ambient:MemberAssist:Rescue:RetryMinutes"] = "10",
                ["ZaloBot:Ambient:MemberAssist:Rescue:ApplyingStaleMinutes"] = "5",
                ["Zalo:CredentialEncryptionKey"] = "open-slot-rescue-test-key"
            })
            .Build();
        var connection = db.ZaloConnections.SingleOrDefault();
        if (connection is not null)
        {
            connection.EncryptedCredentials = new ZaloCredentialProtector(configuration).Protect("{}");
            db.SaveChanges();
        }
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return new ZaloOpenSlotRescueService(
            db,
            new ZaloBridgeClient(client),
            configuration,
            NullLogger<ZaloOpenSlotRescueService>.Instance);
    }

    private static async Task AddPollSourceAsync(Fixture fixture, DateTimeOffset now)
    {
        var owner = await fixture.Db.SessionPlayers.SingleAsync(item => item.Id == "owner-player");
        owner.SourcePollId = "poll-1";
        owner.SourceOptionIdsJson = """["option-t5"]""";
        fixture.Db.PollImports.Add(new PollImport
        {
            SessionId = "s1",
            ImportedByUserId = "admin",
            ZaloGroupId = "g1",
            PollId = "poll-1",
            PollQuestion = "Poll test",
            SelectedOptionIdsJson = """["option-t5"]""",
            ImportedPlayerCount = 0,
            ImportedAt = now.AddMinutes(-20)
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
    }

    private sealed class RecordingHandler(
        bool sent,
        IReadOnlyList<BridgePollOption>? pollOptions = null,
        int uniqueVoteCount = 0,
        HttpStatusCode pollStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public int PollFetchCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.StartsWith("/v1/polls/", StringComparison.Ordinal) == true)
            {
                PollFetchCount += 1;
                if (pollStatus != HttpStatusCode.OK)
                {
                    return Task.FromResult(new HttpResponseMessage(pollStatus)
                    {
                        Content = new StringContent("""{"error":"poll unavailable"}""", Encoding.UTF8, "application/json")
                    });
                }

                var poll = new BridgePoll(
                    "poll-1",
                    "Poll test",
                    "creator",
                    pollOptions ?? [],
                    false,
                    false,
                    false,
                    false,
                    uniqueVoteCount,
                    1,
                    1,
                    0);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(poll, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        Encoding.UTF8,
                        "application/json")
                });
            }

            SendCount += 1;
            var body = sent
                ? "{\"sent\":true,\"mock\":true,\"messageId\":\"rescue-message\"}"
                : "{\"sent\":false,\"mock\":false,\"messageId\":null}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateFinishedAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = $"marketplace-recovery-{Guid.NewGuid():n}@example.test",
                PasswordHash = "x"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "x",
                Status = ZaloConnectionStatus.Connected
            };
            var ownerProfile = new PlayerProfile
            {
                Id = "owner-profile",
                ZaloUserId = "owner",
                DisplayName = "Hoàng Nguyên",
                Gender = PlayerGender.Male,
                DefaultRole = PlayerRole.Attack,
                DefaultLevel = PlayerLevel.Average
            };
            var session = new MatchSession
            {
                Id = "s1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "T6",
                Status = SessionStatus.Finished,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddHours(3),
                TeamCount = 3,
                TeamSize = 6
            };
            var owner = new SessionPlayer
            {
                Id = "owner-player",
                SessionId = session.Id,
                PlayerProfileId = ownerProfile.Id,
                PlayerProfile = ownerProfile,
                DisplayName = ownerProfile.DisplayName,
                IsPresent = true,
                Gender = PlayerGender.Male,
                Role = PlayerRole.Attack,
                Level = PlayerLevel.Average,
                Score = 2,
                IsCaptainEligible = false
            };
            var team = new Team
            {
                Id = "team-a",
                SessionId = session.Id,
                Session = session,
                Name = "Team A",
                TotalAverageScore = 2
            };
            var slot = new DraftSlot
            {
                Id = "owner-slot",
                SessionId = session.Id,
                Session = session,
                Type = DraftSlotType.Single,
                DisplayName = owner.DisplayName,
                Role = owner.Role,
                Gender = owner.Gender,
                AverageScore = owner.Score,
                AssignedTeamId = team.Id,
                AssignedTeam = team,
                IsCaptainSlot = false
            };
            slot.Players.Add(new DraftSlotPlayer
            {
                Id = "owner-link",
                DraftSlotId = slot.Id,
                DraftSlot = slot,
                SessionPlayerId = owner.Id,
                SessionPlayer = owner,
                RotationOrder = 1
            });
            session.Players.Add(owner);
            session.Teams.Add(team);
            session.DraftSlots.Add(slot);

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.Add(ownerProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
