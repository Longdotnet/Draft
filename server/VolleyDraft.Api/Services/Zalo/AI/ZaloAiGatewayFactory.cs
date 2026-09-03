namespace VolleyDraft.Api.Services.Zalo.AI;

/// <summary>
/// Transitional composition helper for feature classes that still receive HttpClient/IConfiguration/ILogger
/// directly. It keeps provider/model/retry/fallback policy inside <see cref="IZaloAiGateway"/> while those
/// callers are migrated to constructor injection. New feature modules should inject IZaloAiGateway instead.
/// </summary>
public static class ZaloAiGatewayFactory
{
    public static IZaloAiGateway Create(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger logger) =>
        new OpenAiCompatibleZaloAiGateway(
            httpClient,
            configuration,
            new ForwardingLogger<OpenAiCompatibleZaloAiGateway>(logger));

    private sealed class ForwardingLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
