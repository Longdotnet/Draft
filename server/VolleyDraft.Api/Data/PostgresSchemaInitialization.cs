using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace VolleyDraft.Api.Data;

// Recreated scoped stores share schema initialization, but never cache transactional
// DDL: a later rollback must leave the next caller able to initialize the schema.
internal static class PostgresSchemaInitialization
{
    private static readonly SuccessfulInitializationCache Cache = new();

    public static Task EnsureAsync(
        VolleyDraftDbContext db,
        string schema,
        Func<CancellationToken, Task> initialize,
        CancellationToken cancellationToken)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true
            || db.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            return initialize(cancellationToken);

        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(db.Database.GetDbConnection().ConnectionString)));
        return Cache.EnsureAsync($"{identity}:{schema}", initialize, cancellationToken);
    }
}

internal sealed class SuccessfulInitializationCache
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Completed;
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new();

    public async Task EnsureAsync(
        string key,
        Func<CancellationToken, Task> initialize,
        CancellationToken cancellationToken)
    {
        var entry = entries.GetOrAdd(key, _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Completed) return;
            await initialize(cancellationToken);
            entry.Completed = true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }
}
