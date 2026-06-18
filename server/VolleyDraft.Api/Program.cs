using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
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
            .AllowAnyMethod();
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

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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
    SessionDraftService service) =>
{
    var userId = httpContext.User.GetUserId();
    return userId is null
        ? Results.Unauthorized()
        : (await service.GetSessionsAsync(userId)).ToHttpResult();
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
