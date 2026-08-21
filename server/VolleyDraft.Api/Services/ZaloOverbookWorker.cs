using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed class ZaloOverbookWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ZaloOverbookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        var nextV2RetentionAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var overbook = scope.ServiceProvider.GetRequiredService<ZaloOverbookService>();
                var sent = await overbook.ProcessDueAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Overbook reminder cycle sent {SentCount} message(s)", sent);

                var draftSent = await overbook.ProcessDraftAutopilotDueAsync(stoppingToken);
                if (draftSent > 0)
                    logger.LogInformation("Draft autopilot cycle sent {SentCount} message(s)", draftSent);

                var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
                var bridge = scope.ServiceProvider.GetRequiredService<ZaloBridgeClient>();
                var nudgeLogger = scope.ServiceProvider.GetRequiredService<ILogger<ZaloCommunityNudgeService>>();
                var communitySent = await new ZaloCommunityNudgeService(db, bridge, nudgeLogger)
                    .ProcessDueAsync(stoppingToken);
                if (communitySent > 0)
                    logger.LogInformation("Community nudge cycle sent {SentCount} message(s)", communitySent);

                var canonicalization = await new ZaloLegacyOutboundCanonicalizer(db)
                    .CanonicalizeAsync(500, stoppingToken);
                if (canonicalization.Canonicalized > 0 || canonicalization.Ambiguous > 0)
                {
                    logger.LogInformation(
                        "Zalo V2 provider-ID canonicalization scanned {ScannedCount}, canonicalized {CanonicalizedCount}, ambiguous {AmbiguousCount}",
                        canonicalization.Scanned,
                        canonicalization.Canonicalized,
                        canonicalization.Ambiguous);
                }

                var pendingProjection = await new ZaloLegacyPendingStateProjector(db)
                    .ProjectAsync(500, stoppingToken);
                if (pendingProjection.Projected > 0 || pendingProjection.SkippedDifferentIntent > 0)
                {
                    logger.LogInformation(
                        "Zalo V2 pending-state projection scanned {ScannedCount}, projected {ProjectedCount}, skippedDifferentIntent {SkippedCount}",
                        pendingProjection.Scanned,
                        pendingProjection.Projected,
                        pendingProjection.SkippedDifferentIntent);
                }

                var projection = await new ZaloLegacyOutcomeTraceProjector(db)
                    .ProjectAsync(500, stoppingToken);
                if (projection.Projected > 0)
                {
                    logger.LogInformation(
                        "Zalo V2 trace projection scanned {ScannedCount} terminal messages and added {ProjectedCount} trace(s)",
                        projection.Scanned,
                        projection.Projected);
                }

                var enrichment = await new ZaloLegacyTraceEnricher(db)
                    .EnrichAsync(500, stoppingToken);
                if (enrichment.Enriched > 0)
                {
                    logger.LogInformation(
                        "Zalo V2 trace enrichment scanned {ScannedCount} projected traces and enriched {EnrichedCount}",
                        enrichment.Scanned,
                        enrichment.Enriched);
                }

                var now = DateTimeOffset.UtcNow;
                if (now >= nextV2RetentionAt)
                {
                    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var policy = ZaloRetentionPolicy.FromConfiguration(configuration);
                    var cleanup = await new ZaloV2RetentionService(db)
                        .CleanupAsync(policy, now, stoppingToken);
                    var deletedReceipts = await new ZaloOutboundReceiptStore(db)
                        .DeleteOlderThanAsync(policy.MessageRelationCutoff(now), stoppingToken);
                    if (cleanup.DeletedTraces + cleanup.DeletedMessageRelations + cleanup.DeletedUserConcepts + deletedReceipts > 0)
                    {
                        logger.LogInformation(
                            "Zalo V2 retention cleanup deleted traces={TraceCount}, relations={RelationCount}, concepts={ConceptCount}, outboundReceipts={ReceiptCount}",
                            cleanup.DeletedTraces,
                            cleanup.DeletedMessageRelations,
                            cleanup.DeletedUserConcepts,
                            deletedReceipts);
                    }
                    nextV2RetentionAt = now.AddHours(6);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Overbook reminder/draft-autopilot/community-nudge/V2 migration/trace/retention cycle failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
