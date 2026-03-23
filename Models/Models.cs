namespace SqlMetro;

// ── Helpers ───────────────────────────────────────────────────────
public class DbName { public string? DatabaseName { get; set; } }

// ── Config ────────────────────────────────────────────────────────
public class SqlMetroOptions
{
    public int PollIntervalSeconds  { get; set; } = 3;
    public int WaitStatTopN         { get; set; } = 15;
    public int SlowQueryThresholdMs { get; set; } = 500;
    public string[] ExcludeSessions { get; set; } = [];
}

// ── Raw DMV rows ──────────────────────────────────────────────────

public class IdleBlocker
{
    public int     SessionId    { get; set; }
    public string? DatabaseName { get; set; }
    public string? LoginName    { get; set; }
    public string? HostName     { get; set; }
    public string? ProgramName  { get; set; }
    public string? SqlText      { get; set; }
}

public class DeadlockRow
{
    public DateTime DetectedAt   { get; set; }
    public string?  DeadlockXml  { get; set; }
}

public class ActiveRequest
{
    public int     SessionId          { get; set; }
    public int?    BlockingSessionId  { get; set; }
    public string? DatabaseName       { get; set; }
    public string? Status             { get; set; }
    public string? Command            { get; set; }
    public string? WaitType           { get; set; }
    public long    WaitTimeMs         { get; set; }
    public long    CpuTime            { get; set; }
    public long    LogicalReads       { get; set; }
    public long    Writes             { get; set; }
    public long    ElapsedMs          { get; set; }
    public string? ProgramName        { get; set; }
    public string? LoginName          { get; set; }
    public string? HostName           { get; set; }
    public string? SqlText            { get; set; }
    public string? ObjectName         { get; set; }
}

public class SessionSummary
{
    public string? DatabaseName     { get; set; }
    public int     TotalSessions    { get; set; }
    public int     ActiveSessions   { get; set; }
    public int     BlockedSessions  { get; set; }
    public long    TotalLogicalReads{ get; set; }
    public long    TotalWrites      { get; set; }
    public long    TotalCpuMs       { get; set; }
}

public class WaitStat
{
    public string WaitType          { get; set; } = "";
    public long   WaitingTasksCount { get; set; }
    public double WaitTimeSec       { get; set; }
    public double AvgWaitMs         { get; set; }
    public string Category          { get; set; } = "Other";
}

// ── Metro domain model ────────────────────────────────────────────

public class MetroDatabase
{
    public string           Id              { get; set; } = "";
    public string           Name            { get; set; } = "";
    public string           Colour          { get; set; } = "#888";
    public string           LightColour     { get; set; } = "#ccc";
    public int              TotalSessions   { get; set; }
    public int              ActiveSessions  { get; set; }
    public int              BlockedSessions { get; set; }
    public List<MetroStation> Stations      { get; set; } = [];
}

public enum StationType { Table, View, StoredProc, Database }

public class MetroStation
{
    public string      Id              { get; set; } = "";
    public string      Name            { get; set; } = "";
    public StationType Type            { get; set; }
    public string      DatabaseId      { get; set; } = "";
    public int         MaxQueue        { get; set; } = 10;
    public int         Queue           { get; set; }
    public long        TotalReads      { get; set; }
    public long        TotalWrites     { get; set; }
    public int         ActiveRequests  { get; set; }
    public int         BlockedRequests { get; set; }
    public bool        IsBottleneck    => Queue >= (int)(MaxQueue * 0.7) || BlockedRequests > 0;
    public string?     TopWaitType     { get; set; }
    public double      AvgElapsedMs    { get; set; }
}

public enum TrainOp { SELECT, INSERT, UPDATE, DELETE, EXECUTE, BATCH }

public class MetroTrain
{
    public string   Id           { get; set; } = "";
    public int      SessionId    { get; set; }
    public string   DatabaseId   { get; set; } = "";
    public TrainOp  Op           { get; set; }
    public bool     IsRead       => Op == TrainOp.SELECT;
    public bool     IsBlocked    { get; set; }
    public int?     BlockedBy    { get; set; }
    public bool     IsHeadBlocker      { get; set; }
    public int      BlockingChainDepth { get; set; }
    public int?     HeadBlockerSessionId { get; set; }
    public bool     IsDeadlockVictim   { get; set; }
    public bool     IsInDeadlock       { get; set; }
    public string?  ObjectName   { get; set; }
    public int      Passengers   { get; set; }
    public string   Status       { get; set; } = "";
    public string?  WaitType     { get; set; }
    public long     ElapsedMs    { get; set; }
    public long     LogicalReads { get; set; }
    public long     Writes       { get; set; }
    public string?  LoginName    { get; set; }
    public string?  HostName     { get; set; }
    public string?  SqlText      { get; set; }

    // Colour lookup by op type
    public string OpColour => Op switch
    {
        TrainOp.SELECT  => "#3B8BD4",
        TrainOp.INSERT  => "#1D9E75",
        TrainOp.UPDATE  => "#EF9F27",
        TrainOp.DELETE  => "#E24B4A",
        TrainOp.EXECUTE => "#7F77DD",
        _               => "#888780",
    };
    public string OpLabel => Op.ToString();
}

// ── Blocking chains ──────────────────────────────────────────────

public class BlockingChain
{
    public int         HeadBlockerSessionId { get; set; }
    public string?     HeadBlockerDatabase  { get; set; }
    public string?     HeadBlockerSqlText   { get; set; }
    public int         ChainDepth           { get; set; }
    public int         TotalVictims         { get; set; }
    public List<int>   VictimSessionIds     { get; set; } = [];
}

// ── Deadlock events ─────────────────────────────────────────────

public class DeadlockEvent
{
    public long        DetectedAtMs          { get; set; }
    public List<int>   VictimSessionIds      { get; set; } = [];
    public List<int>   InvolvedSessionIds    { get; set; } = [];
    public string?     Summary               { get; set; }
}

// ── API response ──────────────────────────────────────────────────

public class MetroSnapshot
{
    public long               CollectedAt         { get; set; }
    public long               PollIntervalMs      { get; set; }
    public bool               IsConnected         { get; set; }
    public string?            ErrorMessage        { get; set; }
    public string?            ServerName          { get; set; }
    public List<MetroDatabase> Databases          { get; set; } = [];
    public List<MetroTrain>   Trains              { get; set; } = [];
    public List<WaitStat>     TopWaits            { get; set; } = [];
    public int                TotalSessions       { get; set; }
    public int                TotalActiveRequests { get; set; }
    public int                TotalBlocked        { get; set; }
    public List<MetroDatabase> AllDatabases       { get; set; } = [];
    public List<BlockingChain> BlockingChains     { get; set; } = [];
    public List<DeadlockEvent> RecentDeadlocks    { get; set; } = [];
    public int                 DeadlockCount      { get; set; }
}
