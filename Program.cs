using SqlMetro;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SqlMetroOptions>(builder.Configuration.GetSection("SqlMetro"));
builder.Services.AddSingleton<SqlServerService>();
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var sqlSvc = app.Services.GetRequiredService<SqlServerService>();

// ── GET /api/snapshot ─────────────────────────────────────────────
app.MapGet("/api/snapshot", async (CancellationToken ct) =>
{
    var snap = await sqlSvc.GetSnapshotAsync(ct);
    return Results.Json(snap);
});

// ── GET /api/health ───────────────────────────────────────────────
app.MapGet("/api/health", async (CancellationToken ct) =>
{
    var snap = await sqlSvc.GetSnapshotAsync(ct);
    return snap.IsConnected
        ? Results.Ok(new { status = "ok", server = snap.ServerName ?? "SQL Server" })
        : Results.Problem(snap.ErrorMessage ?? "Unknown error");
});

app.Run();
