using KyoshinDashboard.Hubs;
using KyoshinDashboard.Models;
using KyoshinDashboard.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// A published app has wwwroot next to the exe: use that folder as content root, so wwwroot,
// appsettings.json and Kyoshin.db are found however it is started (cmd from any folder, Windows Service).
// During development (dotnet run, no wwwroot in bin) the content root stays the current/project folder.
var appFolder = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(appFolder, "wwwroot")) ? appFolder : null
});

// Windows Service support (allows running as sc.exe service)
builder.Host.UseWindowsService();

// Database — a relative SQLite path is resolved against the app folder (content root),
// because a Windows Service runs with current directory = C:\Windows\System32
var dbConnection = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("KyoshinDb") ?? "Data Source=Kyoshin.db");
dbConnection.DataSource = Path.Combine(builder.Environment.ContentRootPath, dbConnection.DataSource);
builder.Services.AddDbContext<KyoshinDbContext>(options =>
    options.UseSqlite(dbConnection.ToString()));

// MVC + SignalR
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

// Machine state + logs, fed by the factory agent through POST /api/ingest (IngestController)
builder.Services.AddSingleton<MachineMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MachineMonitorService>());
// Background data aggregation (every 5 min)
builder.Services.AddHostedService<DataAggregationService>();

var app = builder.Build();

// Auto-create/migrate database + seed initial data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KyoshinDbContext>();
    db.Database.EnsureCreated();
    LogTimeMigration.Run(db, app.Logger);

    // EnsureCreated doesn't add tables to an existing database: add the agent cursor table if missing
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "AgentCursors" (
            "AgentId"  TEXT    NOT NULL CONSTRAINT "PK_AgentCursors" PRIMARY KEY,
            "LastSeq"  INTEGER NOT NULL,
            "LastSeen" TEXT    NOT NULL);
        """);

    // Seed: if no status logs exist, create one "Off" record per machine
    if (!db.StatusLogs.Any())
    {
        var now = DateTime.Now;
        var hour = now.Hour;
        var shift = hour >= 6 && hour < 14 ? ShiftType.Shift1
                  : hour >= 14 && hour < 22 ? ShiftType.Shift2
                  : ShiftType.Shift3;

        for (int m = 1; m <= MachineMonitorService.TOTAL_MACHINES; m++)
        {
            db.StatusLogs.Add(new MachineStatusLog
            {
                MachineId = m,
                Status = MachineStatus.Off,
                StartTime = now,
                EndTime = null,
                DurationSeconds = 0,
                Shift = shift
            });
        }
        db.SaveChanges();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapHub<MachineStatusHub>("/machineHub");

// Shifts and statistics use local time: on the TrueNAS container set TZ=Asia/Ho_Chi_Minh and check this line
app.Logger.LogInformation("Kyoshin Server {Version}. Server time zone: {TimeZone}, local time now {Now:yyyy-MM-dd HH:mm:ss}, database {Db}",
    AppVersion.Current, TimeZoneInfo.Local.DisplayName, DateTime.Now, dbConnection.DataSource);
var ingestKey = app.Configuration["Ingest:ApiKey"] ?? "";
if (ingestKey.Length == 0 || ingestKey == KyoshinDashboard.Controllers.IngestController.PLACEHOLDER_KEY)
    app.Logger.LogWarning("Ingest:ApiKey is not set — the factory agent cannot send data (set env Ingest__ApiKey)");
else if (ingestKey.Length < 16 && !app.Environment.IsDevelopment())
    app.Logger.LogWarning("Ingest:ApiKey is short ({Length} chars) — use a long random key, it travels over plain HTTP", ingestKey.Length);

app.Run();
