using VolleyDraft.Api.Data;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class PostgresSchemaInitializationTests
{
    [Fact]
    public async Task Concurrent_stores_initialize_once_and_database_schema_keys_are_isolated()
    {
        var cache = new SuccessfulInitializationCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task Initialize(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            await release.Task.WaitAsync(token);
        }

        var first = cache.EnsureAsync("db-a:schema-a", Initialize, default);
        await started.Task;
        var concurrent = Enumerable.Range(0, 20)
            .Select(_ => cache.EnsureAsync("db-a:schema-a", Initialize, default)).ToArray();
        release.SetResult();
        await Task.WhenAll(concurrent.Append(first));
        Assert.Equal(1, calls);

        Task Other(CancellationToken _) { calls++; return Task.CompletedTask; }
        await cache.EnsureAsync("db-b:schema-a", Other, default);
        await cache.EnsureAsync("db-a:schema-b", Other, default);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Failure_and_cancellation_do_not_poison_later_initialization()
    {
        var cache = new SuccessfulInitializationCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.EnsureAsync(
            "db:schema", _ => throw new InvalidOperationException(), default));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.EnsureAsync(
            "db:schema", _ => Task.CompletedTask, cancellation.Token));

        var calls = 0;
        Task Initialize(CancellationToken _) { calls++; return Task.CompletedTask; }
        await cache.EnsureAsync("db:schema", Initialize, default);
        await cache.EnsureAsync("db:schema", Initialize, default);
        Assert.Equal(1, calls);
    }
}
