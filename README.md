# Shredded Together API

The .NET 10 API serves six Sofia clubs, member events, term pricing, enrollment, account updates and class reservations. The companion frontend is [GymWebApp](https://github.com/MarioHristov/GymWebApp).

## Run both repositories

Check out the matching frontend and API revisions into sibling directories named GymWebApp and GymWebAppApi. From GymWebAppApi in PowerShell, run:

```powershell
$env:FrontendRoot = (Resolve-Path ../GymWebApp).Path
dotnet run --project GymWebApi
```

Open http://localhost:5266. The API and frontend share an origin and an HTTP-only session cookie. On macOS or Linux use `FrontendRoot="$(cd ../GymWebApp && pwd)" dotnet run --project GymWebApi`. The development SQLite database is created on the first run and survives restarts.

## Data and operation

- Edit `GymWebApi/Clubhouse/catalog.json` to change the demo addresses, hours, trainers, class rules, events and prices. Class rule IDs identify recurring sessions and should remain stable after people reserve them. Times are local to Europe/Sofia.
- Prices use USD. Three-month and annual rates use the original advertised monthly equivalents. A qualifying member receives either 20% for age or 10% for student status after term pricing. Birth date and student status are self-reported in this version.
- Enrollment is pay-at-reception. Confirmation records the amount due and grants access for the term. Renewals start when the previous club term ends. Coaching packs last twelve months. Reservations consume a seat and, for coaching, a credit. Cancellation before the start releases both.
- The profile history contains enrollments for the signed-in account. A new account starts with an empty history.
- Production SQL Server can use the existing Identity user table and password hashes. Supply `ConnectionStrings__GymWebApiContextConnection` through the host. First startup adds four workflow tables alongside the existing schema. Use an account with schema creation permissions for initialization. Historical transactions in the original scaffold have no member reference and are not assigned to anyone automatically.
- Without a SQL Server connection, SQLite uses `clubhouse.db` in the project directory or the App Service persistent data directory. Override with `ConnectionStrings__ClubSqlite` on durable storage. Persist the ASP.NET Data Protection key ring across restarts and instances. Set `DataProtectionKeyPath` when a custom key directory is needed. Use SQL Server for multiple application instances.
- The frontend proxies `/api` to Azure in production. Use HTTPS for the public site. Match the frontend rewrite destination to the App Service hostname and configure the host for .NET 10.

## Workflow check

Run `python tests/workflow.py http://localhost:5266` against a disposable development database. The check creates test accounts and enrollments. It verifies authorization, discounts, duplicate orders, reservations, coaching credits, cancellation and the profile update flow. The GitHub workflow runs the same check on pull requests before deployment from master.
