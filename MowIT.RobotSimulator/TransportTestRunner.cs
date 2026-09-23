using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Shared.Telemetry;

namespace MowIT.RobotSimulator;

internal static class TransportTestRunner
{
    private const double Dt = 0.05;

    private const double WarmupSeconds = 8.0;

    private const double DriveSeconds     = 0.5;
    private const double MowSeconds       = 5.0;
    private const float  DriveLinearVel   = 0.3f;
    private const int    CapturePoints    = 4;
    private const int    RoutePoints      = 5;
    private const double RouteSpacingM    = 2.0;
    private const double MetersPerDegree  = 111_319.444;
    private const double StartLat         = 43.8563;
    private const double StartLon         = 18.4131;

    private const int DrainPollIntervalMs = 20;
    private const int DrainTimeoutMs      = 5000;

    private static readonly int[] RouteFidelitySizes = [4, 50, 255, 256, 1000, 2500];

    private const double FidelityStepDeg = 0.00001;

    private const double RouteFidelityToleranceMm = 1e-7 * MetersPerDegree * 1000.0;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record StepRow(
        int Index, string Action, int LocalState, int RemoteState,
        string LocalEvents, string RemoteEvents, bool Match, double LatencyMs);

    private sealed record RouteFidelityRow(
        string Transport, int RoutePoints, int ReceivedPoints, bool OrderOk,
        double MaxDeviationMm, double TransferTimeMs, bool Pass);

    public static async Task RunAsync(IConfiguration config)
    {
        string baseUrl = LoopbackUrl.Normalize(
            config["Robot:BaseUrl"] ?? "http://localhost:5080", out bool rewritten);
        string robotId = config["Transport:RobotId"] ?? "transport-test-robot";
        string token   = config["Robot:Token"]       ?? "dev-token-please-replace-in-prod";

        string resultsDir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);

        Console.WriteLine($"MowIT.RobotSimulator - dio F (istovjetnost prijenosa) -> {baseUrl}, robot '{robotId}'");
        Console.WriteLine($"Izlaz: {resultsDir}");
        if (rewritten)
            Console.WriteLine("napomena: localhost preslikan u 127.0.0.1 (vidi LoopbackUrl.cs)");
        Console.WriteLine();

        using var http = NewClient(baseUrl, token);

        if (!await WarmUpAsync(http, $"/robots/{Uri.EscapeDataString(robotId)}/commands"))
        {
            Console.WriteLine("! posluzitelj ne odgovara - pokrenuti MowIT.ScheduleServer prije ovog testa");
            return;
        }

        var run = new ConformanceRun(http, robotId);
        await run.InitAsync();
        await RunSequenceAsync(run);

        WriteCsv(Path.Combine(resultsDir, "transport_conformance.csv"), run.Steps);

        await RunRouteFidelitySweepAsync(run, resultsDir);

        RunInfo.Write(resultsDir, "transport-test", new Dictionary<string, string>
        {
            ["base_url"]             = baseUrl,
            ["robot_id"]             = robotId,
            ["step_count"]           = run.Steps.Count.ToString(CultureInfo.InvariantCulture),
            ["dt_s"]                 = F(Dt),
            ["warmup_s"]             = F(WarmupSeconds),
            ["drain_poll_ms"]        = DrainPollIntervalMs.ToString(CultureInfo.InvariantCulture),
            ["noise_sigma_m"]        = "0",
        });

        int matched = run.Steps.Count(s => s.Match);
        var latencies = run.Steps.Where(s => s.LatencyMs > 0).Select(s => s.LatencyMs).ToList();

