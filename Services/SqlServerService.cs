using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace SqlMetro;

public class SqlServerService(
    IConfiguration config,
    IOptions<SqlMetroOptions> opts,
    ILogger<SqlServerService> logger)
{
    private readonly string _connStr = config.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:SqlServer is not set in appsettings.json");

    private readonly SqlMetroOptions _opts = opts.Value;

    // ── Queries ───────────────────────────────────────────────────

    private const string ActiveRequestsSql = """
        SELECT
            r.session_id                                            AS SessionId,
            r.blocking_session_id                                   AS BlockingSessionId,
            DB_NAME(r.database_id)                                  AS DatabaseName,
            r.status                                                AS Status,
            r.command                                               AS Command,
            r.wait_type                                             AS WaitType,
            r.wait_time                                             AS WaitTimeMs,
            r.cpu_time                                              AS CpuTime,
            r.logical_reads                                         AS LogicalReads,
            r.writes                                                AS Writes,
            r.total_elapsed_time                                    AS ElapsedMs,
            s.program_name                                          AS ProgramName,
            s.login_name                                            AS LoginName,
            s.host_name                                             AS HostName,
            OBJECT_NAME(st.objectid, r.database_id)                   AS ObjectName,
            SUBSTRING(st.text,
                (r.statement_start_offset / 2) + 1,
                ((CASE r.statement_end_offset
                    WHEN -1 THEN DATALENGTH(st.text)
                    ELSE r.statement_end_offset
                  END - r.statement_start_offset) / 2) + 1)        AS SqlText
        FROM sys.dm_exec_requests r
        JOIN sys.dm_exec_sessions s
            ON s.session_id = r.session_id
        CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
        WHERE r.session_id <> @@SPID
          AND s.is_user_process = 1
        ORDER BY r.total_elapsed_time DESC;
        """;

    private const string SessionSummarySql = """
        SELECT
            DB_NAME(s.database_id)                                  AS DatabaseName,
            COUNT(*)                                                AS TotalSessions,
            SUM(CASE WHEN s.status = 'running'  THEN 1 ELSE 0 END) AS ActiveSessions,
            SUM(CASE WHEN r.blocking_session_id > 0 THEN 1 ELSE 0 END) AS BlockedSessions,
            SUM(s.logical_reads)                                    AS TotalLogicalReads,
            SUM(s.writes)                                           AS TotalWrites,
            SUM(s.cpu_time)                                         AS TotalCpuMs
        FROM sys.dm_exec_sessions s
        LEFT JOIN sys.dm_exec_requests r
            ON r.session_id = s.session_id
        WHERE s.is_user_process = 1
          AND s.database_id     > 0
          AND DB_NAME(s.database_id) IS NOT NULL
        GROUP BY s.database_id
        ORDER BY COUNT(*) DESC;
        """;

    private const string AllDatabasesSql = """
        SELECT name AS DatabaseName
        FROM sys.databases
        WHERE state_desc = 'ONLINE'
          AND database_id > 4
        ORDER BY name;
        """;

    private const string WaitStatsSql = """
        SELECT TOP (@topN)
            wait_type                                               AS WaitType,
            waiting_tasks_count                                     AS WaitingTasksCount,
            CAST(wait_time_ms / 1000.0 AS FLOAT)                   AS WaitTimeSec,
            CASE WHEN waiting_tasks_count > 0
                 THEN CAST(wait_time_ms AS FLOAT) / waiting_tasks_count
                 ELSE 0 END                                         AS AvgWaitMs
        FROM sys.dm_os_wait_stats
        WHERE wait_type NOT IN (
            'SLEEP_TASK','BROKER_TO_FLUSH','BROKER_TASK_STOP','CLR_AUTO_EVENT',
            'CLR_MANUAL_EVENT','DISPATCHER_QUEUE_SEMAPHORE','FT_IFTS_SCHEDULER_IDLE_WAIT',
            'HADR_FILESTREAM_IOMGR_IOCOMPLETION','HADR_WORK_QUEUE','HADR_CLUSAPI_CALL',
            'HADR_TIMER_TASK','HADR_TRANSPORT_DBTRANSPORT','ONDEMAND_TASK_QUEUE',
            'REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK',
            'SLEEP_DBSTARTUP','SLEEP_DBRECOVER','SLEEP_DBTASK','SLEEP_MASTERDBREADY',
            'SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP',
            'SLEEP_SYSTEMTASK','SLEEP_TEMPDBSTARTUP','SNI_HTTP_ACCEPT',
            'SP_SERVER_DIAGNOSTICS_SLEEP','SQLTRACE_BUFFER_FLUSH',
            'SQLTRACE_INCREMENTAL_FLUSH_SLEEP','WAITFOR','XE_DISPATCHER_WAIT',
            'XE_TIMER_EVENT','BROKER_EVENTHANDLER','CHECKPOINT_QUEUE',
            'DBMIRROR_EVENTS_QUEUE','SQLTRACE_WAIT_ENTRIES',
            'WAIT_XTP_OFFLINE_CKPT_NEW_LOG','DIRTY_PAGE_POLL','HADR_LOGCAPTURE_WAIT',
            'HADR_NOTIFICATION_DEQUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
            'QDS_ASYNC_QUEUE','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',
            'RESOURCE_GOVERNOR_IDLE','WAIT_XTP_HOST_WAIT','XE_DISPATCHER_JOIN'
        )
          AND wait_time_ms       > 0
          AND waiting_tasks_count > 0
        ORDER BY wait_time_ms DESC;
        """;

    // Idle head blockers: sessions that block others but have no active request
    private const string IdleBlockersSql = """
        SELECT DISTINCT
            r.blocking_session_id                                   AS SessionId,
            DB_NAME(s.database_id)                                  AS DatabaseName,
            s.login_name                                            AS LoginName,
            s.host_name                                             AS HostName,
            s.program_name                                          AS ProgramName,
            SUBSTRING(t.text, 1, 500)                               AS SqlText
        FROM sys.dm_exec_requests r
        JOIN sys.dm_exec_sessions s
            ON s.session_id = r.blocking_session_id
        LEFT JOIN sys.dm_exec_connections c
            ON c.session_id = s.session_id
        OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) t
        WHERE r.blocking_session_id > 0
          AND r.blocking_session_id NOT IN (
              SELECT session_id FROM sys.dm_exec_requests WHERE session_id <> @@SPID
          );
        """;

    // Recent deadlocks from system_health extended events
    private const string DeadlocksSql = """
        SELECT TOP 10
            xdr.value('@timestamp','datetime2')                     AS DetectedAt,
            xdr.query('.')                                          AS DeadlockXml
        FROM (
            SELECT CAST(target_data AS xml) AS TargetData
            FROM sys.dm_xe_session_targets st
            JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
            WHERE s.name = 'system_health'
              AND st.target_name = 'ring_buffer'
        ) AS d
        CROSS APPLY TargetData.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS xdr(xdr)
        ORDER BY 1 DESC;
        """;

    // ── Public API ────────────────────────────────────────────────

    public async Task<MetroSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            // Run all queries in parallel on separate connections
            var reqTask     = QueryAsync<ActiveRequest> (ActiveRequestsSql, null, ct);
            var sessTask    = QueryAsync<SessionSummary>(SessionSummarySql, null, ct);
            var waitTask    = QueryAsync<WaitStat>(WaitStatsSql,
                new { topN = _opts.WaitStatTopN }, ct);
            var allDbTask   = QueryAsync<DbName>(AllDatabasesSql, null, ct);
            var blockerTask = QueryAsync<IdleBlocker>(IdleBlockersSql, null, ct);
            var dlTask      = SafeQueryAsync<DeadlockRow>(DeadlocksSql, null, ct);

            await Task.WhenAll(reqTask, sessTask, waitTask, allDbTask, blockerTask, dlTask);

            var requests     = (await reqTask).ToList();
            var sessions     = (await sessTask).ToList();
            var waits        = (await waitTask).ToList();
            var allDbs       = (await allDbTask).ToList();
            var idleBlockers = (await blockerTask).ToList();
            var deadlockRows = (await dlTask).ToList();

            // Merge idle databases into sessions list
            var knownDbs = sessions.Select(s => s.DatabaseName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var db in allDbs)
            {
                if (!string.IsNullOrEmpty(db.DatabaseName) && !knownDbs.Contains(db.DatabaseName))
                    sessions.Add(new SessionSummary { DatabaseName = db.DatabaseName });
            }

            // Apply login exclusions
            if (_opts.ExcludeSessions.Length > 0)
            {
                requests = requests
                    .Where(r => !_opts.ExcludeSessions.Any(ex =>
                        string.Equals(r.LoginName, ex, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            // Categorise waits
            foreach (var w in waits)
                w.Category = CategoriseWait(w.WaitType);

            var serverName = await GetServerNameAsync(ct);
            return BuildSnapshot(requests, sessions, waits, serverName, idleBlockers, deadlockRows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query SQL Server");
            return new MetroSnapshot
            {
                CollectedAt  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsConnected  = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private async Task<string> GetServerNameAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<string>("SELECT @@SERVERNAME") ?? "SQL Server";
        }
        catch { return "SQL Server"; }
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(
        string sql, object? param, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        return await conn.QueryAsync<T>(sql, param);
    }

    /// <summary>Returns empty on failure (non-critical queries like deadlock XE).</summary>
    private async Task<IEnumerable<T>> SafeQueryAsync<T>(
        string sql, object? param, CancellationToken ct)
    {
        try { return await QueryAsync<T>(sql, param, ct); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Non-critical query failed");
            return [];
        }
    }

    // ── Snapshot builder ──────────────────────────────────────────

    private MetroSnapshot BuildSnapshot(
        List<ActiveRequest>  requests,
        List<SessionSummary> sessions,
        List<WaitStat>       waits,
        string               serverName,
        List<IdleBlocker>    idleBlockers,
        List<DeadlockRow>    deadlockRows)
    {
        var dbColours = new[]
        {
            ("#3B8BD4","#B5D4F4"), ("#1D9E75","#9FE1CB"), ("#D85A30","#F5C4B3"),
            ("#7F77DD","#AFA9EC"), ("#EF9F27","#FAC775"), ("#E24B4A","#F7C1C1"),
            ("#0F6E56","#5DCAA5"), ("#993556","#ED93B1"),
        };

        // Build all databases (for side panel list)
        var allDatabases = new List<MetroDatabase>();
        // Map databases (only those with sessions — shown on the metro map)
        var mapDatabases = new List<MetroDatabase>();
        int colIdx = 0;

        foreach (var sess in sessions)
        {
            if (string.IsNullOrEmpty(sess.DatabaseName)) continue;
            var dbReqs = requests
                .Where(r => string.Equals(r.DatabaseName, sess.DatabaseName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            var (col, lite) = dbColours[colIdx++ % dbColours.Length];
            var db = new MetroDatabase
            {
                Id              = sess.DatabaseName,
                Name            = sess.DatabaseName,
                Colour          = col,
                LightColour     = lite,
                TotalSessions   = sess.TotalSessions,
                ActiveSessions  = sess.ActiveSessions,
                BlockedSessions = sess.BlockedSessions,
                Stations        = sess.TotalSessions > 0
                    ? BuildStations(sess.DatabaseName, dbReqs)
                    : new List<MetroStation>(),
            };
            allDatabases.Add(db);
            if (sess.TotalSessions > 0)
                mapDatabases.Add(db);
        }

        // Also include databases that appear in requests but not sessions
        var knownDbs = allDatabases.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dbName in requests
            .Select(r => r.DatabaseName ?? "")
            .Where(n => n.Length > 0 && !knownDbs.Contains(n))
            .Distinct())
        {
            var dbReqs = requests
                .Where(r => string.Equals(r.DatabaseName, dbName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var (col, lite) = dbColours[colIdx++ % dbColours.Length];
            var db = new MetroDatabase
            {
                Id = dbName, Name = dbName, Colour = col, LightColour = lite,
                Stations = BuildStations(dbName, dbReqs),
            };
            allDatabases.Add(db);
            mapDatabases.Add(db);
        }

        var trains = requests.Select(BuildTrain).ToList();

        // Add idle head blockers as synthetic trains
        foreach (var ib in idleBlockers)
        {
            if (trains.Any(t => t.SessionId == ib.SessionId)) continue;
            trains.Add(new MetroTrain
            {
                Id         = $"s{ib.SessionId}",
                SessionId  = ib.SessionId,
                DatabaseId = ib.DatabaseName ?? "unknown",
                Op         = TrainOp.BATCH,
                Status     = "sleeping",
                LoginName  = ib.LoginName,
                HostName   = ib.HostName,
                SqlText    = ib.SqlText,
                IsBlocked  = false,
            });
        }

        // Build blocking chains
        var chains = BuildBlockingChains(requests, idleBlockers);
        var trainMap = trains.ToDictionary(t => t.SessionId);
        foreach (var chain in chains)
        {
            if (trainMap.TryGetValue(chain.HeadBlockerSessionId, out var hb))
            {
                hb.IsHeadBlocker = true;
                hb.BlockingChainDepth = 0;
            }
            foreach (var vid in chain.VictimSessionIds)
            {
                if (trainMap.TryGetValue(vid, out var vt))
                {
                    vt.HeadBlockerSessionId = chain.HeadBlockerSessionId;
                    vt.BlockingChainDepth = ComputeDepth(vid, requests);
                }
            }
        }

        // Detect deadlocks (mutual blocking in current requests)
        var deadlockSessions = DetectMutualBlocking(requests);
        foreach (var sid in deadlockSessions)
        {
            if (trainMap.TryGetValue(sid, out var dt))
                dt.IsInDeadlock = true;
        }

        // Parse XE deadlock events
        var recentDeadlocks = ParseDeadlockRows(deadlockRows);
        foreach (var dl in recentDeadlocks)
        {
            foreach (var vid in dl.VictimSessionIds)
            {
                if (trainMap.TryGetValue(vid, out var vt))
                    vt.IsDeadlockVictim = true;
            }
        }

        return new MetroSnapshot
        {
            CollectedAt         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PollIntervalMs      = _opts.PollIntervalSeconds * 1000L,
            IsConnected         = true,
            ServerName          = serverName,
            Databases           = mapDatabases,
            AllDatabases        = allDatabases,
            Trains              = trains,
            TopWaits            = waits,
            TotalSessions       = sessions.Sum(s => s.TotalSessions),
            TotalActiveRequests = requests.Count,
            TotalBlocked        = requests.Count(r => r.BlockingSessionId.GetValueOrDefault() > 0),
            BlockingChains      = chains,
            RecentDeadlocks     = recentDeadlocks,
            DeadlockCount       = recentDeadlocks.Count,
        };
    }

    private List<MetroStation> BuildStations(string dbName, List<ActiveRequest> requests)
    {
        // Always include a "connections" anchor station
        var anchor = new MetroStation
        {
            Id         = $"{dbName}.__conn",
            Name       = dbName,
            Type       = StationType.Database,
            DatabaseId = dbName,
            MaxQueue   = Math.Max(10, requests.Count + 5),
            Queue      = requests.Count,
        };

        if (!requests.Any()) return new List<MetroStation> { anchor };

        var objectStations = requests
            .Where(r => !string.IsNullOrWhiteSpace(r.ObjectName))
            .GroupBy(r => r.ObjectName!, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .Select(g =>
            {
                var reqs    = g.ToList();
                var blocked = reqs.Count(r => r.BlockingSessionId.GetValueOrDefault() > 0);
                var maxQ    = Math.Max(10, reqs.Count * 3);
                return new MetroStation
                {
                    Id              = $"{dbName}.{SanitiseId(g.Key)}",
                    Name            = g.Key.Length > 28 ? g.Key[..25] + "…" : g.Key,
                    Type            = InferStationType(g.Key, reqs),
                    DatabaseId      = dbName,
                    MaxQueue        = maxQ,
                    Queue           = reqs.Count,
                    ActiveRequests  = reqs.Count(r => r.Status == "running"),
                    BlockedRequests = blocked,
                    TotalReads      = reqs.Sum(r => r.LogicalReads),
                    TotalWrites     = reqs.Sum(r => r.Writes),
                    TopWaitType     = reqs
                        .Where(r => r.WaitType != null)
                        .GroupBy(r => r.WaitType!)
                        .OrderByDescending(g2 => g2.Count())
                        .FirstOrDefault()?.Key,
                    AvgElapsedMs    = reqs.Average(r => r.ElapsedMs),
                };
            });

        // Ad-hoc catch-all for unnamed requests
        var unnamed = requests.Where(r => string.IsNullOrWhiteSpace(r.ObjectName)).ToList();
        var adhocStation = unnamed.Count > 0
            ? new MetroStation
            {
                Id              = $"{dbName}.__adhoc",
                Name            = "ad-hoc queries",
                Type            = StationType.View,
                DatabaseId      = dbName,
                MaxQueue        = Math.Max(10, unnamed.Count + 3),
                Queue           = unnamed.Count,
                ActiveRequests  = unnamed.Count(r => r.Status == "running"),
                BlockedRequests = unnamed.Count(r => r.BlockingSessionId.GetValueOrDefault() > 0),
                TotalReads      = unnamed.Sum(r => r.LogicalReads),
                TotalWrites     = unnamed.Sum(r => r.Writes),
                AvgElapsedMs    = unnamed.Average(r => r.ElapsedMs),
            }
            : null;

        var result = new List<MetroStation> { anchor };
        result.AddRange(objectStations);
        if (adhocStation != null) result.Add(adhocStation);
        return result;
    }

    private MetroTrain BuildTrain(ActiveRequest r) => new()
    {
        Id           = $"s{r.SessionId}",
        SessionId    = r.SessionId,
        DatabaseId   = r.DatabaseName ?? "unknown",
        Op           = ParseOp(r.Command),
        IsBlocked    = r.BlockingSessionId.GetValueOrDefault() > 0,
        BlockedBy    = r.BlockingSessionId > 0 ? r.BlockingSessionId : null,
        ObjectName   = r.ObjectName,
        Passengers   = (int)Math.Clamp(Math.Log10(Math.Max(r.LogicalReads, 1) + 1) * 3, 1, 12),
        Status       = r.Status ?? "unknown",
        WaitType     = r.WaitType,
        ElapsedMs    = r.ElapsedMs,
        LogicalReads = r.LogicalReads,
        Writes       = r.Writes,
        LoginName    = r.LoginName,
        HostName     = r.HostName,
        SqlText      = r.SqlText?.Length > 2000 ? r.SqlText[..1997] + "…" : r.SqlText,
    };

    // ── Helpers ───────────────────────────────────────────────────

    private static TrainOp ParseOp(string? command) =>
        (command?.Trim().ToUpperInvariant() ?? "") switch
        {
            var c when c.StartsWith("SELECT")  => TrainOp.SELECT,
            var c when c.StartsWith("INSERT")  => TrainOp.INSERT,
            var c when c.StartsWith("UPDATE")  => TrainOp.UPDATE,
            var c when c.StartsWith("DELETE")  => TrainOp.DELETE,
            var c when c.StartsWith("EXECUTE") => TrainOp.EXECUTE,
            var c when c.StartsWith("EXEC")    => TrainOp.EXECUTE,
            _ => TrainOp.BATCH,
        };

    private static StationType InferStationType(string key, List<ActiveRequest> reqs)
    {
        var u = key.ToUpperInvariant();
        if (u.StartsWith("USP_") || u.StartsWith("SP_") || u.Contains("PROC"))
            return StationType.StoredProc;
        if (u.StartsWith("VW_") || u.StartsWith("V_") || u.Contains("VIEW"))
            return StationType.View;
        return StationType.Table;
    }

    private static string CategoriseWait(string w)
    {
        if (w.StartsWith("LCK_"))                                   return "Lock";
        if (w.StartsWith("PAGEIO") || w.Contains("_IO_"))           return "IO";
        if (w.StartsWith("NETWORK") || w.Contains("NET_"))          return "Network";
        if (w.Contains("MEMORY") || w.Contains("RESOURCE_SEMAPHORE")) return "Memory";
        if (w.StartsWith("SOS_SCHEDULER") || w.StartsWith("CXPACKET")
            || w.StartsWith("CXCONSUMER"))                          return "CPU";
        if (w.StartsWith("WRITELOG") || w.StartsWith("LOGBUFFER"))  return "Log";
        if (w.StartsWith("HADR_") || w.StartsWith("DBMIRROR"))      return "HA";
        return "Other";
    }

    private static string SanitiseId(string name) =>
        new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray())
            .ToLowerInvariant()
            [..Math.Min(24, name.Length)];

    // ── Blocking chain builder ──────────────────────────────────

    private static List<BlockingChain> BuildBlockingChains(
        List<ActiveRequest> requests, List<IdleBlocker> idleBlockers)
    {
        // Build graph: blocker -> list of victims
        var blocked = new Dictionary<int, List<int>>();
        var allBlockerIds = new HashSet<int>();
        var allBlockedIds = new HashSet<int>();

        foreach (var r in requests.Where(r => r.BlockingSessionId.GetValueOrDefault() > 0))
        {
            var bid = r.BlockingSessionId!.Value;
            allBlockerIds.Add(bid);
            allBlockedIds.Add(r.SessionId);
            if (!blocked.ContainsKey(bid))
                blocked[bid] = [];
            blocked[bid].Add(r.SessionId);
        }

        // Head blockers: block others but are not themselves blocked
        var headBlockers = allBlockerIds.Except(allBlockedIds).ToList();

        var chains = new List<BlockingChain>();
        foreach (var hb in headBlockers)
        {
            var victims = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(hb);
            int depth = 0;
            while (queue.Count > 0)
            {
                var count = queue.Count;
                for (int i = 0; i < count; i++)
                {
                    var sid = queue.Dequeue();
                    if (blocked.TryGetValue(sid, out var children))
                    {
                        foreach (var c in children)
                        {
                            victims.Add(c);
                            queue.Enqueue(c);
                        }
                    }
                }
                depth++;
            }

            if (victims.Count == 0) continue;

            var hbReq = requests.FirstOrDefault(r => r.SessionId == hb);
            var hbIdle = idleBlockers.FirstOrDefault(ib => ib.SessionId == hb);

            chains.Add(new BlockingChain
            {
                HeadBlockerSessionId = hb,
                HeadBlockerDatabase  = hbReq?.DatabaseName ?? hbIdle?.DatabaseName,
                HeadBlockerSqlText   = hbReq?.SqlText ?? hbIdle?.SqlText,
                ChainDepth           = depth,
                TotalVictims         = victims.Count,
                VictimSessionIds     = victims,
            });
        }
        return chains;
    }

    private static int ComputeDepth(int sessionId, List<ActiveRequest> requests)
    {
        int depth = 0;
        var current = sessionId;
        var visited = new HashSet<int>();
        while (visited.Add(current))
        {
            var req = requests.FirstOrDefault(r => r.SessionId == current);
            if (req?.BlockingSessionId.GetValueOrDefault() > 0)
            {
                depth++;
                current = req.BlockingSessionId!.Value;
            }
            else break;
        }
        return depth;
    }

    // ── Deadlock detection ──────────────────────────────────────

    private static HashSet<int> DetectMutualBlocking(List<ActiveRequest> requests)
    {
        // Build blocking graph and detect cycles
        var blockedBy = requests
            .Where(r => r.BlockingSessionId.GetValueOrDefault() > 0)
            .ToDictionary(r => r.SessionId, r => r.BlockingSessionId!.Value);

        var deadlocked = new HashSet<int>();
        foreach (var start in blockedBy.Keys)
        {
            var visited = new HashSet<int>();
            var current = start;
            while (blockedBy.TryGetValue(current, out var next))
            {
                if (!visited.Add(current))
                {
                    // Cycle detected — mark all in cycle
                    var cycle = current;
                    do
                    {
                        deadlocked.Add(cycle);
                        blockedBy.TryGetValue(cycle, out var n);
                        cycle = n;
                    } while (cycle != current && cycle != 0);
                    break;
                }
                current = next;
            }
        }
        return deadlocked;
    }

    private static List<DeadlockEvent> ParseDeadlockRows(List<DeadlockRow> rows)
    {
        var events = new List<DeadlockEvent>();
        var fiveMinAgo = DateTime.UtcNow.AddMinutes(-5);

        foreach (var row in rows)
        {
            if (row.DetectedAt < fiveMinAgo) continue;
            if (string.IsNullOrEmpty(row.DeadlockXml)) continue;

            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(row.DeadlockXml);
                var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

                var victims = doc.Descendants(ns + "victimProcess")
                    .Select(e => e.Attribute("id")?.Value)
                    .Where(id => id != null)
                    .ToList();

                var processes = doc.Descendants(ns + "process")
                    .Select(e => new { Id = e.Attribute("id")?.Value, Spid = e.Attribute("spid")?.Value })
                    .Where(p => p.Spid != null)
                    .ToList();

                // Map process IDs to SPIDs
                var idToSpid = processes.ToDictionary(p => p.Id ?? "", p => int.TryParse(p.Spid, out var s) ? s : 0);
                var victimSpids = victims
                    .Select(v => idToSpid.GetValueOrDefault(v ?? "", 0))
                    .Where(s => s > 0).ToList();
                var allSpids = idToSpid.Values.Where(s => s > 0).ToList();

                events.Add(new DeadlockEvent
                {
                    DetectedAtMs       = new DateTimeOffset(row.DetectedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    VictimSessionIds   = victimSpids,
                    InvolvedSessionIds = allSpids,
                    Summary            = $"Deadlock: {allSpids.Count} sessions, victim(s): {string.Join(", ", victimSpids.Select(s => $"#{s}"))}",
                });
            }
            catch { /* Skip unparseable XML */ }
        }
        return events;
    }
}
