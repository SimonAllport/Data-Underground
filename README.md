# SQL Metro — Live SQL Server Traffic Visualisation

A real-time SQL Server activity visualiser that renders database traffic as a London Underground-style metro map. Each **database** is a metro line, each **object** is a station, and each **active query** is a train moving along the line.

---

## Visual Guide

| Visual | Meaning | Source DMV |
|---|---|---|
| Metro line (colour) | Database | `sys.dm_exec_sessions` |
| Line thickness | Session count (thicker = busier) | `sys.dm_exec_sessions` |
| Station circle | Object (sproc / table / view) with active requests | `sys.dm_exec_requests` |
| Hollow ← train | SELECT — reads move right-to-left | `sys.dm_exec_requests` |
| Solid → train | INSERT / UPDATE / DELETE / EXEC — writes move left-to-right | `sys.dm_exec_requests` |
| Train speed | Based on query duration (fast = short query, slow = long-running) | `total_elapsed_time` |
| Passenger dots on train | Logical reads (log-scaled, 1–12) | `logical_reads` |
| People boarding ↑ | Data being read from the station | — |
| People alighting ↓ | Data being written to the station | — |
| Dim drifting dots on line | Idle sessions (connected but not executing) | `sys.dm_exec_sessions` |
| Platform fill bar | Queue depth (concurrent sessions on that object) | — |
| Red pulsing ring | Bottleneck (queue ≥ 70% OR blocked sessions) | — |
| ⚡ on train | Slow query (exceeds threshold) | `total_elapsed_time` |
| 🔒 hexagonal border | Head blocker (session blocking others) | `blocking_session_id` |
| Red dashed lines | Blocking chain (head blocker → victims) | `blocking_session_id` |
| 💀 skull icon | Deadlock victim | `system_health` XE session |
| Orange dashed border | Session involved in deadlock cycle | — |
| Sparkline charts | Session count trend (last 30 polls) | — |

---

## Requirements

- **.NET 8 SDK** (or later)
- **SQL Server 2016+** (any edition including Express / Developer)
- The login needs `VIEW SERVER STATE` permission
- A modern browser (Chrome, Edge, Firefox) — uses `<canvas>` rendering

---

## Quick Start

### 1. Clone and configure

```bash
git clone <repo-url>
cd SqlMetro/SqlMetro
```

Edit `appsettings.json` with your SQL Server connection:

**Windows Authentication:**
```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=MYSERVER;Database=master;Integrated Security=true;TrustServerCertificate=true;"
  }
}
```

**SQL Authentication:**
```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=MYSERVER;Database=master;User Id=myuser;Password=mypassword;TrustServerCertificate=true;"
  }
}
```

**Named instance:**
```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=MYSERVER\\SQLEXPRESS;Database=master;Integrated Security=true;TrustServerCertificate=true;"
  }
}
```

### 2. Grant permissions

Connect to SQL Server as `sa` or a sysadmin and run:

```sql
-- For SQL auth
GRANT VIEW SERVER STATE TO [myuser];

-- For Windows auth
GRANT VIEW SERVER STATE TO [DOMAIN\username];
```

### 3. Build and run

```bash
dotnet run
```

### 4. Open the browser

Navigate to **http://127.0.0.1:5000/index.html**

> **Note:** `localhost` may not resolve on some Windows configurations. Use `127.0.0.1` if you get "page not found".

---

## Configuration

All settings are in `appsettings.json` under the `SqlMetro` section:

| Key | Default | Description |
|---|---|---|
| `PollIntervalSeconds` | `3` | How often the backend polls the SQL Server DMVs |
| `SlowQueryThresholdMs` | `500` | Queries exceeding this get the ⚡ slow indicator |
| `WaitStatTopN` | `15` | Number of wait types shown in the Waits panel |
| `ExcludeSessions` | `["sa", "NT AUTHORITY\\SYSTEM"]` | Login names to exclude from the display |

