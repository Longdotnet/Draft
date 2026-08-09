using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOverbookStateStoreSchemaTests
{
    [Fact]
    public async Task EnsureAsync_AllowsJsonObjectDefaultWithoutCompositeFormatFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);

        var storeType = typeof(ZaloOverbookService).Assembly.GetType(
            "VolleyDraft.Api.Services.ZaloOverbookStateStore",
            throwOnError: true)!;
        var store = Activator.CreateInstance(
            storeType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [db],
            culture: null)!;
        var ensure = storeType.GetMethod("EnsureAsync", BindingFlags.Instance | BindingFlags.Public)!;

        var task = (Task)ensure.Invoke(store, [CancellationToken.None])!;
        await task;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"ReminderMessageBanksJson\" FROM \"ZaloOverbookStates\" LIMIT 1;";
        await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task EnsureAsync_UpgradesExistingOverbookTableWithReminderBanksColumn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE "ZaloOverbookStates" (
                    "SessionId" TEXT PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL DEFAULT 0,
                    "GraceMinutes" INTEGER NOT NULL DEFAULT 10,
                    "ReminderIntervalMinutes" INTEGER NOT NULL DEFAULT 60,
                    "MaxReminders" INTEGER NOT NULL DEFAULT 5,
                    "MessageSource" TEXT NOT NULL DEFAULT 'AdminPool',
                    "FriendlyMessagesJson" TEXT NOT NULL DEFAULT '[]',
                    "SeriousMessagesJson" TEXT NOT NULL DEFAULT '[]',
                    "StrictMessagesJson" TEXT NOT NULL DEFAULT '[]',
                    "FirstObservedVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "LastObservedVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "SuggestedTargetVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "CurrentTargetVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "ConfirmedTargetVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "NeedsConfirmation" INTEGER NOT NULL DEFAULT 0,
                    "OrderConfidence" TEXT NOT NULL DEFAULT 'Unknown',
                    "CurrentPollId" TEXT NULL,
                    "CurrentSelectedOptionIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "LastPollUpdatedAtUnixMs" BIGINT NOT NULL DEFAULT 0,
                    "EffectiveSlotCount" INTEGER NOT NULL DEFAULT 0,
                    "RawVoterCount" INTEGER NOT NULL DEFAULT 0,
                    "ExcessSlotCount" INTEGER NOT NULL DEFAULT 0,
                    "ReminderCount" INTEGER NOT NULL DEFAULT 0,
                    "LastReminderAt" TEXT NULL,
                    "NextReminderAt" TEXT NULL,
                    "IncidentKey" TEXT NULL,
                    "UsedMessageKeysJson" TEXT NOT NULL DEFAULT '[]',
                    "LastMessageKey" TEXT NULL,
                    "LastActorId" TEXT NULL,
                    "LastObservedAt" TEXT NULL,
                    "LastError" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var storeType = typeof(ZaloOverbookService).Assembly.GetType(
            "VolleyDraft.Api.Services.ZaloOverbookStateStore",
            throwOnError: true)!;
        var store = Activator.CreateInstance(
            storeType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [db],
            culture: null)!;
        var ensure = storeType.GetMethod("EnsureAsync", BindingFlags.Instance | BindingFlags.Public)!;
        await (Task)ensure.Invoke(store, [CancellationToken.None])!;

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='ZaloOverbookStates';";
        var schema = Convert.ToString(await verify.ExecuteScalarAsync()) ?? string.Empty;
        Assert.Contains("ReminderMessageBanksJson", schema, StringComparison.Ordinal);
    }
}
