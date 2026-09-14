using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloInboundLifecycleStatefulFuzzTests
{
    private static readonly InboundLifecycleAction[] SeedActions =
    [
        new(InboundLifecycleActionKind.Claim),
        new(InboundLifecycleActionKind.Duplicate),
        new(InboundLifecycleActionKind.ExpireLease),
        new(InboundLifecycleActionKind.Restart),
        new(InboundLifecycleActionKind.Duplicate),
        new(InboundLifecycleActionKind.MarkTerminal),
        new(InboundLifecycleActionKind.Restart),
        new(InboundLifecycleActionKind.Duplicate)
    ];

    [Fact]
    public async Task Production_shaped_lifecycle_preserves_idempotency_across_expiry_terminalization_and_restart()
    {
        var scenario = new StatefulFuzzCase<InboundLifecycleAction>(
            "inbound-lifecycle-production-shape",
            20260914,
            SeedActions);

        await using var target = new InboundLifecycleTarget(scenario.Seed);
        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.Equal(2, target.LastState!.AcceptedClaims);
        Assert.Equal(2, target.LastState.DuplicateClaims);
        Assert.True(target.LastState.Terminalized);
    }

    [Fact]
    public async Task Stateful_sequence_mutations_never_resurrect_terminal_delivery_or_overwrite_original_payload()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedActions,
                seed,
                CreateAction,
                operationCount: 12);
            var scenario = new StatefulFuzzCase<InboundLifecycleAction>(
                $"inbound-lifecycle-{seed}",
                seed,
                actions);

            await using var target = new InboundLifecycleTarget(seed);
            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static InboundLifecycleAction CreateAction(StableFuzzRandom random) =>
        new((InboundLifecycleActionKind)random.NextInt(Enum.GetValues<InboundLifecycleActionKind>().Length));

    private static string Describe(StatefulFuzzRunResult<InboundLifecycleAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum InboundLifecycleActionKind
    {
        Claim,
        Duplicate,
        ExpireLease,
        MarkTerminal,
        Restart
    }

    internal sealed record InboundLifecycleAction(InboundLifecycleActionKind Kind);

    internal sealed class InboundLifecycleState
    {
        public bool ExpectedClaimable { get; set; } = true;
        public bool Terminalized { get; set; }
        public bool? LastClaimAccepted { get; set; }
        public bool? LastClaimExpectedAccepted { get; set; }
        public int AcceptedClaims { get; set; }
        public int DuplicateClaims { get; set; }
        public int RestartCount { get; set; }
        public string? FirstObservedContent { get; set; }
        public string? FirstObservedSenderId { get; set; }
        public long? FirstObservedSentAtUnixMs { get; set; }
        public string? ViolationId { get; set; }
        public string? ViolationMessage { get; set; }
    }

    internal sealed class InboundLifecycleTarget : IStatefulFuzzTarget<InboundLifecycleState, InboundLifecycleAction>, IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly DbContextOptions<VolleyDraftDbContext> _options;
        private readonly ZaloIncomingMessageEvent _incoming;
        private int _attempt;

        public InboundLifecycleTarget(int seed)
        {
            var connectionString = $"Data Source=inbound-lifecycle-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            _anchor = new SqliteConnection(connectionString);
            _anchor.Open();
            _options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            SeedTrackedGroup(seed);

            var originalContent = seed % 2 == 0 ? "@Npc 9" : "claim slot";
            var originalSenderId = $"u-{seed}";
            var originalSentAtUnixMs = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(seed)
                .ToUnixTimeMilliseconds();
            _incoming = new ZaloIncomingMessageEvent(
                "bot-account",
                "bot-account",
                "g1",
                $"lifecycle-{seed}",
                originalSenderId,
                originalSenderId,
                originalContent,
                [],
                seed % 2 == 0,
                originalSentAtUnixMs);
        }

        public string Name => "inbound-lifecycle-state-machine";
        public InboundLifecycleState? LastState { get; private set; }

        public InboundLifecycleState CreateState(StatefulFuzzCase<InboundLifecycleAction> scenario)
        {
            LastState = new InboundLifecycleState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            InboundLifecycleState state,
            InboundLifecycleAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.LastClaimAccepted = null;
            state.LastClaimExpectedAccepted = null;

            switch (action.Kind)
            {
                case InboundLifecycleActionKind.Claim:
                    await ClaimAsync(state, mutatePayload: false, cancellationToken);
                    break;
                case InboundLifecycleActionKind.Duplicate:
                    await ClaimAsync(state, mutatePayload: true, cancellationToken);
                    break;
                case InboundLifecycleActionKind.ExpireLease:
                    await ExpireLeaseAsync(state, cancellationToken);
                    break;
                case InboundLifecycleActionKind.MarkTerminal:
                    await MarkTerminalAsync(state, cancellationToken);
                    break;
                case InboundLifecycleActionKind.Restart:
                    state.RestartCount += 1;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(InboundLifecycleState state)
        {
            if (!string.IsNullOrWhiteSpace(state.ViolationId))
            {
                yield return new StatefulInvariantViolation(
                    "idempotency",
                    state.ViolationId,
                    state.ViolationMessage ?? state.ViolationId);
                yield break;
            }

            using var verifier = new VolleyDraftDbContext(_options);
            var rows = verifier.ZaloGroupMessages
                .AsNoTracking()
                .Where(message => message.ZaloConnectionId == "conn" && message.MessageId == _incoming.MessageId)
                .ToList();

            if (rows.Count > 1)
            {
                yield return new StatefulInvariantViolation(
                    "idempotency",
                    "inbound-lifecycle-created-extra-row",
                    $"durable row count={rows.Count}");
                yield break;
            }

            if (rows.Count == 0)
                yield break;

            var row = rows[0];
            if (state.FirstObservedContent is null ||
                state.FirstObservedSenderId is null ||
                state.FirstObservedSentAtUnixMs is null)
            {
                yield return new StatefulInvariantViolation(
                    "idempotency",
                    "durable-inbound-row-missing-first-observation-oracle",
                    "a durable row exists before the harness captured the accepted first observation");
                yield break;
            }

            if (!string.Equals(row.Content, state.FirstObservedContent, StringComparison.Ordinal) ||
                !string.Equals(row.SenderId, state.FirstObservedSenderId, StringComparison.Ordinal) ||
                row.SentAt.ToUnixTimeMilliseconds() != state.FirstObservedSentAtUnixMs.Value)
            {
                yield return new StatefulInvariantViolation(
                    "idempotency",
                    "duplicate-overwrote-original-inbound-payload",
                    $"content={row.Content}; sender={row.SenderId}; sentAt={row.SentAt.ToUnixTimeMilliseconds()}");
                yield break;
            }

            if (state.Terminalized && string.Equals(row.ReplyOutcome, "ingress_processing", StringComparison.Ordinal))
            {
                yield return new StatefulInvariantViolation(
                    "idempotency",
                    "terminal-ingress-outcome-resurrected",
                    "a terminal delivery returned to ingress_processing");
            }
        }

        private async Task ClaimAsync(
            InboundLifecycleState state,
            bool mutatePayload,
            CancellationToken cancellationToken)
        {
            var expectedAccepted = state.ExpectedClaimable && !state.Terminalized;
            var attempt = ++_attempt;
            var incoming = mutatePayload
                ? _incoming with
                {
                    Content = $"{_incoming.Content} duplicate-{attempt}",
                    SenderName = $"mutated-{attempt}",
                    SentAtUnixMs = _incoming.SentAtUnixMs + attempt
                }
                : _incoming;

            await using var db = new VolleyDraftDbContext(_options);
            var claim = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                db,
                NullLogger<ZaloInboundCoordinator>.Instance,
                incoming,
                cancellationToken);

            var accepted = claim.IsTracked && !claim.IsDuplicate;
            state.LastClaimExpectedAccepted = expectedAccepted;
            state.LastClaimAccepted = accepted;

            if (accepted)
            {
                state.AcceptedClaims += 1;
                state.ExpectedClaimable = false;
                if (state.FirstObservedContent is null)
                {
                    state.FirstObservedContent = incoming.Content.Trim();
                    state.FirstObservedSenderId = incoming.SenderId.Trim();
                    state.FirstObservedSentAtUnixMs = incoming.SentAtUnixMs;
                }
            }
            else if (claim.IsDuplicate)
            {
                state.DuplicateClaims += 1;
            }

            if (accepted != expectedAccepted)
            {
                state.ViolationId = expectedAccepted
                    ? "claimable-ingress-was-rejected"
                    : "duplicate-or-terminal-ingress-was-reclaimed";
                state.ViolationMessage =
                    $"expectedAccepted={expectedAccepted}; accepted={accepted}; duplicate={claim.IsDuplicate}; terminalized={state.Terminalized}";
            }
        }

        private async Task ExpireLeaseAsync(InboundLifecycleState state, CancellationToken cancellationToken)
        {
            if (state.Terminalized)
                return;

            var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await using var db = new VolleyDraftDbContext(_options);
            var updated = await db.ZaloGroupMessages
                .Where(message =>
                    message.ZaloConnectionId == "conn" &&
                    message.MessageId == _incoming.MessageId &&
                    message.ReplyOutcome == "ingress_processing")
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(
                        message => message.ProcessingStartedAt,
                        (DateTimeOffset?)expiredAt),
                    cancellationToken);

            if (updated > 0)
                state.ExpectedClaimable = true;
        }

        private async Task MarkTerminalAsync(InboundLifecycleState state, CancellationToken cancellationToken)
        {
            var terminalProcessingStartedAt = DateTimeOffset.UtcNow.AddHours(-1);
            await using var db = new VolleyDraftDbContext(_options);
            var updated = await db.ZaloGroupMessages
                .Where(message =>
                    message.ZaloConnectionId == "conn" &&
                    message.MessageId == _incoming.MessageId)
                .ExecuteUpdateAsync(
                    updates => updates
                        .SetProperty(message => message.ReplyOutcome, "no_reply")
                        .SetProperty(message => message.ProcessingToken, "terminal-token")
                        .SetProperty(message => message.ProcessingStartedAt, (DateTimeOffset?)terminalProcessingStartedAt),
                    cancellationToken);

            if (updated > 0)
            {
                state.Terminalized = true;
                state.ExpectedClaimable = false;
            }
        }

        private void SeedTrackedGroup(int seed)
        {
            using var db = new VolleyDraftDbContext(_options);
            db.Database.EnsureCreated();

            var admin = new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = $"inbound-lifecycle-{seed}@example.test",
                PasswordHash = "x"
            };
            var connection = new ZaloConnection
            {
                Id = "conn",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "x",
                Status = ZaloConnectionStatus.Connected
            };
            db.AddRange(admin, connection);
            db.SaveChanges();

            new ZaloAutoSessionStore(db).EnsureAsync().GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow.ToString("O");
            db.Database.ExecuteSqlInterpolated($$"""
                INSERT INTO "ZaloTrackedGroups" (
                    "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName", "AutoSessionEnabled", "CreatedAt", "UpdatedAt")
                VALUES (
                    {{Guid.NewGuid().ToString("n")}}, {{admin.Id}}, {{connection.Id}}, {{"g1"}}, {{"g1"}}, {{1}}, {{now}}, {{now}});
                """);
            db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await _anchor.DisposeAsync();
        }
    }
}