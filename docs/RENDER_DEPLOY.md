# Render Deployment

This repo includes:

- `render.yaml` for Render Blueprint deploys.
- `server/VolleyDraft.Api/Dockerfile` for the ASP.NET API.
- `.github/workflows/ci.yml` to build frontend, backend, and API Docker image on every push/PR to `main`.

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
