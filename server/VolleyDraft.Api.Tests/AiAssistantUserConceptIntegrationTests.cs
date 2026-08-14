using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class AiAssistantUserConceptIntegrationTests
{
    [Fact]
    public async Task Answer_captures_explicit_self_concept_and_sends_it_back_as_user_memory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        string? requestBody = null;
        var handler = new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"Ừ, mình nhớ preference đó trong group này.\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();
        var service = new AiAssistantService(
            new HttpClient(handler),
            configuration,
            NullLogger<AiAssistantService>.Instance,
            db);

        var result = await service.AnswerAsync(new ZaloAiContext(
            "group-1",
            new ZaloAiSender("long", "Long"),
            "tui hay đánh T6",
            [],
            [],
            null,
            [],
            new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.FromHours(7))));

        Assert.Equal("Ừ, mình nhớ preference đó trong group này.", result);
        Assert.NotNull(requestBody);
        Assert.Contains("userConcepts", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session_availability", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T6", requestBody, StringComparison.OrdinalIgnoreCase);

        var stored = await new ZaloUserConceptStore(db).LoadActiveAsync("group-1", "long");
        var concept = Assert.Single(stored);
        Assert.Equal("Preference", concept.ConceptType);
        Assert.Equal("session_availability", concept.Key);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responseFactory(request);
    }
}
