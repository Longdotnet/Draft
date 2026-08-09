from pathlib import Path

path = Path('server/VolleyDraft.Api/Services/ZaloOverbookStateStore.cs')
source = path.read_text(encoding='utf-8')

old_create = '''        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);\n        await EnsureReminderBanksColumnAsync(cancellationToken);'''
new_create = '''        // Do not pass JSON object literals such as '{}' through ExecuteSqlRawAsync.\n        // EF Core treats braces as composite-format placeholders and throws FormatException.\n        await using (var createCommand = await CreateCommandAsync(sql, cancellationToken))\n            await createCommand.ExecuteNonQueryAsync(cancellationToken);\n        await EnsureReminderBanksColumnAsync(cancellationToken);'''
if old_create not in source:
    raise SystemExit('create-table anchor not found')
source = source.replace(old_create, new_create, 1)

old_alter = '''        if (!hasColumn)\n            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \\\"ZaloOverbookStates\\\" ADD COLUMN \\\"ReminderMessageBanksJson\\\" TEXT NOT NULL DEFAULT '{}';", cancellationToken);'''
new_alter = '''        if (!hasColumn)\n        {\n            await using var alterCommand = await CreateCommandAsync(\n                "ALTER TABLE \\\"ZaloOverbookStates\\\" ADD COLUMN \\\"ReminderMessageBanksJson\\\" TEXT NOT NULL DEFAULT '{}';",\n                cancellationToken);\n            await alterCommand.ExecuteNonQueryAsync(cancellationToken);\n        }'''
if old_alter not in source:
    raise SystemExit('alter-table anchor not found')
source = source.replace(old_alter, new_alter, 1)
path.write_text(source, encoding='utf-8')

Path('server/VolleyDraft.Api.Tests/ZaloOverbookStateStoreSchemaTests.cs').write_text(r'''using System.Reflection;
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
''', encoding='utf-8')

print('hotfix patched')
