using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VolleyDraft.Api.Services;

public sealed class ZaloTrendMusicRenderDispatcher(IHttpClientFactory httpClientFactory)
{
    public async Task DispatchAsync(
        string githubToken,
        string githubRepository,
        string githubWorkflow,
        string githubRef,
        string accountId,
        string groupId,
        string connectionId,
        string localDate,
        string idempotencyKey,
        string candidatesJson,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("TrendMusicGitHub");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{githubRepository}/actions/workflows/{Uri.EscapeDataString(githubWorkflow)}/dispatches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("VolleyDraft-TrendMusic/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                @ref = githubRef,
                inputs = new
                {
                    account_id = accountId,
                    group_id = groupId,
                    connection_id = connectionId,
                    local_date = localDate,
                    idempotency_key = idempotencyKey,
                    candidates_json = candidatesJson
                }
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode == StatusCodes.Status204NoContent) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"GitHub workflow dispatch failed HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}",
            null,
            response.StatusCode);
    }
}
