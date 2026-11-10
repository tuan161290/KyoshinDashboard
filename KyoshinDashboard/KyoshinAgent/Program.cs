using System.Reflection;
using KyoshinAgent;

// Exceptions nothing caught go to agent-crash.log and the event log, with the stack trace
CrashLog.Register();

// Content root = exe folder, so appsettings.json and agent.db are found however the agent is started
// (cmd from any folder, or as a Windows Service whose current directory is C:\Windows\System32)
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Windows Service support (sc.exe create KyoshinAgent ...)
builder.Services.AddWindowsService(options => options.ServiceName = "KyoshinAgent");

builder.Services.Configure<PlcOptions>(builder.Configuration.GetSection("Plc"));
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<OutboxStore>();
builder.Services.AddHttpClient(UploaderService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));

// Read the PLC and queue changes; upload the queue to the server
builder.Services.AddHostedService<PlcPollingService>();
builder.Services.AddHostedService<UploaderService>();
// Unfiltered inputs for the RAW IO page
builder.Services.AddHostedService<RawIoUploaderService>();
// Restart the agent if PLC polling stops
builder.Services.AddHostedService<PollingWatchdogService>();

var host = builder.Build();
var version = (typeof(PlcOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];
host.Services.GetRequiredService<ILogger<PlcOptions>>().LogInformation("Kyoshin Agent {Version}", version);
host.Run();
