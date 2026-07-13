# Render Deployment

This repo includes:

- `render.yaml` for Render Blueprint deploys.
- `server/VolleyDraft.Api/Dockerfile` for the ASP.NET API.
- `.github/workflows/ci.yml` to build frontend, backend, and API Docker image on every push/PR to `main`.
- `.github/workflows/reminder-scheduler-v2.yml` to wake Render and check Zalo reminders every 15 minutes.

## Deploy With Blueprint

1. Push this repository to GitHub.
2. In Render, choose **New > Blueprint**.
3. Select this repository.
4. Render will create:
   - `volley-draft-api`
   - `volley-draft-web`
   - `volley-draft-db`
5. When Render prompts for unsynced env vars:
   - `VITE_API_BASE_URL`: set to the API public URL, for example `https://volley-draft-api.onrender.com`.
   - `Cors__Origins__0`: set to the frontend public URL, for example `https://volley-draft-web.onrender.com`.

## Keep Zalo Reminder Alive

The Zalo bridge pings the API health endpoint every 8 minutes while at least one listener is active. The API already reconciles its active bridge listeners, so the two Render services recover each other and the API reminder worker can keep checking due schedules. This is enabled by default in production and can be controlled with `ZALO_BRIDGE_API_KEEP_ALIVE` and `ZALO_BRIDGE_API_KEEP_ALIVE_MINUTES` on the bridge.

GitHub Actions remains a second wake-up path. Its workflow runs at minutes `01`, `16`, `31`, and `46` in the `Asia/Ho_Chi_Minh` timezone. Each run wakes both services and queues one scheduler cycle. GitHub documents scheduled workflows as best-effort: a schedule can be delayed or dropped before a runner is created. A due reminder may use one AI call to make its wording natural when `ZaloBot__AiStyleEnabled=true`; provider failure falls back to the factual template.

### 1. Configure Render API

Open the API service (`Draft`/`volley-draft-api`) in Render, choose **Environment > Edit**, then add:

```text
Scheduler__Key=<a-long-random-secret>
Scheduler__PollRefreshMinutes=20
Scheduler__RetryMinutes=10
Scheduler__UrgentWindowHours=1
Scheduler__UrgentDelayMinutes=0
```

Use a new random value for `Scheduler__Key`. Do not reuse the AI key, JWT key, Zalo credential key, or bridge key. Save and wait for the API to redeploy.

No scheduler secret is required on `draft-zalo-bridge`. The API wakes/reconciles that service through the existing `Zalo__BridgeBaseUrl` and matching bridge internal key. The Blueprint also sets these optional bridge values:

```text
ZALO_BRIDGE_API_KEEP_ALIVE=true
ZALO_BRIDGE_API_KEEP_ALIVE_MINUTES=8
```

Existing manually-created Render services do not need these values because production defaults to enabled/eight minutes. Add them only when you want the setting to be explicit or changed.

### 2. Configure GitHub

In the GitHub repository, open **Settings > Secrets and variables > Actions**:

1. Under **Secrets**, create `SCHEDULER_KEY`. Its value must exactly match Render `Scheduler__Key`.
2. Under **Variables**, create `SCHEDULER_URL` with the full API endpoint, for example:

```text
https://<your-api-service>.onrender.com/api/internal/scheduler/tick
```

Copy the public URL shown inside the Render service running ASP.NET (`Draft`/`volley-draft-api`). Use the API domain, not `volley-draft` static frontend and not `draft-zalo-bridge`. Also make sure `Zalo__WebhookUrl` uses that same API domain plus `/api/internal/zalo/events`.

### 3. Test Once

Open **Actions > Reminder scheduler > Run workflow**. A successful run wakes the bridge and API, then the scheduler endpoint returns HTTP `202`. If a sleeping Render instance cold-starts slowly, the workflow retries health checks and the scheduler request.

The workflow must be committed to the repository's default branch before GitHub runs its cron schedule.

Then test in Zalo with an authorized account (group owner, deputy, or configured operator):

```text
@bot cứ 6 tiếng nhắc nếu còn thiếu slot
@bot cứ 30 phút nhắc T6 nếu còn thiếu
@bot xem lịch nhắc
@bot nhắc ngay
@bot tắt nhắc CN
```

Without a day/session selector, the schedule is saved for all upcoming sessions in that group. On every cycle the bot chooses the nearest upcoming session that is still missing slots. Full sessions are skipped. If a full poll later loses a vote, automatic poll refresh makes the session eligible again, including close to match time.

### GitHub Actions Schedule And Minute Budget

At four runs per hour, a private repository can consume up to 2,976 rounded runner-minutes in a 31-day month. This repository is public, so standard GitHub-hosted runners are not charged against the private-repository minute allowance. Scheduled events can still be delayed or dropped by GitHub before the workflow starts; manual success only proves the workflow body and Render endpoints are valid.

Avoid changing the production workflow to every 5 minutes. The active-listener keep-alive handles Render sleep, while the 15-minute workflow is only a recovery path.

If GitHub shows no run whose event is `schedule` after several expected windows, use a dedicated HTTP scheduler such as cron-job.org instead of repeatedly editing the YAML. Configure one POST job to `SCHEDULER_URL` every 15 minutes and add the custom header `x-scheduler-key` with the same `SCHEDULER_KEY`. The service supports custom methods/headers and execution history, which makes missing ticks visible.

## Local Dev

Frontend dev server proxies `/api` to `http://127.0.0.1:5030`.

Run API:

```powershell
dotnet run --project server/VolleyDraft.Api/VolleyDraft.Api.csproj
```

Run frontend:

```powershell
npm install
npm run dev
```

## Notes

- The API accepts both Npgsql key/value connection strings and Render/Postgres URL strings.
- The API creates tables on startup with `EnsureCreatedAsync()`.
- For production, use PostgreSQL instead of SQLite.
