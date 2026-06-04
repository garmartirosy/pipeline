# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build

# Run (HTTP on :5156, HTTPS on :7284)
dotnet run --project DBMonitor/DBMonitor.csproj

# Run with Docker
docker build -t dbmonitor .
docker run -p 8080:8080 dbmonitor

# EF Core migrations
dotnet ef migrations add <MigrationName> --project DBMonitor
dotnet ef database update --project DBMonitor
```

There is no test project currently.

## Architecture

**ASP.NET Core 8.0 MVC** app with SQL Server via Entity Framework Core. Authentication is handled by ASP.NET Core Identity (auto-scaffolded Razor Pages under `Areas/Identity/`).

**Data flow:**
- MVC routes → `Controllers/` → Razor Views in `Views/`
- Identity routes → `Areas/Identity/Pages/` (Razor Pages, not MVC)
- Both paths use `Data/ApplicationDbContext` (inherits `IdentityDbContext`) as the single EF Core entry point

**Frontend:** Bootstrap 5 + jQuery loaded via `wwwroot/lib/`; custom styles/scripts in `wwwroot/css/site.css` and `wwwroot/js/site.js`. Layout is `Views/Shared/_Layout.cshtml`.

**Configuration:**
- Connection string defaults to LocalDB in `appsettings.json`; override via User Secrets (ID: `aspnet-DBMonitor-6ee26c51-a0a3-4b84-a74c-9cac22db2c6e`) for local dev
- Docker exposes ports 8080 (HTTP) and 8081 (HTTPS)

**Current state:** The project is a scaffold baseline — Identity and the MVC skeleton are wired up, but no domain-specific entities, controllers, or business logic exist yet beyond `HomeController`.
