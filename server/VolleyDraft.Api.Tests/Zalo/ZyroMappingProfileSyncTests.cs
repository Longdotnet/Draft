using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo;

public sealed class ZyroMappingProfileSyncTests
{
    [Fact]
    public async Task Group_listing_is_limited_to_the_configured_Draft_admin()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true, addSecondOwner: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var client = new ZaloBridgeClient(httpClient);
        var service = fixture.CreateService(client, fixture.Configuration);

        var result = await service.GetGroupsForZyroMappingAsync();

        Assert.True(result.IsSuccess, result.Error);
        var group = Assert.Single(result.Value!);
        Assert.Equal("connection", group.ConnectionId);
        Assert.Equal("ZYRO", group.GroupName);
    }

    [Fact]
    public async Task Group_listing_fails_closed_when_multiple_admins_exist_without_an_owner_setting()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: false, addSecondOwner: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var client = new ZaloBridgeClient(httpClient);
        var service = fixture.CreateService(client, fixture.Configuration);

        var result = await service.GetGroupsForZyroMappingAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Contains("ZyroMapping:AdminUserId", result.Error);
    }

    [Fact]
    public async Task Member_directory_can_be_previewed_but_incomplete_directory_cannot_sync_profiles()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers, isComplete: false));
        var client = new ZaloBridgeClient(httpClient);
        var service = fixture.CreateService(client, fixture.Configuration);

        var directory = await service.GetMemberDirectoryForZyroMappingAsync("connection", "group");
        var sync = await service.SyncZyroMappedProfilesAsync(
            "connection",
            "group",
            [new ZyroMappedProfileSyncItem("uid-1", PlayerGender.Male, PlayerRole.Defense, PlayerLevel.Average)]);

        Assert.True(directory.IsSuccess, directory.Error);
        Assert.False(directory.Value!.IsComplete);
        Assert.Equal(2, directory.Value.ExpectedMemberCount);
        Assert.Equal("Thanh Long", directory.Value.Members[0].DisplayName);
        Assert.Equal("Thanh Long", directory.Value.Members[0].ZaloName);
        Assert.False(sync.IsSuccess);
        Assert.Equal(StatusCodes.Status409Conflict, sync.StatusCode);
        Assert.Equal(0, await fixture.Db.PlayerProfiles.CountAsync());
    }

    [Fact]
    public async Task Member_directory_cannot_be_read_from_another_Draft_admin_account()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true, addSecondOwner: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var service = fixture.CreateService(new ZaloBridgeClient(httpClient), fixture.Configuration);

        var result = await service.GetMemberDirectoryForZyroMappingAsync("other-connection", "group");

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task Sync_rejects_UIDs_that_are_not_in_the_selected_group()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var client = new ZaloBridgeClient(httpClient);
        var service = fixture.CreateService(client, fixture.Configuration);

        var result = await service.SyncZyroMappedProfilesAsync(
            "connection",
            "group",
            [new ZyroMappedProfileSyncItem("uid-outside-group", PlayerGender.Male, PlayerRole.Defense, PlayerLevel.Average)]);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Empty(await fixture.Db.PlayerProfiles.ToListAsync());
    }

    [Fact]
    public async Task Sync_cannot_write_profiles_through_another_Draft_admin_connection()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true, addSecondOwner: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var service = fixture.CreateService(new ZaloBridgeClient(httpClient), fixture.Configuration);

        var result = await service.SyncZyroMappedProfilesAsync(
            "other-connection",
            "group",
            [new ZyroMappedProfileSyncItem("uid-1", PlayerGender.Male, PlayerRole.Defense, PlayerLevel.Average)]);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Empty(await fixture.Db.PlayerProfiles.ToListAsync());
    }

    [Fact]
    public async Task Sync_rejects_duplicate_UIDs_in_one_request()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var service = fixture.CreateService(new ZaloBridgeClient(httpClient), fixture.Configuration);

        var result = await service.SyncZyroMappedProfilesAsync(
            "connection",
            "group",
            [
                new ZyroMappedProfileSyncItem("uid-1", PlayerGender.Male, PlayerRole.Defense, PlayerLevel.Average),
                new ZyroMappedProfileSyncItem("uid-1", PlayerGender.Female, PlayerRole.Attack, PlayerLevel.Good)
            ]);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Empty(await fixture.Db.PlayerProfiles.ToListAsync());
    }

    [Fact]
    public async Task Sync_uses_verified_member_identity_fills_missing_values_and_preserves_existing_values()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true);
        fixture.Db.PlayerProfiles.AddRange(
            new PlayerProfile
            {
                Id = "profile-1",
                ZaloUserId = "uid-1",
                DisplayName = "",
                Gender = PlayerGender.Unknown,
                DefaultRole = PlayerRole.New,
                DefaultLevel = null
            },
            new PlayerProfile
            {
                Id = "profile-2",
                ZaloUserId = "uid-2",
                DisplayName = "Tên admin đã xác nhận",
                AvatarUrl = "existing-avatar",
                Gender = PlayerGender.Female,
                DefaultRole = PlayerRole.Attack,
                DefaultLevel = PlayerLevel.New
            });
        await fixture.Db.SaveChangesAsync();

        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var client = new ZaloBridgeClient(httpClient);
        var service = fixture.CreateService(client, fixture.Configuration);
        var result = await service.SyncZyroMappedProfilesAsync(
            "connection",
            "group",
            [
                new ZyroMappedProfileSyncItem("uid-1", PlayerGender.Male, PlayerRole.Defense, PlayerLevel.Average),
                new ZyroMappedProfileSyncItem("uid-2", PlayerGender.Male, PlayerRole.Setter, PlayerLevel.Average)
            ]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, result.Value!.Count);
        fixture.Db.ChangeTracker.Clear();

        var updated = await fixture.Db.PlayerProfiles.SingleAsync(profile => profile.ZaloUserId == "uid-1");
        Assert.Equal("Thanh Long", updated.DisplayName);
        Assert.Equal("avatar-1", updated.AvatarUrl);
        Assert.Equal(PlayerGender.Male, updated.Gender);
        Assert.NotNull(updated.GenderUpdatedAt);
        Assert.Equal("admin", updated.GenderUpdatedByUserId);
        Assert.Equal(PlayerRole.Defense, updated.DefaultRole);
        Assert.Equal(PlayerLevel.Average, updated.DefaultLevel);

        var preserved = await fixture.Db.PlayerProfiles.SingleAsync(profile => profile.ZaloUserId == "uid-2");
        Assert.Equal("Tên admin đã xác nhận", preserved.DisplayName);
        Assert.Equal("existing-avatar", preserved.AvatarUrl);
        Assert.Equal(PlayerGender.Female, preserved.Gender);
        Assert.Equal(PlayerRole.Attack, preserved.DefaultRole);
        Assert.Equal(PlayerLevel.New, preserved.DefaultLevel);

        Assert.Equal("updated_missing_fields", result.Value[0].Status);
        Assert.Equal("existing_values_preserved", result.Value[1].Status);
        Assert.Contains("displayName", result.Value[1].PreservedFields);
        Assert.Contains("avatar", result.Value[1].PreservedFields);
        Assert.Contains("gender", result.Value[1].PreservedFields);
        Assert.Contains("role", result.Value[1].PreservedFields);
        Assert.Contains("level", result.Value[1].PreservedFields);
    }

    [Fact]
    public async Task Sync_creates_a_profile_from_the_selected_group_member_not_client_supplied_name()
    {
        await using var fixture = await DbFixture.CreateAsync(configureAdmin: true);
        using var httpClient = CreateHttpClient(new MappingBridgeHandler(CompleteMembers));
        var client = new ZaloBridgeClient(httpClient);
        var service = fixture.CreateService(client, fixture.Configuration);

        var result = await service.SyncZyroMappedProfilesAsync(
            "connection",
            "group",
            [new ZyroMappedProfileSyncItem("uid-1", PlayerGender.Female, PlayerRole.FullStack, PlayerLevel.New)]);

        Assert.True(result.IsSuccess, result.Error);
        var profile = await fixture.Db.PlayerProfiles.SingleAsync(item => item.ZaloUserId == "uid-1");
        Assert.Equal("Thanh Long", profile.DisplayName);
        Assert.Equal("avatar-1", profile.AvatarUrl);
        Assert.Equal(PlayerGender.Female, profile.Gender);
        Assert.Equal(PlayerRole.FullStack, profile.DefaultRole);
        Assert.Equal(PlayerLevel.New, profile.DefaultLevel);
    }

    private static readonly IReadOnlyList<BridgeMember> CompleteMembers =
    [
        new BridgeMember("uid-1", "Thanh Long", "Thanh Long", "avatar-1"),
        new BridgeMember("uid-2", "Nguyễn An", "Nguyễn An", "avatar-2")
    ];

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new HttpClient(handler)
    {
        BaseAddress = new Uri("http://bridge.test/")
    };

    private sealed class MappingBridgeHandler(
        IReadOnlyList<BridgeMember> members,
        bool isComplete = true) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            object response = path switch
            {
                "/v1/groups" => new BridgeGroupsResponse([
                    new BridgeGroup("group", "ZYRO", "group-avatar", members.Count)
                ]),
                "/v1/groups/group/members" => new BridgeGroupMemberDirectory(
                    "group",
                    "ZYRO",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    members.Count,
                    isComplete,
                    members),
                _ => new { error = $"Unexpected path {path}" }
            };
            var statusCode = path is "/v1/groups" or "/v1/groups/group/members"
                ? HttpStatusCode.OK
                : HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class DbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ZaloCredentialProtector credentialProtector;
        public VolleyDraftDbContext Db { get; }
        public IConfiguration Configuration { get; }

        private DbFixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            IConfiguration configuration,
            ZaloCredentialProtector credentialProtector)
        {
            this.connection = connection;
            Db = db;
            Configuration = configuration;
            this.credentialProtector = credentialProtector;
        }

        public static async Task<DbFixture> CreateAsync(bool configureAdmin, bool addSecondOwner = false)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Zalo:CredentialEncryptionKey"] = "zyro-mapping-test-key"
            };
            if (configureAdmin) settings["ZyroMapping:AdminUserId"] = "admin";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var protector = new ZaloCredentialProtector(configuration);

            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);

            db.Users.Add(new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = "admin@test.local",
                PasswordHash = "hash"
            });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "connection",
                AdminUserId = "admin",
                AccountZaloId = "bot",
                DisplayName = "Bot",
                EncryptedCredentials = protector.Protect("{\"cookie\":\"test\"}"),
                Status = ZaloConnectionStatus.Connected
            });

            if (addSecondOwner)
            {
                db.Users.Add(new User
                {
                    Id = "other-admin",
                    DisplayName = "Other Admin",
                    Email = "other@test.local",
                    PasswordHash = "hash"
                });
                db.ZaloConnections.Add(new ZaloConnection
                {
                    Id = "other-connection",
                    AdminUserId = "other-admin",
                    AccountZaloId = "other-bot",
                    DisplayName = "Other Bot",
                    EncryptedCredentials = protector.Protect("{\"cookie\":\"other\"}"),
                    Status = ZaloConnectionStatus.Connected
                });
            }

            await db.SaveChangesAsync();
            return new DbFixture(connection, db, configuration, protector);
        }

        public ZaloIntegrationService CreateService(ZaloBridgeClient bridge, IConfiguration configuration) =>
            new(Db, bridge, credentialProtector, null!, null!, null!, configuration);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
