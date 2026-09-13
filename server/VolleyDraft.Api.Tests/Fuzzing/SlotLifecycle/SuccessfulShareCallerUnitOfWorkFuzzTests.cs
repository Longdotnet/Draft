using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class SuccessfulShareCallerUnitOfWorkFuzzTests
{
    private const string Fingerprint = "slot-lifecycle:successful-share-must-not-commit-caller-uow";

    [Fact]
    public async Task Successful_share_must_not_persist_unrelated_pending_added_entity()
    {
        await using var state = new ShareState();
        var pendingUserId = $"pending-success-share-{Guid.NewGuid():N}";
        var pendingUser = new User
        {
            Id = pendingUserId,
            DisplayName = "Pending caller-owned user",
            Email = $"{pendingUserId}@example.test",
            PasswordHash = "test"
        };
        state.Db.Users.Add(pendingUser);

        var shared = await ShareReturningAsync(state);
        Assert.True(shared.IsSuccess, shared.Error);

        await using (var verifier = state.CreateVerifier())
        {
            var callerStateWasCommitted = await verifier.Users.AsNoTracking()
                .AnyAsync(user => user.Id == pendingUserId);
            var returningIsPresent = await verifier.SessionPlayers.AsNoTracking()
                .Where(player => player.Id == state.ReturningId)
                .Select(player => player.IsPresent)
                .SingleAsync();

            Assert.True(returningIsPresent);
            Assert.False(
                callerStateWasCommitted,
                $"{Fingerprint}: successful share persisted caller-owned Added state before the caller saved it.");
        }

        Assert.Equal(EntityState.Added, state.Db.Entry(pendingUser).State);
        await state.Db.SaveChangesAsync();

        await using var afterCallerSave = state.CreateVerifier();
        Assert.True(await afterCallerSave.Users.AsNoTracking().AnyAsync(user => user.Id == pendingUserId));
    }

    [Fact]
    public async Task Successful_share_must_not_persist_unrelated_pending_modified_entity()
    {
        await using var state = new ShareState();
        const string pendingName = "Caller-owned pending session rename";
        var session = await state.Db.MatchSessions.SingleAsync(item => item.Id == state.SessionId);
        var durableName = session.Name;
        session.Name = pendingName;

        var shared = await ShareReturningAsync(state);
        Assert.True(shared.IsSuccess, shared.Error);

        await using (var verifier = state.CreateVerifier())
        {
            var persistedName = await verifier.MatchSessions.AsNoTracking()
                .Where(item => item.Id == state.SessionId)
                .Select(item => item.Name)
                .SingleAsync();
            Assert.Equal(durableName, persistedName);
        }

        Assert.Equal(pendingName, session.Name);
        Assert.True(state.Db.Entry(session).Property(item => item.Name).IsModified);
        await state.Db.SaveChangesAsync();

        await using var afterCallerSave = state.CreateVerifier();
        var callerSavedName = await afterCallerSave.MatchSessions.AsNoTracking()
            .Where(item => item.Id == state.SessionId)
            .Select(item => item.Name)
            .SingleAsync();
        Assert.Equal(pendingName, callerSavedName);
    }

    [Fact]
    public async Task Successful_share_must_not_persist_unrelated_pending_deleted_entity()
    {
        await using var state = new ShareState();
        var victimId = $"pending-delete-success-share-{Guid.NewGuid():N}";
        var victim = new User
        {
            Id = victimId,
            DisplayName = "Pending caller-owned delete",
            Email = $"{victimId}@example.test",
            PasswordHash = "test"
        };
        state.Db.Users.Add(victim);
        await state.Db.SaveChangesAsync();
        state.Db.Users.Remove(victim);

        var shared = await ShareReturningAsync(state);
        Assert.True(shared.IsSuccess, shared.Error);

        await using (var verifier = state.CreateVerifier())
        {
            var callerDeleteWasCommitted = !await verifier.Users.AsNoTracking()
                .AnyAsync(user => user.Id == victimId);
            var returningIsPresent = await verifier.SessionPlayers.AsNoTracking()
                .Where(player => player.Id == state.ReturningId)
                .Select(player => player.IsPresent)
                .SingleAsync();

            Assert.True(returningIsPresent);
            Assert.False(
                callerDeleteWasCommitted,
                $"{Fingerprint}: successful share persisted caller-owned Deleted state before the caller saved it.");
        }

        Assert.Equal(EntityState.Deleted, state.Db.Entry(victim).State);
        await state.Db.SaveChangesAsync();

        await using var afterCallerSave = state.CreateVerifier();
        Assert.False(await afterCallerSave.Users.AsNoTracking().AnyAsync(user => user.Id == victimId));
    }

    private static Task<ServiceResult<PreDraftSharedSlotResult>> ShareReturningAsync(ShareState state)
    {
        var service = new SessionDraftService(state.Db);
        return service.SharePreDraftSlotAsync(
            state.AdminId,
            state.SessionId,
            "Anchor",
            [new ShareSlotParticipantInput("Returning")]);
    }

    private sealed class ShareState : IAsyncDisposable
    {
        private readonly string connectionString = $"Data Source=successful-share-uow-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection anchorConnection;
        private readonly DbContextOptions<VolleyDraftDbContext> options;

        public ShareState()
        {
            anchorConnection = new SqliteConnection(connectionString);
            anchorConnection.Open();
            options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            Db = new VolleyDraftDbContext(options);
            SeedAsync().GetAwaiter().GetResult();
        }

        public VolleyDraftDbContext Db { get; }
        public string AdminId { get; private set; } = string.Empty;
        public string SessionId { get; private set; } = string.Empty;
        public string ReturningId { get; private set; } = string.Empty;

        public VolleyDraftDbContext CreateVerifier() => new(options);

        private async Task SeedAsync()
        {
            await Db.Database.EnsureCreatedAsync();
            AdminId = $"fuzz-success-share-admin-{Guid.NewGuid():N}";
            Db.Users.Add(new User
            {
                Id = AdminId,
                DisplayName = "Fuzz Success Share Admin",
                Email = $"{AdminId}@example.test",
                PasswordHash = "test"
            });
            await Db.SaveChangesAsync();

            var service = new SessionDraftService(Db);
            var created = await service.CreateSessionAsync(
                AdminId,
                new CreateSessionRequest("Successful share UoW fuzz", 3, 2));
            if (!created.IsSuccess)
                throw new InvalidOperationException(created.Error);
            SessionId = created.Value!.Id;

            var anchor = await service.AddPlayerAsync(
                AdminId,
                SessionId,
                new AddPlayerRequest("Anchor", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            if (!anchor.IsSuccess)
                throw new InvalidOperationException(anchor.Error);

            var returning = await service.AddPlayerAsync(
                AdminId,
                SessionId,
                new AddPlayerRequest("Returning", PlayerRole.New, PlayerLevel.New, PlayerGender.Female));
            if (!returning.IsSuccess)
                throw new InvalidOperationException(returning.Error);
            ReturningId = returning.Value!.Id;

            var returningEntity = await Db.SessionPlayers.SingleAsync(player => player.Id == ReturningId);
            returningEntity.IsPresent = false;
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await anchorConnection.DisposeAsync();
        }
    }
}
