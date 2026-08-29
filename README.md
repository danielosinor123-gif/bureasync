# BureauSync API

Production-oriented ASP.NET Core 8 baseline for a credit-bureau lender-submission quality gate. It validates CSV data before any downstream ingestion. It does not score borrowers, decide credit, connect to bureaux, or ingest real borrower data.

## Included
- C# / ASP.NET Core Minimal API
- EF Core and SQL Server persistence model
- PBKDF2 password hashes, JWT authentication, and BureauAdmin / BureauOperator / LenderSubmitter roles
- Lender, submission, record, issue, and audit-event entities
- Explainable CSV checks: required fields, loan/balance integrity, status logic, duplicate accounts, date formatting, and repayment-date warning
- Swagger in Development
- Built-in operator dashboard served at `/` for initial admin setup, login, lender setup, CSV upload, submission history, and row-level validation review
- A deliberate ingestion boundary: `/state` rejects `Ingested`.

## Run locally
1. Install .NET 8 SDK plus SQL Server/LocalDB.
2. From this folder set secrets, never commit them:
```powershell
dotnet user-secrets init
dotnet user-secrets set "Jwt:Key" "replace-with-a-random-32-plus-character-secret"
dotnet user-secrets set "ConnectionStrings:BureauSync" "Server=(localdb)\MSSQLLocalDB;Database=BureauSyncDev;Trusted_Connection=True;TrustServerCertificate=True"
```
3. Create and apply migrations, then run:
```powershell
dotnet restore
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate
dotnet ef database update
dotnet run
```

## API flow
1. `POST /api/auth/register` exactly once to create the initial `BureauAdmin`; subsequent anonymous registration is refused.
2. `POST /api/auth/login` to receive a JWT.
3. `POST /api/users` as a `BureauAdmin` to provision additional operators or lender submitters.
4. `POST /api/lenders`.
4. `POST /api/lenders/{id}/submissions` as multipart form data with a CSV.
5. `GET /api/submissions/{id}/records` for row-level validation results.

## Before real borrower data
Threat-model and penetration-test; add refresh-token rotation, MFA/SSO, secret vault, TLS termination, encryption at rest, malware scanning, rate limits, tenant authorization, monitoring, incident response, retention/deletion, data-processing agreements, and Nigerian legal/compliance review.

**Status:** release build verified successfully on August 17, 2026 (0 warnings, 0 errors). Dependency restore required a local network bridge because this host's Windows TLS stack could not connect to NuGet directly. A working SQL Server/LocalDB instance is still required before database-backed endpoint testing.