        Console.WriteLine();
        Console.WriteLine($"podudaranje: {matched}/{run.Steps.Count} koraka");
        if (latencies.Count > 0)
            Console.WriteLine($"zicani put: srednje {F(latencies.Average(), "0.00")} ms, " +
                              $"min {F(latencies.Min(), "0.00")} ms, max {F(latencies.Max(), "0.00")} ms " +
                              $"({latencies.Count} naredbi)");
        Console.WriteLine($"transport_conformance.csv zapisan ({run.Steps.Count} redaka)");
    }

    private static async Task RunSequenceAsync(ConformanceRun run)
    {
        await run.StepAsync("Tick/rampa", null, WarmupSeconds);
        await run.StepAsync("CaptureBase",         Action(RobotAction.CaptureBase));
        await run.StepAsync("BoundaryRecordStart", Action(RobotAction.BoundaryRecordStart));

        for (int i = 0; i < CapturePoints; i++)
        {
            await run.StepAsync("Motor/voznja", Motor(DriveLinearVel, 0f), DriveSeconds);
            await run.StepAsync("BoundaryCapturePoint", Action(RobotAction.BoundaryCapturePoint));
        }

        await run.StepAsync("CaptureOutline",    Action(RobotAction.CaptureOutline));
        await run.StepAsync("CaptureExit",       Action(RobotAction.CaptureExit));
        await run.StepAsync("BoundaryRecordEnd", Action(RobotAction.BoundaryRecordEnd));
        await run.StepAsync("Boundary/ruta",     Route(RoutePoints));
        await run.StepAsync("StartMowing",       Action(RobotAction.StartMowing));
        await run.StepAsync("Tick/kosnja", null, MowSeconds);
        await run.StepAsync("Stop",              Action(RobotAction.Stop));
    }

    private static async Task RunRouteFidelitySweepAsync(ConformanceRun run, string resultsDir)
    {
        Console.WriteLine();
        Console.WriteLine("Dorada dio 4 (R3) - vjernost prijenosa rute preko http_wire");

        var rows = new List<RouteFidelityRow>();

        foreach (int n in RouteFidelitySizes)
        {
            var route = SyntheticFidelityRoute(n);
            var command = new RobotCommandDto
            {
                Kind     = CommandKinds.Boundary,
                Boundary = new BoundaryUploadDto { Name = "route-fidelity", Points = route }
            };

            var (received, latencyMs) = await run.SendAndAwaitEchoAsync(command);
            var receivedPoints = received?.Boundary?.Points ?? Array.Empty<GpsPointDto>();

            bool orderOk = receivedPoints.Length == n;
            for (int i = 1; i < receivedPoints.Length && orderOk; i++)
                if (receivedPoints[i].Lat <= receivedPoints[i - 1].Lat) orderOk = false;

            double maxDevMm = 0;
            int compareCount = Math.Min(n, receivedPoints.Length);
            for (int i = 0; i < compareCount; i++)
            {
                var sent = new GpsPoint(route[i].Lat, route[i].Lon);
                var back = new GpsPoint(receivedPoints[i].Lat, receivedPoints[i].Lon);
                double devMm = sent.DistanceTo(back) * 1000.0;
                if (devMm > maxDevMm) maxDevMm = devMm;
            }

            bool pass = orderOk && receivedPoints.Length == n && maxDevMm <= RouteFidelityToleranceMm;
            rows.Add(new RouteFidelityRow("http_wire", n, receivedPoints.Length, orderOk, maxDevMm, latencyMs, pass));

            Console.WriteLine(
                $"RF  {(pass ? "PROLAZ" : "PAD   ")}  n={n,4}  primljeno={receivedPoints.Length,4}  " +
                $"poredak={(orderOk ? "OK" : "PAD")}  najvece odstupanje={F(maxDevMm, "0.####"),10} mm  " +
                $"{F(latencyMs, "0.00"),8} ms");
        }

        WriteRouteFidelityCsv(Path.Combine(resultsDir, "route_fidelity.csv"), rows);
        Console.WriteLine($"route_fidelity.csv zapisan ({rows.Count} redaka)");
    }

    private static GpsPointDto[] SyntheticFidelityRoute(int count)
    {
        var points = new GpsPointDto[count];
        for (int i = 0; i < count; i++)
            points[i] = new GpsPointDto { Lat = StartLat + i * FidelityStepDeg, Lon = StartLon };
        return points;
    }

    private sealed class ConformanceRun
    {
        private readonly VirtualRobot _local  = new();
        private readonly VirtualRobot _remote = new();
        private readonly HttpClient _http;
        private readonly string _commandsUrl;
        private readonly string _eventsUrl;
        private long _cursor;

        public List<StepRow> Steps { get; } = new();

        public ConformanceRun(HttpClient http, string robotId)
        {
            _http        = http;
            _commandsUrl = $"/robots/{Uri.EscapeDataString(robotId)}/commands";
            _eventsUrl   = $"/robots/{Uri.EscapeDataString(robotId)}/events";
        }

        public async Task InitAsync()
        {
            var seen = await TryGetAsync<EventListResponse>(_http, $"{_eventsUrl}?since=0");
            _cursor = seen?.Cursor ?? 0;
        }

        public async Task StepAsync(string action, RobotCommandDto? command, double seconds = 0)
        {
            var localEvents  = command is null
                ? Array.Empty<RobotEventDto>()
                : _local.ApplyCommand(command);

            IReadOnlyList<RobotEventDto> remoteEvents = Array.Empty<RobotEventDto>();
            double latencyMs = 0;

            if (command is not null)
            {
                var sw = Stopwatch.StartNew();
                remoteEvents = await SendOverWireAsync(command);
                sw.Stop();
                latencyMs = sw.Elapsed.TotalMilliseconds;
            }

            Tick(seconds);

            bool match = _local.State == _remote.State && SameEvents(localEvents, remoteEvents);
            var row = new StepRow(
                Steps.Count + 1, action, _local.State, _remote.State,
                Describe(localEvents), Describe(remoteEvents), match, latencyMs);
            Steps.Add(row);

            Console.WriteLine(
                $"{row.Index:00}  {(match ? "PROLAZ" : "PAD   ")}  {action,-20} " +
                $"stanje {row.LocalState}/{row.RemoteState}  " +
                $"dogadaji [{row.LocalEvents}]/[{row.RemoteEvents}]  " +
                $"{F(latencyMs, "0.00"),8} ms");

            if (!match)
                Console.WriteLine($"    ! neslaganje u koraku {row.Index}: " +
                                  $"{Detail(localEvents)} naspram {Detail(remoteEvents)}");
        }

        private void Tick(double seconds)
        {
            int ticks = (int)Math.Round(seconds / Dt, MidpointRounding.AwayFromZero);
            for (int i = 0; i < ticks; i++)
            {
                _local.Tick(Dt);
                _remote.Tick(Dt);
            }
        }

        private async Task<IReadOnlyList<RobotEventDto>> SendOverWireAsync(RobotCommandDto command)
        {
            try
            {
                var accepted = await _http.PostAsJsonAsync(_commandsUrl, command);
                if (!accepted.IsSuccessStatusCode)
                    Console.WriteLine($"    ! POST naredbe vratio {(int)accepted.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ! naredba nije poslana: {ex.Message}");
                return Array.Empty<RobotEventDto>();
            }

            bool delivered = false;
            var deadline = DateTime.UtcNow.AddMilliseconds(DrainTimeoutMs);
            while (!delivered && DateTime.UtcNow < deadline)
            {
                var pending = await TryGetAsync<CommandListResponse>(_http, _commandsUrl);
                foreach (var received in pending?.Commands ?? Array.Empty<RobotCommandDto>())
                {
                    foreach (var evt in _remote.ApplyCommand(received))
                        await PostEventAsync(evt);
                    delivered |= received.Id == command.Id;
                }
                if (!delivered) await Task.Delay(DrainPollIntervalMs);
            }

            if (!delivered)
                Console.WriteLine($"    ! naredba {command.ActionName ?? command.Kind} nije stigla " +
                                  $"natrag unutar {DrainTimeoutMs} ms");

            var list = await TryGetAsync<EventListResponse>(_http, $"{_eventsUrl}?since={_cursor}");
            if (list is null) return Array.Empty<RobotEventDto>();

            _cursor = list.Cursor;
            return list.Events;
        }

        private async Task PostEventAsync(RobotEventDto evt)
        {
            try { await _http.PostAsJsonAsync(_eventsUrl, evt); }
            catch (Exception ex) { Console.WriteLine($"    ! dogadaj {evt.Type} nije poslan: {ex.Message}"); }
        }

        public async Task<(RobotCommandDto? Received, double LatencyMs)> SendAndAwaitEchoAsync(RobotCommandDto command)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var accepted = await _http.PostAsJsonAsync(_commandsUrl, command);
                if (!accepted.IsSuccessStatusCode)
                {
                    Console.WriteLine($"    ! POST naredbe vratio {(int)accepted.StatusCode}");
                    sw.Stop();
                    return (null, sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ! naredba nije poslana: {ex.Message}");
                sw.Stop();
                return (null, sw.Elapsed.TotalMilliseconds);
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(DrainTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var pending = await TryGetAsync<CommandListResponse>(_http, _commandsUrl);
                var hit = pending?.Commands.FirstOrDefault(c => c.Id == command.Id);
                if (hit is not null)
                {
                    sw.Stop();
                    return (hit, sw.Elapsed.TotalMilliseconds);
                }
                await Task.Delay(DrainPollIntervalMs);
            }

            sw.Stop();
            Console.WriteLine($"    ! naredba {command.Kind} nije stigla natrag unutar {DrainTimeoutMs} ms");
            return (null, sw.Elapsed.TotalMilliseconds);
        }
    }

    private static bool SameEvents(IReadOnlyList<RobotEventDto> local, IReadOnlyList<RobotEventDto> remote)
    {
        if (local.Count != remote.Count) return false;

        for (int i = 0; i < local.Count; i++)
        {
            if (local[i].Type    != remote[i].Type    ||
                local[i].Reason  != remote[i].Reason  ||
                local[i].XCm     != remote[i].XCm     ||
                local[i].YCm     != remote[i].YCm     ||
                local[i].Success != remote[i].Success)
                return false;
        }
        return true;
    }

    private static string Describe(IEnumerable<RobotEventDto> events) =>
        string.Join(';', events.Select(e => e.Reason is null ? e.Type : $"{e.Type}/{e.Reason}"));

    private static string Detail(IEnumerable<RobotEventDto> events) =>
        string.Join(';', events.Select(e =>
            $"{e.Type}/{e.Reason ?? "-"}/x={e.XCm}/y={e.YCm}/ok={e.Success}"));

    private static RobotCommandDto Action(RobotAction action) => new()
    {
        Kind       = CommandKinds.Action,
        ActionName = action.ToString(),
        ActionCode = (byte)action
    };

    private static RobotCommandDto Motor(float linear, float angular) => new()
    {
        Kind       = CommandKinds.Motor,
        LinearVel  = linear,
        AngularVel = angular
    };

    private static RobotCommandDto Route(int count)
    {
        var points = new GpsPointDto[count];
        for (int i = 0; i < count; i++)
            points[i] = new GpsPointDto
            {
                Lat = StartLat + i * RouteSpacingM / MetersPerDegree,
                Lon = StartLon
            };

        return new RobotCommandDto
        {
            Kind     = CommandKinds.Boundary,
            Boundary = new BoundaryUploadDto { Name = "transport-test", Points = points }
        };
    }

    private static async Task<bool> WarmUpAsync(HttpClient http, string commandsUrl)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var resp = await http.GetAsync(commandsUrl);
                if (resp.IsSuccessStatusCode) return true;
                Console.WriteLine($"  posluzitelj odgovara {(int)resp.StatusCode} - provjeriti Robot:Token");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  cekam posluzitelj: {ex.Message}");
            }
            await Task.Delay(1000);
        }
        return false;
    }

    private static async Task<T?> TryGetAsync<T>(HttpClient http, string url) where T : class
    {
        try { return await http.GetFromJsonAsync<T>(url); }
        catch { return null; }
    }

    private static HttpClient NewClient(string baseUrl, string token)
    {
        var client = new HttpClient(new SocketsHttpHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void WriteCsv(string path, IEnumerable<StepRow> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("step,action,local_state,remote_state,local_events,remote_events,match,latency_ms");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                r.Index.ToString(CultureInfo.InvariantCulture),
                Csv(r.Action),
                r.LocalState.ToString(CultureInfo.InvariantCulture),
                r.RemoteState.ToString(CultureInfo.InvariantCulture),
                Csv(r.LocalEvents),
                Csv(r.RemoteEvents),
                r.Match ? "1" : "0",
                F(r.LatencyMs, "0.###")));
    }

    private static void WriteRouteFidelityCsv(string path, IEnumerable<RouteFidelityRow> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("transport,route_points,received_points,order_ok,max_deviation_mm,transfer_time_ms,pass");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                r.Transport,
                r.RoutePoints.ToString(CultureInfo.InvariantCulture),
                r.ReceivedPoints.ToString(CultureInfo.InvariantCulture),
                r.OrderOk ? "1" : "0",
                F(r.MaxDeviationMm, "0.######"),
                F(r.TransferTimeMs, "0.###"),
                r.Pass ? "1" : "0"));
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