Example:
```json
{
  "SqlMetro": {
    "PollIntervalSeconds": 2,
    "SlowQueryThresholdMs": 1000,
    "WaitStatTopN": 20,
    "ExcludeSessions": ["sa", "NT AUTHORITY\\SYSTEM", "NT SERVICE\\SQLTELEMETRY"]
  }
}
```

---

## UI Controls

### Toolbar

| Button | Action |
|---|---|
| ⏸ PAUSE / ▶ RESUME | Pause or resume polling |
| 🔥 BOTTLENECKS | Show only congested stations |
| 👁 READS ONLY | Show only SELECT trains |
| ✏ WRITES ONLY | Show only write trains |
| 🔒 BLOCKING | Show only sessions involved in blocking chains or deadlocks |
| ↺ RESET VIEW | Clear all filters and reset pan position |
| POLL slider | Adjust polling interval (2–15 seconds) |

### Header Pills

| Pill | Meaning |
|---|---|
| Blue — `N sessions` | Total user sessions across all databases |
| Green — `N active` | Currently executing requests |
| Red — `N blocked` | Sessions blocked by another session |
| Yellow — `N bottlenecks` | Stations with queue ≥ 70% capacity |
| Red — `N deadlocks` | Deadlocks detected in last 5 minutes |

### Map Interaction

| Action | Effect |
|---|---|
| **Click + drag** | Pan the map |
| **Double-click** | Reset pan to origin |
| **Hover station** | Tooltip: queue depth, blocked count, reads/writes, avg elapsed, top wait |
| **Hover train** | Tooltip: session ID, login, host, SQL text (syntax-highlighted), elapsed, wait type, blocking status |
| **Click train** | Select session → opens Session drilldown panel |

### Side Panel Tabs

| Tab | Content |
|---|---|
| **Databases** | List of all databases with session counts, activity badges, sparkline history charts. Click to filter the map. **Show All** / **Clear** buttons for bulk filter. |
| **Queries** | Sortable list of all active queries. Sort by Duration, Reads, Writes, SPID, or Database. Shows SPID, database, elapsed time, operation type, wait type, read/write counts, SQL preview, and blocking/deadlock flags. Click a row to drill down. |
| **Session** | Full session drilldown: database, status, elapsed, wait type, logical reads, writes, login, host, object name, blocking chain info, full SQL text with syntax highlighting. |
| **Waits** | Top wait types from `dm_os_wait_stats` with category badges (Lock, IO, CPU, Memory, Network, Log, HA) and bar charts. |

The panel can be collapsed/expanded using the ◀/▶ toggle button.

---

## API Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/api/snapshot` | GET | Full state JSON — databases, trains, stations, waits, blocking chains, deadlocks |
| `/api/health` | GET | Connection test — returns server name or error |

### Snapshot Response Structure

```
{
  isConnected, serverName, collectedAt, pollIntervalMs,
  databases: [{ id, name, colour, totalSessions, activeSessions, blockedSessions, stations: [...] }],
  allDatabases: [...],     // includes idle databases
  trains: [{ id, sessionId, databaseId, op, isBlocked, blockedBy, isHeadBlocker,
             blockingChainDepth, isDeadlockVictim, isInDeadlock, sqlText, ... }],
  topWaits: [{ waitType, category, waitTimeSec, avgWaitMs }],
  blockingChains: [{ headBlockerSessionId, chainDepth, totalVictims, victimSessionIds }],
  recentDeadlocks: [{ detectedAtMs, victimSessionIds, involvedSessionIds, summary }],
  totalSessions, totalActiveRequests, totalBlocked, deadlockCount
}
```

---

## Architecture

