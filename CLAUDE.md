# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet run                    # starts on http://localhost:5000
dotnet build                  # compile only
```

No tests or linter are configured. No solution file — single project only.

When restarting, kill the running process first (`taskkill` the PID on port 5000) since the exe will be locked.

## Prerequisites

- .NET 8 SDK
- SQL Server 2016+ with `VIEW SERVER STATE` granted to the connection login
- Connection string in `appsettings.json` under `ConnectionStrings:SqlServer`

## Architecture

SqlMetro is a real-time SQL Server activity visualiser that renders database traffic as a London Underground-style metro map. Single ASP.NET Core minimal API project with a canvas-based SPA frontend.

### Backend (C#)

- **Program.cs** — Minimal API setup. Two endpoints: `GET /api/snapshot` (full state JSON polled by frontend) and `GET /api/health`.
- **Services/SqlServerService.cs** — Singleton service that runs 6 parallel DMV queries per poll cycle via Dapper, then assembles a `MetroSnapshot`. Includes blocking chain tree builder, deadlock cycle detector, and XE ring buffer deadlock parser.
- **Models/Models.cs** — All data classes in one file. Raw DMV rows (`ActiveRequest`, `SessionSummary`, `WaitStat`, `IdleBlocker`, `DeadlockRow`), metro domain objects (`MetroDatabase`, `MetroStation`, `MetroTrain`), blocking/deadlock models (`BlockingChain`, `DeadlockEvent`), and the API response (`MetroSnapshot`).

### Frontend

- **wwwroot/index.html** — Single-file SPA (~1100 lines, HTML + CSS + JS). Uses `<canvas>` for the metro map with tube-map routing (horizontal, vertical, 45° diagonal segments). No build step, no framework, no bundler.

### Key Frontend Systems

- **Tube map layout** — Routes defined as waypoint arrays with bends, spreading databases across the canvas
- **Train animation** — Speed proportional to query duration (fast queries zip, long queries crawl)
- **Idle session dots** — Dim dots drifting along each line for connected-but-sleeping sessions
- **Line thickness** — Rail width scales with session count (sqrt-scaled)
- **Blocking chain lines** — Red dashed lines between head blocker and victim trains
- **SQL syntax highlighting** — Keywords, strings, numbers, comments coloured in tooltips
- **Historical sparklines** — Rolling 30-poll SVG charts per database in the sidebar
- **Database filtering** — Click database rows to show/hide lines on the map
- **Active Queries tab** — Sortable list of running queries (by duration, reads, writes, SPID, DB)
- **Session drilldown** — Click a train or query row for full session details panel
- **Pannable canvas** — Click+drag to pan, double-click to reset

### Domain Mapping

Each **database** is a metro line (all databases shown, including idle). Each **object** (sproc/table) with active requests becomes a station. Each **active query** is a train — SELECTs move right-to-left, writes move left-to-right. Passenger count is log-scaled from logical reads.

### Configuration

All tunables are in `appsettings.json` under the `SqlMetro` section and bound to `SqlMetroOptions`: `PollIntervalSeconds`, `SlowQueryThresholdMs`, `WaitStatTopN`, `ExcludeSessions`.
