# Render Deployment

This repo includes:

- `render.yaml` for Render Blueprint deploys.
- `server/VolleyDraft.Api/Dockerfile` for the ASP.NET API.
- `.github/workflows/ci.yml` to build frontend, backend, and API Docker image on every push/PR to `main`.
- `.github/workflows/reminder-scheduler.yml` to wake Render and check Zalo reminders every 30 minutes.

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

## Keep Zalo Reminder Alive With GitHub Actions

The scheduler runs at minute `07` and `37` of every hour. It does not run continuously and does not call AI. Each run sends one short request to the API; the API wakes the Zalo bridge, refreshes linked polls when needed, and sends only reminders that are due.

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

No scheduler environment variable is required on `draft-zalo-bridge`. The API wakes/reconciles that service through the existing `Zalo__BridgeBaseUrl` and matching bridge internal key.

### 2. Configure GitHub

In the GitHub repository, open **Settings > Secrets and variables > Actions**:

1. Under **Secrets**, create `SCHEDULER_KEY`. Its value must exactly match Render `Scheduler__Key`.
2. Under **Variables**, create `SCHEDULER_URL` with the full API endpoint, for example:

```text
https://<your-api-service>.onrender.com/api/internal/scheduler/tick
```

Copy the public URL shown inside the Render service running ASP.NET (`Draft`/`volley-draft-api`). Use the API domain, not `volley-draft` static frontend and not `draft-zalo-bridge`. Also make sure `Zalo__WebhookUrl` uses that same API domain plus `/api/internal/zalo/events`.

### 3. Test Once

Open **Actions > Reminder scheduler > Run workflow**. A successful warm request returns HTTP `202`. If a sleeping Render instance needs more than 45 seconds, the action exits successfully after requesting a wake-up; the API's startup reminder worker performs the check after boot.

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

### GitHub Actions Minute Budget

The workflow has no checkout/install/build step and `timeout-minutes: 1`. At two runs per hour, the upper bound is 48 runs/day, or 1,488 one-minute jobs in a 31-day month. This fits a 2,000-minute private-repository allowance, leaving about 512 minutes for CI. Scheduled runs can be delayed by GitHub or Render cold starts, so reminders are best-effort with roughly 30-minute precision. GitHub resets the included quota on its normal monthly billing cycle; it is not a rolling 30-day timer.

Avoid changing this workflow to run every 5 minutes: that would use up to 8,928 one-minute jobs in a 31-day month.

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
