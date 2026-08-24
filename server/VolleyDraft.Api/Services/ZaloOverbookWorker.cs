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

                // Guest commands are intentionally reply-gated. Ordinary group chat must
                // never fall into +1/+2/cancel/profile handling just because a session is
                // currently recruiting.
                var guestTurns = await overbook.ProcessReplyGatedRecruitmentGuestTurnsDueAsync(stoppingToken);
                if (guestTurns > 0)
                    logger.LogInformation("Recruitment guest cycle handled {HandledCount} turn(s)", guestTurns);

                // Guest overflow has its own sponsor-aware waitlist because the friend
                // may not have a Zalo UID. Always consume that queue before a due
                // conditional +guest promise gets a chance to use newly free room.
                var guestPromotions = await overbook.ProcessGuestWaitlistDueAsync(stoppingToken);
                if (guestPromotions > 0)
                    logger.LogInformation("Guest waitlist cycle announced {PromotionCount} promotion(s)", guestPromotions);

                // Conditional guest intents are durable promises such as
                // "nếu 19h vẫn thiếu thì +2". At the due time this lane re-syncs the
                // exact poll and evaluates the condition before any mutation.
                var conditionalGuests = await overbook.ProcessConditionalGuestIntentsDueAsync(stoppingToken);
                if (conditionalGuests > 0)
                    logger.LogInformation("Conditional guest cycle resolved {HandledCount} intent(s)", conditionalGuests);

                // Event observation is separate from send cooldown. This lane refreshes
                // the linked poll every cycle while KeepRecruiting is active, consumes a
                // guest waitlist first, debounces accidental unvotes, and only then
                // chooses soft update vs @all for a confirmed roster drop.
                var rosterChanges = await overbook.ProcessRecruitmentRosterChangesDueAsync(stoppingToken);
                if (rosterChanges > 0)
                    logger.LogInformation("Recruitment roster-change cycle sent {SentCount} update(s)", rosterChanges);

                // KeepRecruiting owns group-wide recruiting once a live organizer has
                // explicitly chosen that direction. Run it before the leader-aware V2
                // reminder so a handled recruitment bucket is not duplicated by a
                // second leader-tag message in the same cycle.
                var recruitingSent = await overbook.ProcessKeepRecruitingBroadcastsDueAsync(stoppingToken);
                if (recruitingSent > 0)
                    logger.LogInformation("Keep-recruiting broadcast cycle sent {SentCount} message(s)", recruitingSent);

                // If a full/locked roster is blocked only by missing profile fields,
                // target the exact verified Zalo identities first. This lane marks the
                // same draft-preparation bucket handled after a successful send, so the
                // generic leader reminder does not immediately duplicate the prompt.
                var profilePrompts = await overbook.ProcessMissingProfilePromptsDueAsync(stoppingToken);
                if (profilePrompts > 0)
                    logger.LogInformation("Missing-profile collection cycle sent {SentCount} targeted prompt(s)", profilePrompts);

                // The leader-aware preparation lane owns proactive draft reminders.
                // It always refreshes the exact poll/option linked to MatchSession and
                // separates observed roster state from leader decisions such as
                // KeepRecruiting / PlayCurrentRoster / StopMatch.
                var draftSent = await overbook.ProcessDraftPreparationRemindersDueV2Async(stoppingToken);
                if (draftSent > 0)
                    logger.LogInformation("Draft preparation reminder cycle sent {SentCount} message(s)", draftSent);

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
                logger.LogError(exception, "Overbook reminder/recruitment-guest/guest-waitlist/conditional-guest/roster-change/keep-recruiting/profile-collection/draft-preparation/community-nudge/V2 migration/trace/retention cycle failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}