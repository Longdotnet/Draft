using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ShareSlotTeamPreferenceCrossFeatureFuzzTests
{
    [Fact]
    public async Task Share_rejoin_does_not_consume_or_resurrect_stale_same_team_membership()
    {
        await using var target = new SharePreferenceTarget();
        var scenario = new StatefulFuzzCase<SharePreferenceAction>(
            "share-rejoin-stale-team-preference",
            20260909,
            [
                new(SharePreferenceActionKind.PreviewShare),
                new(SharePreferenceActionKind.Restart),
                new(SharePreferenceActionKind.ApplyShare),
                new(SharePreferenceActionKind.Reconcile)
            ]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Sequence_mutations_preserve_share_truth_and_prevent_stale_preference_reactivation()
    {
        SharePreferenceAction[] seedActions =
        [
            new(SharePreferenceActionKind.PreviewShare),
            new(SharePreferenceActionKind.Restart),
            new(SharePreferenceActionKind.ApplyShare),
            new(SharePreferenceActionKind.Reconcile)
        ];

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                seedActions,
                seed,
                random => new SharePreferenceAction((SharePreferenceActionKind)random.NextInt(4)),
                operationCount: 10);
            await using var target = new SharePreferenceTarget();
            var scenario = new StatefulFuzzCase<SharePreferenceAction>(
                $"share-team-preference-cross-feature-{seed}",
                seed,
                actions);

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<SharePreferenceAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum SharePreferenceActionKind
    {
        PreviewShare,
        ApplyShare,
        Restart,
        Reconcile
    }

    internal sealed record SharePreferenceAction(SharePreferenceActionKind Kind);

    internal sealed record SharePreferenceSnapshot(
        bool ReturningPresent,
        bool SharedSlotContainsAnchorAndReturning,
        bool ReturningStillInPreference,
        string[] CorePreferencePlayerIds,
        int[] CorePreferenceRotationOrders,
        string[] ForeignPreferencePlayerIds,
        int[] ForeignPreferenceRotationOrders);

    internal sealed class SharePreferenceState : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<VolleyDraftDbContext> options;

        public SharePreferenceState()
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            Db = new VolleyDraftDbContext(options);
            Db.Database.EnsureCreated();
            SeedAsync().GetAwaiter().GetResult();
        }

        public VolleyDraftDbContext Db { get; private set; }
        public string SessionId { get; private set; } = string.Empty;
        public string ForeignSessionId { get; private set; } = string.Empty;
        public string AnchorId { get; private set; } = string.Empty;
        public string ReturningId { get; private set; } = string.Empty;
        public string CoreOneId { get; private set; } = string.Empty;
        public string CoreTwoId { get; private set; } = string.Empty;
        public string ForeignOneId { get; private set; } = string.Empty;
        public string ForeignTwoId { get; private set; } = string.Empty;
        public bool Applied { get; set; }
        public ServiceResult<ShareSlotPreview>? LastPreview { get; set; }
        public SharePreferenceSnapshot? Snapshot { get; set; }

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new VolleyDraftDbContext(options);
        }

        public async Task CaptureAsync(CancellationToken cancellationToken)
        {
            var returning = await Db.SessionPlayers
                .AsNoTracking()
                .SingleAsync(player => player.Id == ReturningId, cancellationToken);
            var sharedSlots = await Db.DraftSlots
                .AsNoTracking()
                .Include(slot => slot.Players)
                .Where(slot => slot.SessionId == SessionId && slot.Type == DraftSlotType.Shared)
                .ToListAsync(cancellationToken);
            var expectedShareIds = new[] { AnchorId, ReturningId }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var shareExists = sharedSlots.Any(slot =>
                slot.Players.Select(link => link.SessionPlayerId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(expectedShareIds));

            var targetGroups = await Db.TeamPreferenceGroups
                .AsNoTracking()
                .Include(group => group.Players.OrderBy(link => link.RotationOrder))
                .Where(group => group.SessionId == SessionId)
                .ToListAsync(cancellationToken);
            var returningStillLinked = targetGroups.Any(group =>
                group.Players.Any(link => link.SessionPlayerId == ReturningId));
            var coreGroup = targetGroups.SingleOrDefault(group =>
                group.Players.Any(link => link.SessionPlayerId == CoreOneId) &&
                group.Players.Any(link => link.SessionPlayerId == CoreTwoId));

            var foreignGroup = await Db.TeamPreferenceGroups
                .AsNoTracking()
                .Include(group => group.Players.OrderBy(link => link.RotationOrder))
                .SingleAsync(group => group.SessionId == ForeignSessionId, cancellationToken);

            Snapshot = new SharePreferenceSnapshot(
                returning.IsPresent,
                shareExists,
                returningStillLinked,
                coreGroup?.Players.OrderBy(link => link.RotationOrder).Select(link => link.SessionPlayerId).ToArray() ?? [],
                coreGroup?.Players.OrderBy(link => link.RotationOrder).Select(link => link.RotationOrder).ToArray() ?? [],
                foreignGroup.Players.OrderBy(link => link.RotationOrder).Select(link => link.SessionPlayerId).ToArray(),
                foreignGroup.Players.OrderBy(link => link.RotationOrder).Select(link => link.RotationOrder).ToArray());
        }

        private async Task SeedAsync()
        {
            var admin = new User
            {
                Id = "fuzz-share-pref-admin",
                DisplayName = "Fuzz Share Admin",
                Email = "fuzz-share-pref-admin@example.test",
                PasswordHash = "test"
            };
            Db.Users.Add(admin);
            await Db.SaveChangesAsync();

            var service = new SessionDraftService(Db);
            var session = await service.CreateSessionAsync(
                admin.Id,
                new CreateSessionRequest("T6", 3, 2));
            if (!session.IsSuccess)
                throw new InvalidOperationException(session.Error);
            SessionId = session.Value!.Id;

            AnchorId = await AddPlayerAsync(service, admin.Id, SessionId, "Anchor");
            ReturningId = await AddPlayerAsync(service, admin.Id, SessionId, "Returning");
            CoreOneId = await AddPlayerAsync(service, admin.Id, SessionId, "Core One");
            CoreTwoId = await AddPlayerAsync(service, admin.Id, SessionId, "Core Two");

            var returning = await Db.SessionPlayers.SingleAsync(player => player.Id == ReturningId);
            returning.IsPresent = false;
            AddPreferenceGroup(SessionId, ReturningId, CoreOneId, CoreTwoId);

            var foreignSession = await service.CreateSessionAsync(
                admin.Id,
                new CreateSessionRequest("CN", 3, 2));
            if (!foreignSession.IsSuccess)
                throw new InvalidOperationException(foreignSession.Error);
            ForeignSessionId = foreignSession.Value!.Id;
            ForeignOneId = await AddPlayerAsync(service, admin.Id, ForeignSessionId, "Foreign One");
            ForeignTwoId = await AddPlayerAsync(service, admin.Id, ForeignSessionId, "Foreign Two");
            AddPreferenceGroup(ForeignSessionId, ForeignOneId, ForeignTwoId);

            await Db.SaveChangesAsync();
            await CaptureAsync(CancellationToken.None);
        }

        private static async Task<string> AddPlayerAsync(
            SessionDraftService service,
            string adminId,
            string sessionId,
            string displayName)
        {
            var added = await service.AddPlayerAsync(
                adminId,
                sessionId,
                new AddPlayerRequest(
                    displayName,
                    PlayerRole.New,
                    PlayerLevel.New,
                    PlayerGender.Male));
            if (!added.IsSuccess)
                throw new InvalidOperationException(added.Error);
            return added.Value!.Id;
        }

        private void AddPreferenceGroup(string sessionId, params string[] playerIds)
        {
            var group = new TeamPreferenceGroup { SessionId = sessionId };
            for (var index = 0; index < playerIds.Length; index += 1)
            {
                group.Players.Add(new TeamPreferenceGroupPlayer
                {
                    TeamPreferenceGroupId = group.Id,
                    SessionPlayerId = playerIds[index],
                    RotationOrder = index + 1
                });
            }
            Db.TeamPreferenceGroups.Add(group);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class SharePreferenceTarget :
        IStatefulFuzzTarget<SharePreferenceState, SharePreferenceAction>,
        IAsyncDisposable
    {
        public string Name => "share-slot-team-preference-cross-feature";
        public SharePreferenceState? LastState { get; private set; }

        public SharePreferenceState CreateState(StatefulFuzzCase<SharePreferenceAction> scenario)
        {
            LastState = new SharePreferenceState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            SharePreferenceState state,
            SharePreferenceAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == SharePreferenceActionKind.Restart)
            {
                await state.RestartAsync();
                return;
            }

            if (action.Kind == SharePreferenceActionKind.Reconcile)
            {
                await new TeamPreferenceRosterReconciler(state.Db)
                    .ReconcileAsync(state.SessionId, cancellationToken);
                await state.CaptureAsync(cancellationToken);
                return;
            }

            var service = new SessionDraftService(state.Db);
            if (action.Kind == SharePreferenceActionKind.PreviewShare)
            {
                state.LastPreview = await service.PreviewShareSlotAsync(
                    "fuzz-share-pref-admin",
                    state.SessionId,
                    "Anchor",
                    [new ShareSlotParticipantInput("Returning")],
                    cancellationToken);
                await state.CaptureAsync(cancellationToken);
                return;
            }

            if (state.Applied) return;
            var applied = await service.SharePreDraftSlotAsync(
                "fuzz-share-pref-admin",
                state.SessionId,
                "Anchor",
                [new ShareSlotParticipantInput("Returning")]);
            if (!applied.IsSuccess)
                throw new InvalidOperationException($"Share mutation failed unexpectedly: {applied.Error}");
            state.Applied = true;
            await state.CaptureAsync(cancellationToken);
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(SharePreferenceState state)
        {
            if (!state.Applied && state.LastPreview is { IsSuccess: false } preview)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "share-preview-consumed-stale-team-preference",
                    $"Share preview rejected a returning member because stale same-team state was consumed: {preview.Error}",
                    "cross-feature:share-preview-consumed-stale-team-preference");
            }

            var snapshot = state.Snapshot;
            if (snapshot is null) yield break;

            if (state.Applied && (!snapshot.ReturningPresent || !snapshot.SharedSlotContainsAnchorAndReturning))
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "share-rejoin-did-not-establish-roster-truth",
                    "A successful pre-draft share did not leave the returning member present in the anchor shared slot.",
                    "cross-feature:share-rejoin-did-not-establish-roster-truth");
            }

            if (state.Applied && snapshot.ReturningStillInPreference)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "share-rejoin-resurrected-stale-team-preference",
                    "A member that had left the roster re-entered through share slot and silently resurrected its stale same-team membership.",
                    "cross-feature:share-rejoin-resurrected-stale-team-preference");
            }

            if (state.Applied &&
                (!snapshot.CorePreferencePlayerIds.SequenceEqual(new[] { state.CoreOneId, state.CoreTwoId }) ||
                 !snapshot.CorePreferenceRotationOrders.SequenceEqual(new[] { 1, 2 })))
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "share-rejoin-damaged-surviving-team-preference",
                    "Pruning the returning member's stale preference did not preserve the still-valid core preference with contiguous rotation.",
                    "cross-feature:share-rejoin-damaged-surviving-team-preference");
            }

            if (!snapshot.ForeignPreferencePlayerIds.SequenceEqual(new[] { state.ForeignOneId, state.ForeignTwoId }) ||
                !snapshot.ForeignPreferenceRotationOrders.SequenceEqual(new[] { 1, 2 }))
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "share-team-preference-cross-session-leak",
                    "Share/reconciliation activity in one session changed another session's same-team preference state.",
                    "cross-feature:share-team-preference-cross-session-leak");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
