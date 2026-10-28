namespace VolleyDraft.Api.Services.Zalo.AI;

/// <summary>
/// Per-incoming-turn HTTP budget for external AI calls. The budget flows through async
/// feature/pre-route work via AsyncLocal and is intentionally absent for background jobs
/// that do not begin an inbound Zalo turn.
/// </summary>
internal static class ZaloAiRequestBudget
{
    private sealed class BudgetState(int preAnswerCalls, int answerCalls)
    {
        private int preAnswerRemaining = Math.Max(0, preAnswerCalls);
        private int answerRemaining = Math.Max(0, answerCalls);

        public int Remaining =>
            Math.Max(0, Volatile.Read(ref preAnswerRemaining)) +
            Math.Max(0, Volatile.Read(ref answerRemaining));

        public bool TryConsume(ZaloAiWorkload workload) =>
            IsAnswerWorkload(workload)
                ? TryConsume(ref answerRemaining)
                : TryConsume(ref preAnswerRemaining);

        private static bool TryConsume(ref int remaining)
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

    public static IDisposable Begin(int preAnswerCalls, int answerCalls)
    {
        var previous = Current.Value;
        Current.Value = new BudgetState(preAnswerCalls, answerCalls);
        return new Scope(previous);
    }

    public static bool TryConsume(ZaloAiWorkload workload) => Current.Value?.TryConsume(workload) ?? true;

    internal static bool IsActive => Current.Value is not null;
    internal static int? RemainingForTests => Current.Value?.Remaining;

    internal static bool IsAnswerWorkload(ZaloAiWorkload workload) => workload is
        ZaloAiWorkload.GeneralChat or
        ZaloAiWorkload.SocialReply or
        ZaloAiWorkload.DomainNarration;
}
