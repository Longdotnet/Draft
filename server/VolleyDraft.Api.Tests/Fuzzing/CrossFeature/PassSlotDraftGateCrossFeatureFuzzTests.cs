using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class PassSlotDraftGateCrossFeatureFuzzTests
{
    [Fact]
    public async Task Draft_gate_tracks_authoritative_pass_slot_ledger_across_restart_and_resolution()
    {
        await using var target = new PassSlotDraftGateTarget();
        var scenario = new StatefulFuzzCase<PassSlotDraftGateAction>(
            "pass-slot-draft-gate-restart-resolution",
            20260909,
            [
                new(PassSlotDraftGateActionKind.OpenOwned),
                new(PassSlotDraftGateActionKind.Restart),
                new(PassSlotDraftGateActionKind.ClaimOwned),
                new(PassSlotDraftGateActionKind.BeginApplyOwned),
                new(PassSlotDraftGateActionKind.CompleteOwned),
                new(PassSlotDraftGateActionKind.AttemptDraft)
            ]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Sequence_mutations_never_allow_active_pass_risk_to_cross_final_draft_gate()
    {
        PassSlotDraftGateAction[] seedActions =
        [
            new(PassSlotDraftGateActionKind.OpenOwned),
            new(PassSlotDraftGateActionKind.Restart),
            new(PassSlotDraftGateActionKind.ClaimOwned),
            new(PassSlotDraftGateActionKind.BeginApplyOwned),
            new(PassSlotDraftGateActionKind.CompleteOwned),
            new(PassSlotDraftGateActionKind.OpenForeignConnection),
            new(PassSlotDraftGateActionKind.OpenForeignSession),
            new(PassSlotDraftGateActionKind.AttemptDraft)
        ];

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                seedActions,
                seed,
                random => new PassSlotDraftGateAction((PassSlotDraftGateActionKind)random.NextInt(9)),
                operationCount: 12);
            await using var target = new PassSlotDraftGateTarget();
            var scenario = new StatefulFuzzCase<PassSlotDraftGateAction>(
                $"pass-slot-draft-gate-cross-feature-{seed}",
                seed,
                actions);

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<PassSlotDraftGateAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum PassSlotDraftGateActionKind
    {
        OpenOwned,
        ClaimOwned,
        BeginApplyOwned,
        CompleteOwned,
        CancelOwned,
        Restart,
        OpenForeignConnection,
        OpenForeignSession,
        AttemptDraft
    }

    internal sealed record PassSlotDraftGateAction(PassSlotDraftGateActionKind Kind);

    internal sealed record DraftGateSnapshot(
        int ActiveRiskBeforeAttempt,
        bool DraftSucceeded,
        int StatusCode,
        SessionStatus SessionStatus,
        int DraftRoundCount);

    internal sealed class PassSlotDraftGateState : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<VolleyDraftDbContext> options;

        public PassSlotDraftGateState()
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
        public string ConnectionId { get; } = "fuzz-draft-gate-conn";
        public string ForeignConnectionId { get; } = "fuzz-draft-gate-foreign-conn";
        public string GroupId { get; } = "fuzz-draft-gate-group";
        public string AdminId { get; } = "fuzz-draft-gate-admin";
        public ZaloOpenSlotOfferSnapshot? OwnedOffer { get; set; }
        public DraftGateSnapshot? LastAttempt { get; set; }
        public bool AttemptedDraft { get; set; }

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new VolleyDraftDbContext(options);
        }

        private async Task SeedAsync()
        {
            var admin = new User
            {
                Id = AdminId,
                DisplayName = "Fuzz Draft Gate Admin",
                Email = "fuzz-draft-gate-admin@example.test",
                PasswordHash = "test"
            };
            Db.Users.Add(admin);
            Db.ZaloConnections.AddRange(
                new ZaloConnection
                {
                    Id = ConnectionId,
                    AdminUserId = admin.Id,
                    AccountZaloId = "fuzz-draft-gate-bot",
                    DisplayName = "NPC",
                    EncryptedCredentials = "test"
                },
                new ZaloConnection
                {
                    Id = ForeignConnectionId,
                    AdminUserId = admin.Id,
                    AccountZaloId = "fuzz-draft-gate-foreign-bot",
                    DisplayName = "NPC Foreign",
                    EncryptedCredentials = "test"
                });
            await Db.SaveChangesAsync();

            var service = new SessionDraftService(Db);
            var created = await service.CreateSessionAsync(AdminId, new CreateSessionRequest("T6", 3, 2));
            if (!created.IsSuccess) throw new InvalidOperationException(created.Error);
            SessionId = created.Value!.Id;
            var session = await Db.MatchSessions.SingleAsync(item => item.Id == SessionId);
            session.ZaloConnectionId = ConnectionId;
            session.ZaloGroupId = GroupId;
            session.BotEnabled = true;

            var foreign = await service.CreateSessionAsync(AdminId, new CreateSessionRequest("CN", 3, 2));
            if (!foreign.IsSuccess) throw new InvalidOperationException(foreign.Error);
            ForeignSessionId = foreign.Value!.Id;
            var foreignSession = await Db.MatchSessions.SingleAsync(item => item.Id == ForeignSessionId);
            foreignSession.ZaloConnectionId = ConnectionId;
            foreignSession.ZaloGroupId = GroupId;
            foreignSession.BotEnabled = true;
            await Db.SaveChangesAsync();

            var playerIds = new List<string>();
            for (var i = 1; i <= 6; i += 1)
            {
                var added = await service.AddPlayerAsync(
                    AdminId,
                    SessionId,
                    new AddPlayerRequest($"P{i}", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
                if (!added.IsSuccess) throw new InvalidOperationException(added.Error);
                playerIds.Add(added.Value!.Id);
            }

            var captains = await service.SetManualCaptainsAsync(
                AdminId,
                SessionId,
                new ManualCaptainsRequest(playerIds.Take(3).ToList()));
            if (!captains.IsSuccess) throw new InvalidOperationException(captains.Error);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class PassSlotDraftGateTarget :
        IStatefulFuzzTarget<PassSlotDraftGateState, PassSlotDraftGateAction>,
        IAsyncDisposable
    {
        public string Name => "pass-slot-draft-gate-cross-feature";
        public PassSlotDraftGateState? LastState { get; private set; }

        public PassSlotDraftGateState CreateState(StatefulFuzzCase<PassSlotDraftGateAction> scenario)
        {
            LastState = new PassSlotDraftGateState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            PassSlotDraftGateState state,
            PassSlotDraftGateAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == PassSlotDraftGateActionKind.Restart)
            {
                await state.RestartAsync();
                return;
            }

            var store = new ZaloOpenSlotOfferStore(state.Db);
            switch (action.Kind)
            {
                case PassSlotDraftGateActionKind.OpenOwned:
                    state.OwnedOffer = await store.OpenAsync(
                        state.ConnectionId,
                        state.GroupId,
                        "owner-uid",
                        "Owner",
                        state.SessionId,
                        "T6",
                        $"open-{actionIndex}",
                        DateTimeOffset.UtcNow.AddHours(1),
                        null,
                        cancellationToken);
                    return;
                case PassSlotDraftGateActionKind.ClaimOwned when state.OwnedOffer is not null:
                    if (state.OwnedOffer.Status == ZaloOpenSlotOfferStatus.Open &&
                        await store.TryClaimAsync(state.OwnedOffer, "claimant-uid", "Claimant", $"claim-{actionIndex}", cancellationToken))
                    {
                        state.OwnedOffer = state.OwnedOffer with
                        {
                            Status = ZaloOpenSlotOfferStatus.ClaimPending,
                            ClaimantZaloUserId = "claimant-uid",
                            ClaimantDisplayName = "Claimant",
                            Version = state.OwnedOffer.Version + 1
                        };
                    }
                    return;
                case PassSlotDraftGateActionKind.BeginApplyOwned when state.OwnedOffer is not null:
                    if (await store.TryBeginApplyAsync(state.OwnedOffer.Id, "claimant-uid", cancellationToken))
                        state.OwnedOffer = state.OwnedOffer with { Status = ZaloOpenSlotOfferStatus.Applying };
                    return;
                case PassSlotDraftGateActionKind.CompleteOwned when state.OwnedOffer is not null:
                    if (await store.CompleteAsync(state.OwnedOffer.Id, "claimant-uid", cancellationToken))
                        state.OwnedOffer = state.OwnedOffer with { Status = ZaloOpenSlotOfferStatus.Completed };
                    return;
                case PassSlotDraftGateActionKind.CancelOwned when state.OwnedOffer is not null:
                    if (await store.CancelAsync(state.OwnedOffer.Id, "owner-uid", cancellationToken))
                        state.OwnedOffer = state.OwnedOffer with { Status = ZaloOpenSlotOfferStatus.Cancelled };
                    return;
                case PassSlotDraftGateActionKind.OpenForeignConnection:
                    await store.OpenAsync(
                        state.ForeignConnectionId,
                        state.GroupId,
                        $"foreign-owner-{actionIndex}",
                        "Foreign Owner",
                        state.SessionId,
                        "T6",
                        $"foreign-connection-{actionIndex}",
                        DateTimeOffset.UtcNow.AddHours(1),
                        null,
                        cancellationToken);
                    return;
                case PassSlotDraftGateActionKind.OpenForeignSession:
                    await store.OpenAsync(
                        state.ConnectionId,
                        state.GroupId,
                        $"foreign-session-owner-{actionIndex}",
                        "Foreign Session Owner",
                        state.ForeignSessionId,
                        "CN",
                        $"foreign-session-{actionIndex}",
                        DateTimeOffset.UtcNow.AddHours(1),
                        null,
                        cancellationToken);
                    return;
                case PassSlotDraftGateActionKind.AttemptDraft:
                    if (state.AttemptedDraft) return;
                    state.AttemptedDraft = true;
                    var activeRisk = await new ZaloOpenSlotRiskCounter(state.Db)
                        .CountActiveForSessionAsync(state.ConnectionId, state.GroupId, state.SessionId, cancellationToken);
                    var result = await new SessionDraftService(state.Db)
                        .StartDraftAsync(state.AdminId, state.SessionId);
                    var session = await state.Db.MatchSessions.AsNoTracking()
                        .SingleAsync(item => item.Id == state.SessionId, cancellationToken);
                    var roundCount = await state.Db.DraftRounds.AsNoTracking()
                        .CountAsync(item => item.SessionId == state.SessionId, cancellationToken);
                    state.LastAttempt = new DraftGateSnapshot(
                        activeRisk,
                        result.IsSuccess,
                        result.StatusCode,
                        session.Status,
                        roundCount);
                    return;
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(PassSlotDraftGateState state)
        {
            var attempt = state.LastAttempt;
            if (attempt is null) yield break;

            if (attempt.ActiveRiskBeforeAttempt > 0 && attempt.DraftSucceeded)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "pass-risk-crossed-final-draft-gate",
                    $"Draft started with {attempt.ActiveRiskBeforeAttempt} active authoritative pass-slot offer(s).",
                    "cross-feature:pass-risk-crossed-final-draft-gate");
            }

            if (attempt.ActiveRiskBeforeAttempt > 0 &&
                (attempt.StatusCode != StatusCodes.Status409Conflict ||
                 attempt.SessionStatus != SessionStatus.CaptainSelection ||
                 attempt.DraftRoundCount != 0))
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "blocked-pass-risk-mutated-draft-state",
                    "An unresolved pass-slot offer blocked the request but draft state was still mutated or returned the wrong contract.",
                    "cross-feature:blocked-pass-risk-mutated-draft-state");
            }

            if (attempt.ActiveRiskBeforeAttempt == 0 && !attempt.DraftSucceeded)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "resolved-or-foreign-pass-risk-blocked-draft",
                    $"Draft was blocked without an authoritative active pass-slot risk (status {attempt.StatusCode}).",
                    "cross-feature:resolved-or-foreign-pass-risk-blocked-draft");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
