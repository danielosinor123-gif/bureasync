# BureauSync — Deploy via GitHub → Vercel (free *.vercel.app)

This repo is ready for split deployment: **Vercel Static** (frontend) + **Render/Fly/Azure** (API + DB).

## 1) Push to GitHub
```powershell
# Already initialized locally (commit 4f1bf9b)
# Create empty repo on https://github.com/new (no README) named `bureasync`
git remote add origin https://github.com/<YOUR-USERNAME>/bureasync.git
git branch -M main
git push -u origin main
```

## 2) Frontend → Vercel (free domain)

1. https://vercel.com → **Add New Project** → Import `bureasync` from GitHub
2. Framework Preset: **Other** (static)
   - Build Command: *(leave empty)*
   - Output Directory: `wwwroot`
   - Install Command: *(empty)*
3. **Deploy** → you get `https://bureasync-xxxx.vercel.app` (free, auto TLS, CDN)
   - Vercel reads `vercel.json:2` `outputDirectory: wwwroot` and `headers` for security.
   - Static files: `wwwroot/index.html` (38 lines login), `wwwroot/app.html` (73 lines dashboard), `wwwroot/config.js` (API base).

## 3) API → Render (free) — required because Vercel cannot run .NET 8 + SQLite persistently

### Option A: Render (recommended)
1. https://dashboard.render.com → **New Web Service** → Connect same GitHub repo `bureasync`
2. Runtime: **Docker** (uses `Dockerfile:1` `mcr.microsoft.com/dotnet/aspnet:8.0` → `dotnet publish`)
3. Environment:
   ```
   ASPNETCORE_ENVIRONMENT=Production
   ASPNETCORE_URLS=http://+:8080
   DISABLE_HTTPS_REDIRECT=1
   Jwt__Key=<32+ random chars, e.g. openssl rand -base64 48>
   Jwt__Issuer=BureauSync
   Jwt__Audience=BureauSync.Api
   Jwt__AccessTokenMinutes=15
   ConnectionStrings__BureauSync=Data Source=bureausync.db   # SQLite ephemeral for demo
   # For prod: Server=tcp:<azure>.database.windows.net,1433;Database=BureauSync;User Id=...;Password=...;TrustServerCertificate=True
   DatabaseProvider=Sqlite   # or SqlServer for external DB
   FrontendUrl=https://bureasync-xxxx.vercel.app
   ```
4. Deploy → Render gives `https://bureasync-api.onrender.com`

### Option B: Fly.io / Azure App Service — same env vars, use `Dockerfile`.

## 4) Wire frontend → API

In Vercel dashboard or locally edit `wwwroot/config.js:5`:

```js
window.API_BASE = "https://bureasync-api.onrender.com";
```

- For local dev leave `""` (same-origin `http://localhost:5000`).
- After changing, commit & push → Vercel auto redeploys static.
- CORS already allowed via `Program.cs:18` `AddCors("frontend")` reading `FrontendUrl` env var — set `FrontendUrl=https://bureasync-xxxx.vercel.app` on API host.

## 5) Verify

- `https://bureasync-xxxx.vercel.app/health` → via API proxy or direct `https://bureasync-api.onrender.com/health` → `{"status":"ok"}`
- `https://bureasync-xxxx.vercel.app` → login `admin@example.com` / `longpassword123456` → bootstrap if DB empty → **Lender Directory** shows 4 seeded lenders with aliases (SWIFT/CBN/LEI/CustomId)
- Upload `Pitch-Demo-All-Checks.csv` (17 rows) to `LND-001` → `6 Ready · 1 Review · 10 Rejected`

## 6) Notes

- `bureausync-dev.db` is `.gitignore:10` — not pushed. On Render free, SQLite is ephemeral (resets on deploy). For persistent prod use `DatabaseProvider=SqlServer` + external DB and run `dotnet ef migrations add InitialCreate && dotnet ef database update`.
- `Program.cs:25` `UseHttpsRedirection` is auto-disabled when `DISABLE_HTTPS_REDIRECT=1` (required behind Vercel/Render proxy).
- `wwwroot/index.html` minimal login → `localStorage bs_token` → `wwwroot/app.html` dashboard. Root `index.html` (480 lines) is legacy, not deployed (only `wwwroot` is `outputDirectory`).
- Vercel Hobby: `5MB` upload limit matches `Safety:MaxUploadBytes:5242880` (`appsettings.json:1`).

## 7) Custom domain (optional)

Vercel → Settings → Domains → add `bureasync.com` → free TLS. Keep `*.vercel.app` as fallback.
