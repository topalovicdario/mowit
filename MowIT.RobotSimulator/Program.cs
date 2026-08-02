using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MowIT.RobotSimulator;
using MowIT.Shared.Schedules;
using MowIT.Shared.Telemetry;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

string baseUrl = config["Robot:BaseUrl"] ?? "http://localhost:5080";
string robotId = config["Robot:RobotId"] ?? "demo-robot-01";
string token   = config["Robot:Token"]   ?? "dev-token-please-replace-in-prod";
double tickHz  = double.TryParse(config["Robot:TickHz"], out var hz) && hz > 0 ? hz : 2.0;

var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

string telemetryUrl = $"/robots/{Uri.EscapeDataString(robotId)}/telemetry";
string commandsUrl  = $"/robots/{Uri.EscapeDataString(robotId)}/commands";
string eventsUrl    = $"/robots/{Uri.EscapeDataString(robotId)}/events";
string schedulesUrl = $"/robots/{Uri.EscapeDataString(robotId)}/schedules";

var robot = new VirtualRobot();
double dt = 1.0 / tickHz;
long scheduleVersion = -1;
int  tickCount = 0;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Log($"virtual robot '{robotId}' -> {baseUrl}  (tick {tickHz:F0} Hz)");

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(dt));
while (!cts.IsCancellationRequested && await SafeWaitAsync(timer, cts.Token))
{
    robot.Tick(dt);

    try
    {
        var telemetry = robot.ToTelemetry();
        await http.PostAsJsonAsync(telemetryUrl, telemetry, cts.Token);

        var pending = await http.GetFromJsonAsync<CommandListResponse>(commandsUrl, cts.Token);
        if (pending?.Commands is { Length: > 0 } commands)
        {
            foreach (var command in commands)
            {
                Log($"RX cmd {command.Kind} {command.ActionName} lin={command.LinearVel:F2} ang={command.AngularVel:F2}");
                foreach (var evt in robot.ApplyCommand(command))
                {
                    await http.PostAsJsonAsync(eventsUrl, evt, cts.Token);
                    Log($"TX event {evt.Type}{(evt.Reason is null ? "" : "/" + evt.Reason)}");
                }
            }
        }

        if (tickCount++ % 10 == 0)
        {
            var schedules = await http.GetFromJsonAsync<ScheduleListResponse>(schedulesUrl, cts.Token);
            if (schedules is not null && schedules.Version != scheduleVersion)
            {
                scheduleVersion = schedules.Version;
                Log($"schedules synced from cloud: v{schedules.Version}, {schedules.Schedules.Length} schedule(s)");
                foreach (var s in schedules.Schedules)
                    Log($"   - \"{s.ZoneName}\" {string.Join(",", s.ActiveDays)} {new TimeSpan(s.StartTimeTicks):hh\\:mm} {s.DurationMinutes}min active={s.IsActive}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Log($"! {ex.Message} (is the server running?)");
    }
}

Log("stopped");

static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
{
    try { return await timer.WaitForNextTickAsync(ct); }
    catch (OperationCanceledException) { return false; }
}

static void Log(string message)
    => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