```
SqlMetro/
├── Program.cs                     Minimal API: /api/snapshot, /api/health, static files
├── appsettings.json               Connection string + SqlMetro config
├── Models/
│   └── Models.cs                  All data classes:
│                                    - Raw DMV rows: ActiveRequest, SessionSummary, WaitStat
│                                    - Metro domain: MetroDatabase, MetroStation, MetroTrain
│                                    - Blocking: BlockingChain, DeadlockEvent
│                                    - API response: MetroSnapshot
├── Services/
│   └── SqlServerService.cs        Singleton service:
│                                    - 6 parallel DMV queries per poll cycle
│                                    - Blocking chain tree builder
│                                    - Deadlock cycle detection (runtime)
│                                    - XE ring buffer deadlock parser
│                                    - Snapshot assembly
└── wwwroot/
    └── index.html                 Single-file SPA (HTML + CSS + JS):
                                     - Canvas rendering (tube map layout)
                                     - Train animation with speed by duration
                                     - Idle session dots
                                     - Blocking chain line rendering
                                     - SQL syntax highlighting
                                     - Sparkline charts
                                     - Sortable query list
                                     - Session drilldown panel
```

### Data Flow

```
SQL Server DMVs ──► SqlServerService (6 parallel queries)
                          │
                    ┌─────┴─────┐
                    │ BuildSnapshot │  Assembles databases, stations, trains,
                    │               │  blocking chains, deadlock events
                    └─────┬─────┘
                          │
                    /api/snapshot (JSON)
                          │
                    Frontend (polls every N seconds)
                          │
                    ┌─────┴─────┐
                    │  Canvas    │  Metro map, trains, idle dots,
                    │  render    │  blocking lines, sparklines
                    └───────────┘
```

### DMV Queries

| Query | DMV | Purpose |
|---|---|---|
| Active Requests | `dm_exec_requests` + `dm_exec_sessions` + `dm_exec_sql_text` | Running queries with SQL text |
| Session Summary | `dm_exec_sessions` + `dm_exec_requests` | Per-database session counts |
| Wait Stats | `dm_os_wait_stats` | Server-wide wait type breakdown |
| All Databases | `sys.databases` | Include idle databases in the list |
| Idle Blockers | `dm_exec_requests` + `dm_exec_sessions` + `dm_exec_connections` | Sessions blocking others but not running a query |
| Deadlocks | `dm_xe_session_targets` (system_health ring buffer) | Recent deadlock XML events |

---

## Troubleshooting

| Problem | Solution |
|---|---|
| "Cannot reach SQL Server" | Check connection string in `appsettings.json`. Ensure SQL Server is running and network accessible. |
| `localhost:5000` not found | Use `http://127.0.0.1:5000/index.html` instead. |
| No databases shown | Ensure the login has `VIEW SERVER STATE`. Run: `GRANT VIEW SERVER STATE TO [login];` |
| Only `master` shows activity | This is normal for an idle server — most sessions are connected but sleeping. Idle sessions appear as dim dots on each line. |
| Deadlock count always 0 | The `system_health` XE session must be running (it is by default). Deadlocks are only shown from the last 5 minutes. |
| Blocking chains not visible | Blocking only appears when sessions are actively blocked. Generate test blocking with two competing transactions. |
| Error: "Invalid column name" | Ensure SQL Server 2016+. Some DMV columns were added in later versions. |

### Generating Test Activity

To see the visualisation in action on a quiet server, open two SSMS windows:

**Window 1 (create a blocker):**
```sql
BEGIN TRAN;
UPDATE SomeTable SET Col1 = Col1 WHERE 1=0;  -- holds lock
WAITFOR DELAY '00:01:00';
ROLLBACK;
```

**Window 2 (get blocked):**
```sql
SELECT * FROM SomeTable WITH (UPDLOCK);
```

This will create a blocking chain visible on the map with the 🔒 head blocker indicator and red dashed chain lines.

---

## Build Commands

```bash
dotnet build       # compile only
dotnet run         # build + run on http://localhost:5000
```

No tests, linter, or solution file — single project only.
