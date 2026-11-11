using System.Diagnostics;
using System.Threading.Channels;
using VolleyDraft.Api.Security;

namespace VolleyDraft.Api.Services;

public sealed class ZaloTrendMusicLocalRenderSignal
{
    private readonly Channel<byte> channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public void Wake() => channel.Writer.TryWrite(1);

    public ValueTask<byte> WaitAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAsync(cancellationToken);
}

public sealed class ZaloTrendMusicLocalRenderWorker(
    ZaloTrendMusicLocalRenderSignal signal,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ZaloTrendMusicLocalRenderWorker> logger) : BackgroundService
{
    private const int MaxDrainJobs = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Trend music local Render worker initialized Enabled={Enabled}",
            configuration.GetValue("ZaloBot:Ambient:TrendMusic:LocalRendererEnabled", false));
        if (configuration.GetValue("ZaloBot:Ambient:TrendMusic:LocalRendererEnabled", false))
            signal.Wake();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(stoppingToken);
                if (!configuration.GetValue("ZaloBot:Ambient:TrendMusic:LocalRendererEnabled", false))
                    continue;

                var settings = ZaloTrendMusicSettings.FromConfiguration(configuration);
                if (settings.CanDirectDispatch)
                    continue;

                await DrainQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Trend music local Render worker failed");
            }
        }
    }

    private async Task DrainQueueAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < MaxDrainJobs && !cancellationToken.IsCancellationRequested; index++)
        {
            var result = await RunOnceAsync(cancellationToken);
            if (!result.Success || result.NoPendingJob)
                return;
        }
    }

    private async Task<LocalRenderResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var key = TrendMusicInternalAuthorization.GetExpectedKey(configuration);
        if (string.IsNullOrWhiteSpace(key) || System.Text.Encoding.UTF8.GetByteCount(key) < 32)
        {
            logger.LogWarning("Trend music local renderer skipped because internal callback key is not configured");
            return new(false, false);
        }

        var publicBaseUrl = configuration["Public:BaseUrl"]?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicBaseUri) ||
            !string.Equals(publicBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Trend music local renderer skipped because Public:BaseUrl is not a valid HTTPS origin");
            return new(false, false);
        }

        var script = Path.Combine(environment.ContentRootPath, "scripts", "trend-music", "claim_and_render.py");
        if (!File.Exists(script))
        {
            logger.LogWarning("Trend music local renderer script is missing Path={Path}", script);
            return new(false, false);
        }

        var python = Environment.GetEnvironmentVariable("TREND_MUSIC_PYTHON")?.Trim();
        if (string.IsNullOrWhiteSpace(python))
            python = "python3";

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(script);
        startInfo.Environment["SCHEDULER_KEY"] = key;
        startInfo.Environment["TREND_MUSIC_CALLBACK_KEY"] = key;
        startInfo.Environment["TREND_MUSIC_CALLBACK_URL"] =
            $"{publicBaseUrl}/api/internal/trend-music/render-complete";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["TREND_MUSIC_LIGHTWEIGHT"] = "1";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            logger.LogWarning("Trend music local renderer process could not be started");
            return new(false, false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var noPending = stdout.Contains("no pending trend-music render job", StringComparison.OrdinalIgnoreCase);

        if (process.ExitCode == 0)
        {
            logger.LogInformation(
                noPending
                    ? "Trend music local renderer found no pending job"
                    : "Trend music local renderer completed a queued job");
            return new(true, noPending);
        }

        logger.LogWarning(
            "Trend music local renderer exited with code {ExitCode}. Stdout={Stdout} Stderr={Stderr}",
            process.ExitCode,
            Tail(stdout),
            Tail(stderr));
        return new(false, false);
    }

    private static string Tail(string value)
    {
        const int limit = 2000;
        var trimmed = value.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[^limit..];
    }

    private sealed record LocalRenderResult(bool Success, bool NoPendingJob);
}
