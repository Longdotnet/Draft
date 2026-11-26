# ChatGPT plan text pilot

Scope: `GeneralChat` only. Draft's domain routing, structured extraction, context assembly,
existing provider pool, and Zalo delivery remain unchanged. No images, audio, tools, browser
automation, new services, or database migrations are included.

The adapter uses the public Responses API with `stream=true` and `store=false` and accepts
text only after a `response.completed` event. It reuses the inbound AI request budget and
existing provider pool for eligible quota/temporary infrastructure failures. Refusals and
permission errors are not reasons to retry with another model.

## Activation gate

This is a disabled-by-default, single-user integration pilot, not a completed OAuth product.
Remotely hosted apps need appropriate approval for ChatGPT plan usage. See:

- https://developers.openai.com/siwc/token-sharing-open-source
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations

Do not enable the pilot before the hosted integration is approved, its actual OAuth grant
and model availability are verified, and the selected user authorizes processing in the
selected test group. Group context may contain other members' messages; use a consented
test group. Do not reuse a COS tunnel key, browser session, another application's tokens,
or one person's subscription for other Zalo users. No OAuth refresh or multi-user login
is implemented in this pilot. An expired token is not used; the existing gateway remains
available. This is not a guarantee of 24/7 ChatGPT availability.

Once authorized, use protected runtime configuration, never commit credentials or paste
them in chat/logs. The following are configuration names, not sample credentials:

| Configuration key | Purpose |
| --- | --- |
| `Ai__ChatGptPlan__Enabled` | Defaults to false |
| `Ai__ChatGptPlan__HostedAccessApproved` | Defaults to false; records actual approval, not a substitute for it |
| `Ai__ChatGptPlan__ConnectionId` | Exact trusted Draft connection ID |
| `Ai__ChatGptPlan__GroupId` | Exact approved test group ID |
| `Ai__ChatGptPlan__SenderId` | Exact authorized Zalo sender ID |
| `Ai__ChatGptPlan__Model` | Slug from the authorized account's model catalog |
| `Ai__ChatGptPlan__AccessToken` | Protected, short-lived OAuth token for this integration |
| `Ai__ChatGptPlan__GrantedScopes` | Actual granted scopes, including `resource.invoke` and `chatgpt.tokens.use.direct` |
| `Ai__ChatGptPlan__AccessTokenExpiresAtUtc` | Actual token expiry in UTC, ISO 8601 |
| `Ai__ChatGptPlan__TimeoutSeconds` | Default 30, bounded 5–60 |
| `Ai__ChatGptPlan__QuotaProbeSeconds` | Local delay before checking quota again, default 300; not the plan reset time |

The existing configuration remains the source for fallback providers; this change does
not configure new billing or purchase API credits. Disable the pilot to roll back without
changing routing or data. Any future login, token renewal, or expansion to multiple users
must use the approved integration's authorization and consent flow.

## Verification

Run `dotnet test server/VolleyDraft.Api.Tests/VolleyDraft.Api.Tests.csproj --filter FullyQualifiedName~ChatGptPlanTextGatewayTests`.
Tests use fake HTTP responses and do not contact OpenAI. After approval, verify the exact
authorized sender receives a text answer, an unrelated sender cannot use that grant,
quota fallback is visible in logs, and legacy Draft commands still work. Until that live
test is completed, the adapter is not considered a working ChatGPT connection.
