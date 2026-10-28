namespace VolleyDraft.Api.Services.Zalo.AI;

/// <summary>
/// Per-incoming-turn HTTP budget for external AI calls. The budget flows through async
/// feature/pre-route work via AsyncLocal and is intentionally absent for background jobs
/// that do not begin an inbound Zalo turn.
/// </summary>
internal static class ZaloAiRequestBudget
{
    private sealed class BudgetState(int maxCalls)
    {
        private int remaining = Math.Max(0, maxCalls);

        public int Remaining => Math.Max(0, Volatile.Read(ref remaining));

        public bool TryConsume()
        {
            while (true)
            {
                var current = Volatile.Read(ref remaining);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref remaining, current - 1, current) == current)
                    return true;
            }
        }
    }

    private sealed class Scope(BudgetState? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Current.Value = previous;
        }
    }

    private static readonly AsyncLocal<BudgetState?> Current = new();

    public static IDisposable Begin(int maxCalls)
    {
        var previous = Current.Value;
        Current.Value = new BudgetState(maxCalls);
        return new Scope(previous);
    }

    public static bool TryConsume() => Current.Value?.TryConsume() ?? true;

    internal static bool IsActive => Current.Value is not null;
    internal static int? RemainingForTests => Current.Value?.Remaining;
}
