using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class PassSlotDraftGateMultiInstanceFuzzTests
{
    [Fact]
    public async Task Draft_gate_observes_pass_slot_risk_created_by_another_instance()
    {
        await using var target = new MultiInstanceDraftGateTarget();
        var scenario = new StatefulFuzzCase<MultiInstanceAction>(
            "pass-slot-draft-gate-cross-instance-visibility",
            20260910,
            [
                new(MultiInstanceActionKind.OpenOwned),
                new(MultiInstanceActionKind.SwitchInstance),
                new(MultiInstanceActionKind.RestartActiveInstance),
                new(MultiInstanceActionKind.AttemptDraft)
            ]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Multi_instance_interleavings_preserve_final_draft_gate_authority()
    {
        MultiInstanceAction[] seedActions =
        [
            new(MultiInstanceActionKind.OpenOwned),
            new(MultiInstanceActionKind.SwitchInstance),
            new(MultiInstanceActionKind.ClaimOwned),
            new(MultiInstanceActionKind.RestartActiveInstance),
            new(MultiInstanceActionKind.BeginApplyOwned),
            new(MultiInstanceActionKind.SwitchInstance),
            new(MultiInstanceActionKind.CompleteOwned),
            new(MultiInstanceActionKind.OpenForeignConnection),
            new(MultiInstanceActionKind.OpenForeignSession),
            new(MultiInstanceActionKind.AttemptDraft)
        ];

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                seedActions,
                seed,
                random => new MultiInstanceAction((MultiInstanceActionKind)random.NextInt(10)),
                operationCount: 16);
            await using var target = new MultiInstanceDraftGateTarget();
            var scenario = new StatefulFuzzCase<MultiInstanceAction>(
                $"pass-slot-draft-gate-multi-instance-{seed}",
                seed,
                actions);

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<MultiInstanceAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum MultiInstanceActionKind
    {
        OpenOwned,
        ClaimOwned,
        BeginApplyOwned,
        CompleteOwned,
        CancelOwned,
        SwitchInstance,
        RestartActiveInstance,
        OpenForeignConnection,
        OpenForeignSession,
        AttemptDraft
    }

    internal sealed record MultiInstanceAction(MultiInstanceActionKind Kind);

    internal sealed record MultiInstanceDraftAttempt(
        int ActiveRiskBeforeAttempt,
        bool DraftSucceeded,
        int StatusCode,
        SessionStatus SessionStatus,
        int DraftRoundCount,
        string InstanceName);

    internal sealed class MultiInstanceState : IAsyncDisposable
    {
        private readonly string connectionString;
        private readonly SqliteConnection anchor;
        private readonly DbContextOptions<VolleyDraftDbContext> options;
        private bool useSecondary;

        public MultiInstanceState()
        {
            connectionString = $"Data Source=pass-slot-draft-gate-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            anchor = new SqliteConnection(connectionString);
            anchor.Open();
            options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            Primary = new VolleyDraftDbContext(options);
            Secondary = new VolleyDraftDbContext(options);
            Primary.Database.EnsureCreated();
            SeedAsync().GetAwaiter().GetResult();
        }

        public VolleyDraftDbContext Primary { get; private set; }
        public VolleyDraftDbContext Secondary { get; private set; }
        public VolleyDraftDbContext ActiveDb => useSecondary ? Secondary : Primary;
        public string ActiveInstanceName => useSecondary ? "secondary" : "primary";
        public string SessionId { get; private set; } = string.Empty;
        public string ForeignSessionId { get; private set; } = string.Empty;
        public string ConnectionId { get; } = "fuzz-draft-gate-multi-conn";
        public string ForeignConnectionId { get; } = "fuzz-draft-gate-multi-foreign-conn";
        public string GroupId { get; } = "fuzz-draft-gate-multi-group";
        public string AdminId { get; } = "fuzz-draft-gate-multi-admin";
        public ZaloOpenSlotOfferSnapshot? OwnedOffer { get; set; }
        public MultiInstanceDraftAttempt? LastAttempt { get; set; }
        public bool AttemptedDraft { get; set; }

        public void SwitchInstance() => useSecondary = !useSecondary;

        public async Task RestartActiveAsync()
        {
            if (useSecondary)
            {
                await Secondary.DisposeAsync();
                Secondary = new VolleyDraftDbContext(options);
            }
            else
            {
                await Primary.DisposeAsync();
                Primary = new VolleyDraftDbContext(options);
            }
        }

        private async Task SeedAsync()
        {
            var admin = new User
            {
                Id = AdminId,
                DisplayName = "Fuzz Multi Instance Admin",
                Email = "fuzz-multi-instance-admin@example.test",
                PasswordHash = "test"
            };
            Primary.Users.Add(admin);
            Primary.ZaloConnections.AddRange(
                new ZaloConnection
                {
                    Id = ConnectionId,
                    AdminUserId = admin.Id,
                    AccountZaloId = "fuzz-multi-instance-bot",
                    DisplayName = "NPC",
                    EncryptedCredentials = "test"
                },
                new ZaloConnection
                {
                    Id = ForeignConnectionId,
                    AdminUserId = admin.Id,
                    AccountZaloId = "fuzz-multi-instance-foreign-bot",
                    DisplayName = "NPC Foreign",
                    EncryptedCredentials = "test"
                });
            await Primary.SaveChangesAsync();

            var service = new SessionDraftService(Primary);
            var created = await service.CreateSessionAsync(AdminId, new CreateSessionRequest("T6", 3, 2));
            if (!created.IsSuccess) throw new InvalidOperationException(created.Error);
            SessionId = created.Value!.Id;
            var session = await Primary.MatchSessions.SingleAsync(item => item.Id == SessionId);
            session.ZaloConnectionId = ConnectionId;
            session.ZaloGroupId = GroupId;
            session.BotEnabled = true;

            var foreign = await service.CreateSessionAsync(AdminId, new CreateSessionRequest("CN", 3, 2));
            if (!foreign.IsSuccess) throw new InvalidOperationException(foreign.Error);
            ForeignSessionId = foreign.Value!.Id;
            var foreignSession = await Primary.MatchSessions.SingleAsync(item => item.Id == ForeignSessionId);
            foreignSession.ZaloConnectionId = ConnectionId;
            foreignSession.ZaloGroupId = GroupId;
            foreignSession.BotEnabled = true;
            await Primary.SaveChangesAsync();

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
            await Primary.DisposeAsync();
            await Secondary.DisposeAsync();
            await anchor.DisposeAsync();
        }
    }

    internal sealed class MultiInstanceDraftGateTarget :
        IStatefulFuzzTarget<MultiInstanceState, MultiInstanceAction>,
        IAsyncDisposable
    {
        public string Name => "pass-slot-draft-gate-multi-instance";
        public MultiInstanceState? LastState { get; private set; }

        public MultiInstanceState CreateState(StatefulFuzzCase<MultiInstanceAction> scenario)
        {
            LastState = new MultiInstanceState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            MultiInstanceState state,
            MultiInstanceAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (state.AttemptedDraft) return;

            if (action.Kind == MultiInstanceActionKind.SwitchInstance)
            {
                state.SwitchInstance();
                return;
            }

            if (action.Kind == MultiInstanceActionKind.RestartActiveInstance)
            {
                await state.RestartActiveAsync();
                return;
            }

            var db = state.ActiveDb;
            var store = new ZaloOpenSlotOfferStore(db);
            switch (action.Kind)
            {
                case MultiInstanceActionKind.OpenOwned:
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
                case MultiInstanceActionKind.ClaimOwned when state.OwnedOffer is not null:
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
                case MultiInstanceActionKind.BeginApplyOwned when state.OwnedOffer is not null:
                    if (await store.TryBeginApplyAsync(state.OwnedOffer.Id, "claimant-uid", cancellationToken))
                        state.OwnedOffer = state.OwnedOffer with { Status = ZaloOpenSlotOfferStatus.Applying };
                    return;
                case MultiInstanceActionKind.CompleteOwned when state.OwnedOffer is not null:
                    if (await store.CompleteAsync(state.OwnedOffer.Id, "claimant-uid", cancellationToken))
                        state.OwnedOffer = state.OwnedOffer with { Status = ZaloOpenSlotOfferStatus.Completed };
                    return;
                case MultiInstanceActionKind.CancelOwned when state.OwnedOffer is not null:
                    if (await store.CancelAsync(state.OwnedOffer.Id, "owner-uid", cancellationToken))
                        state.OwnedOffer = state.OwnedOffer with { Status = ZaloOpenSlotOfferStatus.Cancelled };
                    return;
                case MultiInstanceActionKind.OpenForeignConnection:
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
                case MultiInstanceActionKind.OpenForeignSession:
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
                case MultiInstanceActionKind.AttemptDraft:
                    state.AttemptedDraft = true;
                    var activeRisk = await new ZaloOpenSlotRiskCounter(db)
                        .CountActiveForSessionAsync(state.ConnectionId, state.GroupId, state.SessionId, cancellationToken);
                    var result = await new SessionDraftService(db)
                        .StartDraftAsync(state.AdminId, state.SessionId);
                    var session = await db.MatchSessions.AsNoTracking()
                        .SingleAsync(item => item.Id == state.SessionId, cancellationToken);
                    var roundCount = await db.DraftRounds.AsNoTracking()
                        .CountAsync(item => item.SessionId == state.SessionId, cancellationToken);
                    state.LastAttempt = new MultiInstanceDraftAttempt(
                        activeRisk,
                        result.IsSuccess,
                        result.StatusCode,
                        session.Status,
                        roundCount,
                        state.ActiveInstanceName);
                    return;
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(MultiInstanceState state)
        {
            var attempt = state.LastAttempt;
            if (attempt is null) yield break;

            if (attempt.ActiveRiskBeforeAttempt > 0 && attempt.DraftSucceeded)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "cross-instance-pass-risk-crossed-final-draft-gate",
                    $"{attempt.InstanceName} started draft with {attempt.ActiveRiskBeforeAttempt} active pass-slot offer(s) created through shared durable state.",
                    "cross-feature:cross-instance-pass-risk-crossed-final-draft-gate");
            }

            if (attempt.ActiveRiskBeforeAttempt > 0 &&
                (attempt.StatusCode != StatusCodes.Status409Conflict ||
                 attempt.SessionStatus != SessionStatus.CaptainSelection ||
                 attempt.DraftRoundCount != 0))
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "cross-instance-blocked-pass-risk-mutated-draft-state",
                    $"{attempt.InstanceName} observed active pass risk but the final draft gate returned an inconsistent contract or mutated draft state.",
                    "cross-feature:cross-instance-blocked-pass-risk-mutated-draft-state");
            }

            if (attempt.ActiveRiskBeforeAttempt == 0 && !attempt.DraftSucceeded)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "cross-instance-resolved-or-foreign-risk-blocked-draft",
                    $"{attempt.InstanceName} blocked draft without an authoritative active pass-slot risk (status {attempt.StatusCode}).",
                    "cross-feature:cross-instance-resolved-or-foreign-risk-blocked-draft");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
