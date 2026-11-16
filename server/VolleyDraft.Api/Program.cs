using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Security;
using VolleyDraft.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AppClient", policy =>
    {
        var configuredOrigins = builder.Configuration
            .GetSection("Cors:Origins")
            .Get<string[]>()
            ?? [];
        var origins = configuredOrigins
            .Concat(["http://127.0.0.1:5173", "http://localhost:5173"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddDbContext<VolleyDraftDbContext>(options =>
{
    var connectionString = NormalizePostgresConnectionString(
        builder.Configuration.GetConnectionString("Default")
        ?? builder.Configuration["DATABASE_URL"])
        ?? "Data Source=volley-draft.db";
    var provider = builder.Configuration["Database:Provider"];
    var usePostgres = string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase);

    if (usePostgres)
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<PasswordService>();
builder.Services.AddScoped<SessionDraftService>();
builder.Services.AddScoped<DraftBoardService>();
builder.Services.AddScoped<ZaloIntegrationService>();
builder.Services.AddScoped<ZaloGreetingTestService>();
builder.Services.AddScoped<ZaloAutoSessionSettingsService>();
builder.Services.AddScoped<ZaloBotService>();
builder.Services.AddScoped<ZaloBotImageService>();
builder.Services.AddScoped<ZaloTeamCardService>();
builder.Services.AddScoped<Npc11CardService>();
builder.Services.AddScoped<ZaloListenerCoordinator>();
builder.Services.AddScoped<ZaloReminderService>();
builder.Services.AddScoped<ZaloDraftReadinessService>();
builder.Services.AddScoped<ZaloScheduledDraftService>();
builder.Services.AddScoped<ZaloOverbookService>();
builder.Services.AddScoped<ZaloInboundCoordinator>();
builder.Services.AddScoped<SessionWaitlistService>();
builder.Services.AddScoped<ZaloBotActionHistoryService>();
builder.Services.AddScoped<ZaloActivityBackfillCoordinator>();
builder.Services.AddScoped<ZaloMemberActivityService>();
builder.Services.AddScoped<ZaloMembershipHistoryService>();
builder.Services.AddScoped<ZaloMemberIntelligenceBotService>();
builder.Services.AddSingleton<SettlementSummaryCardRenderer>();
builder.Services.AddScoped<SettlementService>();
builder.Services.AddScoped<SettlementAccessService>();
builder.Services.AddScoped<SettlementExcelExporter>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ZaloSchedulerTrigger>();
builder.Services.AddSingleton<ZaloPollEventQueue>();
builder.Services.AddHttpClient<AiAssistantService>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("TeamCardAvatars", client => client.Timeout = TimeSpan.FromSeconds(6));
builder.Services.AddHttpClient("Npc11ArtWorker", client => client.Timeout = TimeSpan.FromSeconds(190));
builder.Services.AddHttpClient("Npc11CloudflareAi", client => client.Timeout = TimeSpan.FromSeconds(125));
builder.Services.AddHttpClient("TrendMusicFeed", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("TrendMusicArtwork", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("TrendMusicGitHub", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("ZaloQuotedMedia", client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<ZaloTrendMusicService>();
builder.Services.AddScoped<ZaloBirthdayService>();
builder.Services.AddScoped<ZaloTrendMusicRenderDispatcher>();
builder.Services.AddHostedService<ZaloListenerWorker>();
builder.Services.AddHostedService<ZaloReminderWorker>();
builder.Services.AddHostedService<ZaloSchedulerWorker>();
builder.Services.AddHostedService<ZaloPollEventWorker>();
builder.Services.AddHostedService<ZaloOverbookWorker>();
builder.Services.AddHostedService<ZaloActivityBackfillWorker>();
builder.Services.AddHostedService<SettlementMessageWorker>();
builder.Services.AddHostedService<ZaloTrendMusicWorker>();
builder.Services.AddSingleton<ZaloCredentialProtector>();
builder.Services.AddSingleton<ZaloQrLoginRegistry>();
builder.Services.AddHttpClient<ZaloBridgeClient>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var baseUrl = configuration["Zalo:BridgeBaseUrl"] ?? "http://localhost:3000";
    client.BaseAddress = new Uri($"{baseUrl.TrimEnd('/')}/");
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.Add(
        "x-internal-key",
        configuration["Zalo:BridgeInternalKey"] ?? "development-zalo-bridge-key");
});
builder.Services.AddOpenApi();

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"] ?? throw new InvalidOperationException("Jwt:Key is required.");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"] ?? "VolleyDraft",
            ValidAudience = jwtSection["Audience"] ?? "VolleyDraftClient",
            IssuerSigningKey = signingKey
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<ClientIpAccessPolicy>();

var app = builder.Build();

var renderGitCommit = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT");
var deploymentRevision = string.IsNullOrWhiteSpace(renderGitCommit)
    ? null
    : renderGitCommit[..Math.Min(7, renderGitCommit.Length)];
app.MapGet("/health", () => Results.Ok(new { status = "ok", revision = deploymentRevision }));
app.MapZaloSchedulerHealth();
app.MapPost("/api/internal/scheduler/tick", (
    HttpContext httpContext,
    ZaloSchedulerTrigger trigger,
    IConfiguration configuration,
    ILogger<Program> logger) =>
{
    var expectedKey = configuration["Scheduler:Key"];
    if (string.IsNullOrWhiteSpace(expectedKey))
    {
        return Results.Json(
            new { message = "Scheduler key is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    if (!string.Equals(httpContext.Request.Headers["x-scheduler-key"], expectedKey, StringComparison.Ordinal))
        return Results.Unauthorized();

    var queued = trigger.TryTrigger();
    logger.LogInformation("Accepted scheduler tick from {RemoteIp}; queued={Queued}",
        httpContext.Connection.RemoteIpAddress,
        queued);
    return Results.Accepted(value: new
    {
        accepted = true,
        queued,
        requestedAt = DateTimeOffset.UtcNow
    });
});

app.MapPost("/api/internal/trend-music/render-complete", async (
    HttpContext httpContext,
    ZaloTrendMusicRenderCompleteRequest request,
    ZaloTrendMusicService service,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!TrendMusicInternalAuthorization.IsConfigured(configuration))
    {
        return Results.Json(
            new { message = "Trend music callback key is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    if (!TrendMusicInternalAuthorization.IsAuthorized(httpContext, configuration))
        return Results.Unauthorized();

    try
    {
        var sent = await service.CompleteRenderAsync(request, cancellationToken);
        return sent
            ? Results.Ok(new { accepted = true, sent = true })
            : Results.Conflict(new { accepted = true, sent = false, retry = true });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPost("/api/internal/trend-music/render-progress", async (
    HttpContext httpContext,
    ZaloTrendMusicRenderProgressRequest request,
    ZaloTrendMusicService service,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!TrendMusicInternalAuthorization.IsConfigured(configuration))
    {
        return Results.Json(
            new { message = "Trend music callback key is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    if (!TrendMusicInternalAuthorization.IsAuthorized(httpContext, configuration))
        return Results.Unauthorized();

    try
    {
        var sent = await service.ReportRenderProgressAsync(request, cancellationToken);
        return sent
            ? Results.Ok(new { accepted = true, sent = true })
            : Results.Conflict(new { accepted = true, sent = false, retry = true });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPost("/api/internal/trend-music/render-claim", async (
    HttpContext httpContext,
    ZaloTrendMusicService service,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!TrendMusicInternalAuthorization.IsConfigured(configuration))
    {
        return Results.Json(
            new { message = "Trend music internal key is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    if (!TrendMusicInternalAuthorization.IsAuthorized(httpContext, configuration))
        return Results.Unauthorized();

    var claim = await service.ClaimQueuedRenderAsync(cancellationToken);
    return claim is null ? Results.NoContent() : Results.Ok(claim);
});

app.MapPost("/api/internal/trend-music/recover-daily-greeting", async (
    HttpContext httpContext,
    ZaloDailyGreetingRecoveryRequest request,
    ZaloTrendMusicService service,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!TrendMusicInternalAuthorization.IsConfigured(configuration))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!TrendMusicInternalAuthorization.IsAuthorized(httpContext, configuration))
        return Results.Unauthorized();
    try
    {
        return Results.Ok(await service.RecoverDailyGreetingAsync(request, cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Use(async (httpContext, next) =>
{
    var accessPolicy = httpContext.RequestServices.GetRequiredService<ClientIpAccessPolicy>();
    if (!ClientIpAccessPolicy.IsInfrastructureRequest(httpContext) &&
        !accessPolicy.IsAllowed(httpContext))
    {
        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        await httpContext.Response.WriteAsJsonAsync(new { message = "This site is restricted to approved IP addresses." });
        return;
    }

    await next(httpContext);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("AppClient");
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
    await db.Database.EnsureCreatedAsync();
    await DatabaseSchemaPatch.EnsureLatestAsync(db);
}

var auth = app.MapGroup("/api/auth");
auth.MapPost("/register", async (RegisterRequest request, AuthService service) =>
    (await service.RegisterAsync(request)).ToHttpResult());
auth.MapPost("/login", async (LoginRequest request, AuthService service) =>
    (await service.LoginAsync(request)).ToHttpResult());
auth.MapGet("/me", async (HttpContext httpContext, AuthService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.MeAsync(userId)).ToHttpResult();
}).RequireAuthorization();

app.MapPost("/api/internal/zalo/events", async (
    HttpContext httpContext,
    ZaloIncomingMessageEvent incoming,
    ZaloInboundCoordinator coordinator,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var expectedKey = configuration["Zalo:WebhookKey"]
        ?? configuration["Zalo:BridgeInternalKey"]
        ?? "development-zalo-bridge-key";
    if (httpContext.Request.Headers["x-zalo-bridge-key"] != expectedKey)
    {
        return Results.Unauthorized();
    }

    var handling = await coordinator.HandleAsync(incoming, cancellationToken);
    return Results.Ok(new { accepted = handling.Accepted, handledBy = handling.HandledBy });
});

app.MapPost("/api/internal/zalo/membership-events", async (
    HttpContext httpContext,
    ZaloMembershipChangedEvent incoming,
    ZaloMembershipHistoryService membershipHistory,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var expectedKey = configuration["Zalo:WebhookKey"]
        ?? configuration["Zalo:BridgeInternalKey"]
        ?? "development-zalo-bridge-key";
    if (httpContext.Request.Headers["x-zalo-bridge-key"] != expectedKey)
        return Results.Unauthorized();

    var accepted = await membershipHistory.RecordProviderEventAsync(incoming, cancellationToken);
    return Results.Accepted(value: new { accepted });
});

app.MapPost("/api/internal/zalo/presence-events", async (
    HttpContext httpContext,
    ZaloMemberPresenceSignalEvent incoming,
    VolleyDraftDbContext db,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var expectedKey = configuration["Zalo:WebhookKey"]
        ?? configuration["Zalo:BridgeInternalKey"]
        ?? "development-zalo-bridge-key";
    if (httpContext.Request.Headers["x-zalo-bridge-key"] != expectedKey)
        return Results.Unauthorized();

    var accepted = await new ZaloMemberPresenceSignalStore(db)
        .RecordAsync(incoming, cancellationToken);
    return Results.Accepted(value: new { accepted });
});

app.MapGet("/api/internal/zyro-mapping/groups", async (
    HttpContext httpContext,
    IConfiguration configuration,
    ZaloIntegrationService service,
    CancellationToken cancellationToken) =>
{
    if (!ZyroMappingInternalAuthorization.IsConfigured(configuration))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!ZyroMappingInternalAuthorization.IsAuthorized(httpContext, configuration))
        return Results.Unauthorized();

    httpContext.Response.Headers.CacheControl = "no-store";
    return (await service.GetGroupsForZyroMappingAsync(cancellationToken)).ToHttpResult();
});

app.MapGet("/api/internal/zyro-mapping/groups/{connectionId}/{groupId}/members", async (
    HttpContext httpContext,
    string connectionId,
    string groupId,
    IConfiguration configuration,
    ZaloIntegrationService service,
    CancellationToken cancellationToken) =>
{
    if (!ZyroMappingInternalAuthorization.IsConfigured(configuration))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!ZyroMappingInternalAuthorization.IsAuthorized(httpContext, configuration))
        return Results.Unauthorized();

    httpContext.Response.Headers.CacheControl = "no-store";
    return (await service.GetMemberDirectoryForZyroMappingAsync(
        connectionId,
        groupId,
        cancellationToken)).ToHttpResult();
});

app.MapPost("/api/internal/zyro-mapping/profiles/sync", async (
    HttpContext httpContext,
    ZyroMappedProfileSyncRequest request,
    IConfiguration configuration,
    ZaloIntegrationService service,
    CancellationToken cancellationToken) =>
{
    if (!ZyroMappingInternalAuthorization.IsConfigured(configuration))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!ZyroMappingInternalAuthorization.IsAuthorized(httpContext, configuration))
        return Results.Unauthorized();

    httpContext.Response.Headers.CacheControl = "no-store";
    return (await service.SyncZyroMappedProfilesAsync(
        request.ConnectionId,
        request.GroupId,
        request.Profiles ?? [],
        cancellationToken)).ToHttpResult();
});

app.MapPost("/api/internal/zalo/poll-events", (
    HttpContext httpContext,
    ZaloPollBoardEvent incoming,
    ZaloPollEventQueue queue,
    IConfiguration configuration) =>
{
    var expectedKey = configuration["Zalo:WebhookKey"]
        ?? configuration["Zalo:BridgeInternalKey"]
        ?? "development-zalo-bridge-key";
    if (httpContext.Request.Headers["x-zalo-bridge-key"] != expectedKey)
        return Results.Unauthorized();
    return Results.Accepted(value: new { accepted = queue.TryEnqueue(incoming) });
});

var zalo = app.MapGroup("/api/zalo").RequireAuthorization();
zalo.MapPost("/connections/qr", async (
    HttpContext httpContext,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.StartQrLoginAsync(userId)).ToHttpResult();
});
zalo.MapGet("/connections/qr/{loginId}", async (
    HttpContext httpContext,
    string loginId,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetQrLoginStatusAsync(userId, loginId)).ToHttpResult();
});
zalo.MapGet("/connections", async (
    HttpContext httpContext,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetConnectionsAsync(userId)).ToHttpResult();
});
zalo.MapGet("/connections/{connectionId}/groups", async (
    HttpContext httpContext,
    string connectionId,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetGroupsAsync(userId, connectionId)).ToHttpResult();
});
zalo.MapGet("/connections/{connectionId}/groups/{groupId}/birthdays", async (
    HttpContext httpContext,
    string connectionId,
    string groupId,
    int? month,
    VolleyDraftDbContext db,
    ZaloIntegrationService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var targetMonth = month ?? DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Month;
    if (targetMonth is < 1 or > 12)
        return Results.BadRequest(new { error = "month must be between 1 and 12" });

    var ownsConnection = await db.ZaloConnections
        .AsNoTracking()
        .AnyAsync(item => item.Id == connectionId && item.AdminUserId == userId, cancellationToken);
    if (!ownsConnection) return Results.NotFound();

    var members = await db.ZaloGroupMembers
        .AsNoTracking()
        .Where(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.IsCurrentMember)
        .Select(item => new { item.ZaloUserId, item.DisplayName })
        .ToListAsync(cancellationToken);

    static string NormalizeZaloId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    var profiles = members.Count == 0
        ? Array.Empty<BridgeUserProfile>()
        : await service.GetUserProfilesAsync(
            connectionId,
            members.Select(item => item.ZaloUserId).ToArray(),
            cancellationToken);
    var nameById = members.ToDictionary(
        item => NormalizeZaloId(item.ZaloUserId),
        item => item.DisplayName,
        StringComparer.Ordinal);
    var birthdays = profiles
        .Select(profile => ZaloMemberIntelligenceBotService.TryParseBirthday(profile, out var birthday)
            ? new
            {
                UserId = NormalizeZaloId(profile.ZaloUserId),
                DisplayName = nameById.GetValueOrDefault(NormalizeZaloId(profile.ZaloUserId)) ?? profile.DisplayName,
                Birthday = birthday,
                profile.IsFriend
            }
            : null)
        .Where(item => item is not null && item.Birthday.Month == targetMonth)
        .Select(item => item!)
        .OrderBy(item => item.Birthday.Day)
        .ThenBy(item => item.DisplayName)
        .Select(item => new
        {
            date = item.Birthday.ToString("dd/MM"),
            displayName = item.DisplayName,
            isFriend = item.IsFriend
        })
        .ToArray();

    return Results.Ok(new
    {
        month = targetMonth,
        currentMemberCount = members.Count,
        visibleProfileCount = profiles.Count,
        birthdays
    });
});
zalo.MapPost("/greeting-tests/preview", async (
    HttpContext httpContext,
    ZaloGreetingTestPreviewRequest request,
    ZaloGreetingTestService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.PreviewAsync(userId, request, cancellationToken)).ToHttpResult();
});
zalo.MapPost("/greeting-tests/send", async (
    HttpContext httpContext,
    ZaloGreetingTestSendRequest request,
    ZaloGreetingTestService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.SendAsync(userId, request, cancellationToken)).ToHttpResult();
});
zalo.MapGet("/auto-session-groups", async (
    HttpContext httpContext,
    ZaloAutoSessionSettingsService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetGroupsAsync(userId, cancellationToken)).ToHttpResult();
});
zalo.MapPost("/auto-session-groups", async (
    HttpContext httpContext,
    CreateZaloAutoSessionGroupRequest request,
    ZaloAutoSessionSettingsService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CreateGroupAsync(userId, request, cancellationToken)).ToHttpResult();
});
zalo.MapPut("/auto-session-groups/{trackedGroupId}", async (
    HttpContext httpContext,
    string trackedGroupId,
    UpdateZaloAutoSessionGroupRequest request,
    ZaloAutoSessionSettingsService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateGroupAsync(userId, trackedGroupId, request, cancellationToken)).ToHttpResult();
});

zalo.MapGet("/bot-images", async (
    HttpContext httpContext,
    ZaloBotImageService service,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetAssetsAsync(userId, GetPublicOrigin(httpContext, configuration), cancellationToken)).ToHttpResult();
});
zalo.MapPost("/bot-images", async (
    HttpContext httpContext,
    IFormFile file,
    ZaloBotImageService service,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UploadAsync(userId, file, GetPublicOrigin(httpContext, configuration), cancellationToken)).ToHttpResult();
}).DisableAntiforgery();

app.MapGet("/api/public/bot-images/{assetId}", async (
    string assetId,
    ZaloBotImageService service,
    CancellationToken cancellationToken) =>
{
    var image = await service.GetPublicAsync(assetId, cancellationToken);
    return image is null
        ? Results.NotFound()
        : Results.File(image.Data, image.ContentType, enableRangeProcessing: true);
});

app.MapGet("/api/public/sessions/{sessionId}/team-card.png", async (
    string sessionId,
    ZaloTeamCardService service,
    CancellationToken cancellationToken) =>
{
    var image = await service.GenerateAsync(sessionId, cancellationToken);
    return image is null ? Results.NotFound() : Results.File(image.Data, image.ContentType);
});

var publicSessions = app.MapGroup("/api/public/sessions");
publicSessions.MapGet("/", async (
    int? page,
    int? pageSize,
    SessionDraftService service) =>
    (await service.GetPublicSessionsAsync(page ?? 1, pageSize ?? 3)).ToHttpResult());
publicSessions.MapGet("/{sessionId}/players", async (
    string sessionId,
    int? page,
    int? pageSize,
    SessionDraftService service) =>
    (await service.GetPublicPlayersAsync(sessionId, page ?? 1, pageSize ?? 6)).ToHttpResult());
publicSessions.MapGet("/{sessionId}/captains", async (
    string sessionId,
    SessionDraftService service) =>
    (await service.GetPublicCaptainsAsync(sessionId)).ToHttpResult());
publicSessions.MapPost("/{sessionId}/captains/auto-select", async (
    string sessionId,
    SessionDraftService service) =>
    (await service.AutoSelectPublicCaptainsAsync(sessionId)).ToHttpResult());
publicSessions.MapPost("/{sessionId}/start-draft", async (
    string sessionId,
    SessionDraftService service) =>
    (await service.StartPublicDraftAsync(sessionId)).ToHttpResult());
publicSessions.MapGet("/{sessionId}/draft-state", async (
    string sessionId,
    SessionDraftService service) =>
    (await service.GetPublicDraftStateAsync(sessionId)).ToHttpResult());
publicSessions.MapPost("/{sessionId}/blind-bags/{bagId}/prepare-reveal", async (
    string sessionId,
    string bagId,
    SessionDraftService service) =>
    (await service.PreparePublicBagRevealAsync(sessionId, bagId)).ToHttpResult());
publicSessions.MapPost("/{sessionId}/blind-bags/{bagId}/open", async (
    string sessionId,
    string bagId,
    SessionDraftService service) =>
    (await service.OpenPublicBagAsync(sessionId, bagId)).ToHttpResult());

var settlementAccess = app.MapGroup("/api/settlement-access");
settlementAccess.MapPost("/challenges", async (
    HttpContext httpContext,
    CreateSettlementAccessChallengeRequest request,
    SettlementAccessService service,
    CancellationToken cancellationToken) =>
{
    var requesterKey = ClientIpAccessPolicy.GetClientIp(httpContext)?.ToString() ?? "unknown";
    return (await service.CreateChallengeAsync(request.SessionId, requesterKey, cancellationToken)).ToHttpResult();
});
settlementAccess.MapPost("/challenges/redeem", async (
    HttpContext httpContext,
    RedeemSettlementAccessChallengeRequest request,
    SettlementAccessService service,
    CancellationToken cancellationToken) =>
{
    var redemption = await service.RedeemChallengeAsync(request.ChallengeId, request.RedemptionSecret, cancellationToken);
    if (!redemption.IsSuccess)
        return Results.Json(new { message = redemption.Error }, statusCode: redemption.StatusCode);

    if (!string.IsNullOrWhiteSpace(redemption.Value!.AccessToken) && redemption.Value.AccessExpiresAt is { } expiresAt)
    {
        SetSettlementAccessCookie(httpContext, redemption.Value.AccessToken, expiresAt);
    }
    return Results.Ok(redemption.Value.Response);
});
settlementAccess.MapGet("/sessions/{sessionId}/status", async (
    HttpContext httpContext,
    string sessionId,
    SettlementAccessService service,
    CancellationToken cancellationToken) =>
    (await service.GetStatusAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken)).ToHttpResult());
settlementAccess.MapGet("/sessions/{sessionId}/settlement", async (
    HttpContext httpContext,
    string sessionId,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.GetAsync(access.Value!.AdminUserId, sessionId, cancellationToken)).ToHttpResult();
});
settlementAccess.MapPost("/sessions/{sessionId}/settlement", async (
    HttpContext httpContext,
    string sessionId,
    CreateSettlementRequest request,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.CreateAsync(access.Value!.AdminUserId, sessionId, request, cancellationToken)).ToHttpResult();
});
settlementAccess.MapPut("/sessions/{sessionId}/settlement", async (
    HttpContext httpContext,
    string sessionId,
    UpdateSettlementRequest request,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.UpdateAsync(access.Value!.AdminUserId, sessionId, request, cancellationToken)).ToHttpResult();
});
settlementAccess.MapPut("/sessions/{sessionId}/settlement/fund-coverage", async (
    HttpContext httpContext,
    string sessionId,
    UpdateSettlementFundCoverageRequest request,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.UpdateFundCoverageAsync(access.Value!.AdminUserId, sessionId, request, cancellationToken)).ToHttpResult();
});
settlementAccess.MapPut("/sessions/{sessionId}/settlement/entries/{entryId}", async (
    HttpContext httpContext,
    string sessionId,
    string entryId,
    UpdateSettlementEntryRequest request,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.UpdateEntryAsync(access.Value!.AdminUserId, sessionId, entryId, request, cancellationToken)).ToHttpResult();
});
settlementAccess.MapPost("/sessions/{sessionId}/settlement/actions", async (
    HttpContext httpContext,
    string sessionId,
    CreateSettlementActionRequest request,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.QueueActionAsync(access.Value!.AdminUserId, sessionId, request, cancellationToken)).ToHttpResult();
});
settlementAccess.MapGet("/sessions/{sessionId}/settlement/export", async (
    HttpContext httpContext,
    string sessionId,
    SettlementAccessService accessService,
    SettlementExcelExporter exporter,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    if (!access.IsSuccess)
        return Results.Json(new { message = access.Error }, statusCode: access.StatusCode);

    var exported = await exporter.ExportAsync(access.Value!.AdminUserId, sessionId, cancellationToken);
    return exported.IsSuccess
        ? Results.File(
            exported.Value!.Content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            exported.Value.FileName)
        : Results.Json(new { message = exported.Error }, statusCode: exported.StatusCode);
});
settlementAccess.MapPost("/sessions/{sessionId}/settlement/payment-qr", async (
    HttpContext httpContext,
    string sessionId,
    IFormFile file,
    SettlementAccessService accessService,
    SettlementService settlementService,
    ZaloBotImageService imageService,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    if (!access.IsSuccess)
        return Results.Json(new { message = access.Error }, statusCode: access.StatusCode);

    var existing = await settlementService.GetAsync(access.Value!.AdminUserId, sessionId, cancellationToken);
    if (!existing.IsSuccess) return existing.ToHttpResult();

    var uploaded = await imageService.UploadAsync(
        access.Value.AdminUserId,
        file,
        GetPublicOrigin(httpContext, configuration),
        cancellationToken);
    if (!uploaded.IsSuccess) return uploaded.ToHttpResult();

    return (await settlementService.UpdatePaymentQrAsync(
        access.Value.AdminUserId,
        sessionId,
        uploaded.Value!.Url,
        cancellationToken)).ToHttpResult();
}).DisableAntiforgery();
settlementAccess.MapPost("/sessions/{sessionId}/settlement/close", async (
    HttpContext httpContext,
    string sessionId,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.SetClosedAsync(access.Value!.AdminUserId, sessionId, true, cancellationToken)).ToHttpResult();
});
settlementAccess.MapPost("/sessions/{sessionId}/settlement/open", async (
    HttpContext httpContext,
    string sessionId,
    SettlementAccessService accessService,
    SettlementService settlementService,
    CancellationToken cancellationToken) =>
{
    var access = await accessService.AuthorizeAsync(GetSettlementAccessToken(httpContext), sessionId, cancellationToken);
    return !access.IsSuccess
        ? Results.Json(new { message = access.Error }, statusCode: access.StatusCode)
        : (await settlementService.SetClosedAsync(access.Value!.AdminUserId, sessionId, false, cancellationToken)).ToHttpResult();
});

var sessions = app.MapGroup("/api/sessions").RequireAuthorization();
sessions.MapPost("/", async (
    HttpContext httpContext,
    CreateSessionRequest request,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CreateSessionAsync(userId, request)).ToHttpResult();
});
sessions.MapGet("/", async (
    HttpContext httpContext,
    int? page,
    int? pageSize,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetSessionsAsync(userId, page ?? 1, pageSize ?? 5)).ToHttpResult();
});
sessions.MapGet("/{sessionId}", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetSessionAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPut("/{sessionId}", async (
    HttpContext httpContext,
    string sessionId,
    UpdateSessionRequest request,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateSessionAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapDelete("/{sessionId}", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.DeleteSessionAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/zalo-group", async (
    HttpContext httpContext,
    string sessionId,
    LinkZaloGroupRequest request,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.LinkGroupAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/zalo-bot-settings", async (
    HttpContext httpContext,
    string sessionId,
    ZaloBotService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetSettingsAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/zalo-bot-settings", async (
    HttpContext httpContext,
    string sessionId,
    UpdateZaloBotSettingsRequest request,
    ZaloBotService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateSettingsAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/zalo-overbook", async (
    HttpContext httpContext,
    string sessionId,
    ZaloOverbookService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetStatusAsync(userId, sessionId, cancellationToken)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/zalo-overbook", async (
    HttpContext httpContext,
    string sessionId,
    UpdateZaloOverbookSettingsRequest request,
    ZaloOverbookService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateSettingsAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/zalo-overbook/copy", async (
    HttpContext httpContext,
    string sessionId,
    CopyZaloOverbookSettingsRequest request,
    ZaloOverbookService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CopySettingsAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/zalo-overbook/confirm", async (
    HttpContext httpContext,
    string sessionId,
    ConfirmZaloOverbookTargetsRequest request,
    ZaloOverbookService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.ConfirmTargetsScopedAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/zalo-overbook/confirm-and-remind", async (
    HttpContext httpContext,
    string sessionId,
    ConfirmZaloOverbookTargetsRequest request,
    ZaloOverbookService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.ConfirmTargetsAndRemindNowAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/zalo-bot-rules", async (
    HttpContext httpContext,
    string sessionId,
    int? page,
    int? pageSize,
    ZaloBotService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetLearnedRulesAsync(userId, sessionId, page ?? 1, pageSize ?? 5)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/zalo-bot-operators", async (
    HttpContext httpContext,
    string sessionId,
    ZaloBotService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetOperatorCandidatesAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/zalo-bot-rules/{ruleId}", async (
    HttpContext httpContext,
    string sessionId,
    string ruleId,
    ReviewZaloBotLearnedRuleRequest request,
    ZaloBotService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.ReviewLearnedRuleAsync(userId, sessionId, ruleId, request)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/zalo-polls", async (
    HttpContext httpContext,
    string sessionId,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetPollsAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/zalo-import-preview", async (
    HttpContext httpContext,
    string sessionId,
    CreateZaloImportPreviewRequest request,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CreateImportPreviewAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/zalo-import", async (
    HttpContext httpContext,
    string sessionId,
    ConfirmZaloPollImportRequest request,
    ZaloIntegrationService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.ConfirmImportAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/member-intelligence/sync", async (
    HttpContext httpContext,
    string sessionId,
    StartZaloActivitySyncRequest request,
    ZaloActivityBackfillCoordinator service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.QueueForSessionAsync(
            userId,
            sessionId,
            request.Full,
            cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/member-intelligence/sync", async (
    HttpContext httpContext,
    string sessionId,
    ZaloActivityBackfillCoordinator service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetStatusForSessionAsync(
            userId,
            sessionId,
            cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/member-intelligence/desktop-history", async (
    HttpContext httpContext,
    string sessionId,
    ImportZaloDesktopHistoryRequest request,
    ZaloActivityBackfillCoordinator service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.ImportDesktopHistoryForSessionAsync(
            userId,
            sessionId,
            request,
            cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/member-intelligence/members", async (
    HttpContext httpContext,
    string sessionId,
    string? filter,
    string? period,
    int? page,
    int? pageSize,
    ZaloMemberActivityService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    if (userId is null) return Results.Unauthorized();
    var parsedFilter = filter?.Trim().ToLowerInvariant() switch
    {
        "no-vote" or "novote" => ZaloMemberActivityFilter.NoVote,
        "no-message" or "nomessage" => ZaloMemberActivityFilter.NoMessage,
        "inactive" => ZaloMemberActivityFilter.Inactive,
        "at-risk" or "atrisk" => ZaloMemberActivityFilter.AtRisk,
        _ => ZaloMemberActivityFilter.All
    };
    return (await service.QueryForSessionAsync(
        userId,
        sessionId,
        ZaloMemberActivityService.ParsePeriod(period),
        parsedFilter,
        page ?? 1,
        pageSize ?? 10,
        cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/member-intelligence/engagement", async (
    HttpContext httpContext,
    string sessionId,
    string? period,
    ZaloMemberActivityService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetGroupEngagementForSessionAsync(
            userId,
            sessionId,
            ZaloMemberActivityService.ParsePeriod(period),
            cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/waitlist", async (
    HttpContext httpContext,
    string sessionId,
    SessionWaitlistService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetAsync(userId, sessionId, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/waitlist", async (
    HttpContext httpContext,
    string sessionId,
    AddSessionWaitlistEntryRequest request,
    SessionWaitlistService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    if (userId is null) return Results.Unauthorized();
    var owned = await service.GetAsync(userId, sessionId, cancellationToken);
    return !owned.IsSuccess
        ? owned.ToHttpResult()
        : (await service.JoinAsync(sessionId, request.ZaloUserId, request.DisplayName,
            userId, "Admin website", cancellationToken)).ToHttpResult();
});
sessions.MapDelete("/{sessionId}/waitlist/{zaloUserId}", async (
    HttpContext httpContext,
    string sessionId,
    string zaloUserId,
    SessionWaitlistService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    if (userId is null) return Results.Unauthorized();
    var owned = await service.GetAsync(userId, sessionId, cancellationToken);
    return !owned.IsSuccess
        ? owned.ToHttpResult()
        : (await service.LeaveAsync(sessionId, zaloUserId, userId, "Admin website", cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/action-history", async (
    HttpContext httpContext,
    string sessionId,
    int? count,
    ZaloBotActionHistoryService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetHistoryAsync(userId, sessionId, count ?? 10, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/action-history/{actionId}/undo", async (
    HttpContext httpContext,
    string sessionId,
    string actionId,
    ZaloBotActionHistoryService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UndoAsync(userId, sessionId, actionId, userId, cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/settlement", async (
    HttpContext httpContext,
    string sessionId,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetAsync(userId, sessionId, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/settlement", async (
    HttpContext httpContext,
    string sessionId,
    CreateSettlementRequest request,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CreateAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/settlement", async (
    HttpContext httpContext,
    string sessionId,
    UpdateSettlementRequest request,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/settlement/fund-coverage", async (
    HttpContext httpContext,
    string sessionId,
    UpdateSettlementFundCoverageRequest request,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateFundCoverageAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/settlement/entries/{entryId}", async (
    HttpContext httpContext,
    string sessionId,
    string entryId,
    UpdateSettlementEntryRequest request,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateEntryAsync(userId, sessionId, entryId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/settlement/actions", async (
    HttpContext httpContext,
    string sessionId,
    CreateSettlementActionRequest request,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.QueueActionAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/settlement/export", async (
    HttpContext httpContext,
    string sessionId,
    SettlementExcelExporter exporter,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    if (userId is null) return Results.Unauthorized();
    var exported = await exporter.ExportAsync(userId, sessionId, cancellationToken);
    return exported.IsSuccess
        ? Results.File(
            exported.Value!.Content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            exported.Value.FileName)
        : Results.Json(new { message = exported.Error }, statusCode: exported.StatusCode);
});
sessions.MapPost("/{sessionId}/settlement/payment-qr", async (
    HttpContext httpContext,
    string sessionId,
    IFormFile file,
    SettlementService settlementService,
    ZaloBotImageService imageService,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var existing = await settlementService.GetAsync(userId, sessionId, cancellationToken);
    if (!existing.IsSuccess) return existing.ToHttpResult();

    var uploaded = await imageService.UploadAsync(
        userId,
        file,
        GetPublicOrigin(httpContext, configuration),
        cancellationToken);
    if (!uploaded.IsSuccess) return uploaded.ToHttpResult();

    return (await settlementService.UpdatePaymentQrAsync(
        userId,
        sessionId,
        uploaded.Value!.Url,
        cancellationToken)).ToHttpResult();
}).DisableAntiforgery();
sessions.MapPost("/{sessionId}/settlement/close", async (
    HttpContext httpContext,
    string sessionId,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.SetClosedAsync(userId, sessionId, true, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/settlement/open", async (
    HttpContext httpContext,
    string sessionId,
    SettlementService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.SetClosedAsync(userId, sessionId, false, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/players", async (
    HttpContext httpContext,
    string sessionId,
    AddPlayerRequest request,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.AddPlayerAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/players", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetPlayersAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/players/{playerId}", async (
    HttpContext httpContext,
    string sessionId,
    string playerId,
    UpdatePlayerRequest request,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdatePlayerAsync(userId, sessionId, playerId, request)).ToHttpResult();
});
sessions.MapDelete("/{sessionId}/players/{playerId}", async (
    HttpContext httpContext,
    string sessionId,
    string playerId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.DeletePlayerAsync(userId, sessionId, playerId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/shared-slots", async (
    HttpContext httpContext,
    string sessionId,
    CreateSharedSlotRequest request,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CreateSharedSlotAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/shared-slots", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetSharedSlotsAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapDelete("/{sessionId}/shared-slots/{slotId}", async (
    HttpContext httpContext,
    string sessionId,
    string slotId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.DeleteSharedSlotAsync(userId, sessionId, slotId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/team-preferences", async (
    HttpContext httpContext,
    string sessionId,
    CreateTeamPreferenceGroupRequest request,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CreateTeamPreferenceGroupAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/team-preferences", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetTeamPreferenceGroupsAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapDelete("/{sessionId}/team-preferences/{groupId}", async (
    HttpContext httpContext,
    string sessionId,
    string groupId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.DeleteTeamPreferenceGroupAsync(userId, sessionId, groupId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/captains/auto-select", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.AutoSelectCaptainsAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/captains/manual", async (
    HttpContext httpContext,
    string sessionId,
    ManualCaptainsRequest request,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.SetManualCaptainsAsync(userId, sessionId, request)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/captains", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetCaptainsAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/start-draft", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.StartDraftAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/undo-last-pick", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UndoLastDraftPickAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/reset-draft", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.ResetDraftAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/draft-state", async (
    HttpContext httpContext,
    string sessionId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetDraftStateAsync(userId, sessionId)).ToHttpResult();
});
sessions.MapPut("/{sessionId}/draft-board", async (
    HttpContext httpContext,
    string sessionId,
    UpdateDraftBoardRequest request,
    DraftBoardService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.UpdateAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapGet("/{sessionId}/draft-snapshots", async (
    HttpContext httpContext,
    string sessionId,
    int? page,
    int? pageSize,
    DraftBoardService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetSnapshotsAsync(userId, sessionId, page ?? 1, pageSize ?? 6, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/draft-snapshots", async (
    HttpContext httpContext,
    string sessionId,
    CreateDraftSnapshotRequest request,
    DraftBoardService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.CreateSnapshotAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/draft-snapshots/{snapshotId}/restore", async (
    HttpContext httpContext,
    string sessionId,
    string snapshotId,
    RestoreDraftSnapshotRequest request,
    DraftBoardService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.RestoreSnapshotAsync(userId, sessionId, snapshotId, request, cancellationToken)).ToHttpResult();
});
sessions.MapDelete("/{sessionId}/draft-snapshots/{snapshotId}", async (
    HttpContext httpContext,
    string sessionId,
    string snapshotId,
    DraftBoardService service,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.DeleteSnapshotAsync(userId, sessionId, snapshotId, cancellationToken)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/blind-bags/{bagId}/prepare-reveal", async (
    HttpContext httpContext,
    string sessionId,
    string bagId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.PrepareBagRevealAsync(userId, sessionId, bagId)).ToHttpResult();
});
sessions.MapPost("/{sessionId}/blind-bags/{bagId}/open", async (
    HttpContext httpContext,
    string sessionId,
    string bagId,
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.OpenBagAsync(userId, sessionId, bagId)).ToHttpResult();
});

app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var indexPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
    if (!File.Exists(indexPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath);
});

app.Run();

static string? NormalizePostgresConnectionString(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return connectionString;
    }

    if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
    {
        return connectionString;
    }

    var userInfo = uri.UserInfo.Split(':', 2);
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? string.Empty),
        Password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? string.Empty),
        SslMode = SslMode.Require
    };

    return builder.ConnectionString;
}

static string GetPublicOrigin(HttpContext httpContext, IConfiguration configuration)
{
    var configuredOrigin = configuration["Public:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(configuredOrigin))
    {
        return configuredOrigin.TrimEnd('/');
    }

    var forwardedProto = httpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
    var forwardedHost = httpContext.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
    var scheme = string.IsNullOrWhiteSpace(forwardedProto) ? httpContext.Request.Scheme : forwardedProto;
    var host = string.IsNullOrWhiteSpace(forwardedHost) ? httpContext.Request.Host.Value : forwardedHost;
    return $"{scheme}://{host}".TrimEnd('/');
}

static string? GetSettlementAccessToken(HttpContext httpContext) =>
    httpContext.Request.Cookies.TryGetValue(SettlementAccessService.CookieName, out var token)
        ? token
        : null;

static void SetSettlementAccessCookie(HttpContext httpContext, string token, DateTimeOffset expiresAt)
{
    var forwardedProto = httpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
    var secure = httpContext.Request.IsHttps || string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase);
    httpContext.Response.Cookies.Append(
        SettlementAccessService.CookieName,
        token,
        new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/api/settlement-access",
            Expires = expiresAt,
            IsEssential = true
        });
}
