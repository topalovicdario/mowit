using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using MowIT.Domain.Enums;
using MowIT.Shared.Telemetry;

namespace MowIT.RobotSimulator;

internal static class NetworkTestRunner
{
    private const int    DefaultRepeats          = 30;
    private const int    DefaultDeliveryCommands = 200;
    private const int    PollIntervalMs          = 500;
    private const int    DeliveryTimeoutMs       = 5000;
    private const int    RoutePoints             = 50;
    private const int    Seed                    = 20260101;
    private const double MetersPerDegree         = 111_319.444;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record Row(string Metric, int RunIndex, double ValueMs, bool Success);

    public static async Task RunAsync(IConfiguration config)
    {
        string baseUrl = LoopbackUrl.Normalize(
            config["Robot:BaseUrl"] ?? "http://localhost:5080", out bool rewritten);
        string robotId = config["Robot:RobotId"] ?? "demo-robot-01";
        string token   = config["Robot:Token"]   ?? "dev-token-please-replace-in-prod";

        int repeats  = Int(config["Network:Repeats"],          DefaultRepeats);
        int delivery = Int(config["Network:DeliveryCommands"], DefaultDeliveryCommands);

        string telemetryUrl = $"/robots/{Uri.EscapeDataString(robotId)}/telemetry";
        string commandsUrl  = $"/robots/{Uri.EscapeDataString(robotId)}/commands";

        string resultsDir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);

        Console.WriteLine($"MowIT.RobotSimulator - dio C1 (mrezni test) -> {baseUrl}, robot '{robotId}'");
        Console.WriteLine($"Izlaz: {resultsDir}");
        if (rewritten)
            Console.WriteLine("napomena: localhost preslikan u 127.0.0.1 (vidi LoopbackUrl.cs)");
        Console.WriteLine();

        var rows = new List<Row>();
        var rng  = new Random(Seed);

        using var http = NewClient(baseUrl, token);

        if (!await WarmUpAsync(http, telemetryUrl))
        {
            Console.WriteLine("! posluzitelj ne odgovara - pokrenuti MowIT.ScheduleServer prije ovog testa");
            return;
        }

        rows.AddRange(await MeasureConnectAsync(baseUrl, token, telemetryUrl, repeats));
        rows.AddRange(await MeasureTelemetryRttAsync(http, telemetryUrl, repeats));
        rows.AddRange(await MeasureCommandsRttAsync(http, commandsUrl, repeats));
        rows.AddRange(await MeasureDeliveryAsync(http, commandsUrl, "command_delivery", repeats, rng));
        rows.AddRange(await MeasureDeliveryAsync(http, commandsUrl, "delivery_success", delivery, rng));
        rows.AddRange(await MeasureRouteTransferAsync(http, commandsUrl, repeats, rng));

        WriteCsv(Path.Combine(resultsDir, "network.csv"), rows);

        RunInfo.Write(resultsDir, "network-test", new Dictionary<string, string>
        {
            ["base_url"]           = baseUrl,
            ["robot_id"]           = robotId,
            ["repeats_per_metric"] = repeats.ToString(CultureInfo.InvariantCulture),
            ["delivery_commands"]  = delivery.ToString(CultureInfo.InvariantCulture),
            ["route_points"]       = RoutePoints.ToString(CultureInfo.InvariantCulture),
            ["poll_interval_ms"]   = PollIntervalMs.ToString(CultureInfo.InvariantCulture),
            ["seed"]               = Seed.ToString(CultureInfo.InvariantCulture),
        });

        Console.WriteLine();
        Console.WriteLine($"network.csv zapisan ({rows.Count} redaka)");
    }

    private static async Task<bool> WarmUpAsync(HttpClient http, string telemetryUrl)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var resp = await http.PostAsJsonAsync(telemetryUrl, SyntheticTelemetry());
                if (resp.IsSuccessStatusCode) return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  cekam posluzitelj: {ex.Message}");
            }
            await Task.Delay(1000);
        }
        return false;
    }

    private static async Task<List<Row>> MeasureConnectAsync(
        string baseUrl, string token, string telemetryUrl, int repeats)
    {
        var rows = new List<Row>();
        for (int i = 0; i < repeats; i++)
        {
            using var client = NewClient(baseUrl, token);
            var sw = Stopwatch.StartNew();
            bool ok;
            try
            {
                var resp = await client.PostAsJsonAsync(telemetryUrl, SyntheticTelemetry());
                ok = resp.IsSuccessStatusCode;
            }
            catch { ok = false; }
            sw.Stop();
            rows.Add(new Row("connect", i, sw.Elapsed.TotalMilliseconds, ok));
        }
        Summarize("connect", rows);
        return rows;
    }

    private static async Task<List<Row>> MeasureTelemetryRttAsync(
        HttpClient http, string telemetryUrl, int repeats)
    {
        var rows = new List<Row>();
        for (int i = 0; i < repeats; i++)
        {
            var sw = Stopwatch.StartNew();
            bool ok;
            try
            {
                var resp = await http.PostAsJsonAsync(telemetryUrl, SyntheticTelemetry());
                ok = resp.IsSuccessStatusCode;
            }
            catch { ok = false; }
            sw.Stop();
            rows.Add(new Row("telemetry_rtt", i, sw.Elapsed.TotalMilliseconds, ok));
        }
        Summarize("telemetry_rtt", rows);
        return rows;
    }

    private static async Task<List<Row>> MeasureCommandsRttAsync(
        HttpClient http, string commandsUrl, int repeats)
    {
        var rows = new List<Row>();
        for (int i = 0; i < repeats; i++)
        {
            var sw = Stopwatch.StartNew();
            bool ok;
            try
            {
                var pending = await http.GetFromJsonAsync<CommandListResponse>(commandsUrl);
                ok = pending is not null;
            }
            catch { ok = false; }
            sw.Stop();
            rows.Add(new Row("commands_rtt", i, sw.Elapsed.TotalMilliseconds, ok));
        }
        Summarize("commands_rtt", rows);
        return rows;
    }

    private static async Task<List<Row>> MeasureDeliveryAsync(
        HttpClient http, string commandsUrl, string metric, int count, Random rng)
    {
        var rows  = new List<Row>();
        var clock = new PollClock(PollIntervalMs);

        for (int i = 0; i < count; i++)
        {
            var command = new RobotCommandDto
            {
                Kind       = CommandKinds.Action,
                ActionName = nameof(RobotAction.Stop),
                ActionCode = (byte)RobotAction.Stop
            };

            var (latencyMs, ok, _) = await PostAndAwaitAsync(http, commandsUrl, command, clock, rng);
            rows.Add(new Row(metric, i, latencyMs, ok));

            if (metric == "delivery_success" && (i + 1) % 50 == 0)
                Console.WriteLine($"  delivery_success: {i + 1}/{count}");
        }

        Summarize(metric, rows);
        return rows;
    }

    private static async Task<List<Row>> MeasureRouteTransferAsync(
        HttpClient http, string commandsUrl, int repeats, Random rng)
    {
        var rows  = new List<Row>();
        var clock = new PollClock(PollIntervalMs);
        var route = SyntheticRoute(RoutePoints);

        for (int i = 0; i < repeats; i++)
        {
            var command = new RobotCommandDto
            {
                Kind     = CommandKinds.Boundary,
                Boundary = new BoundaryUploadDto { Name = "network-test", Points = route }
            };

            var (latencyMs, ok, received) = await PostAndAwaitAsync(http, commandsUrl, command, clock, rng);
            bool valid = ok && received?.Boundary?.Points.Length == route.Length;
            rows.Add(new Row("route_transfer", i, latencyMs, valid));
        }

        Summarize("route_transfer", rows);
        return rows;
    }

    private static async Task<(double LatencyMs, bool Success, RobotCommandDto? Received)> PostAndAwaitAsync(
        HttpClient http, string commandsUrl, RobotCommandDto command, PollClock clock, Random rng)
    {
        await Task.Delay(rng.Next(PollIntervalMs));

        var createdAt = DateTime.UtcNow;
        try
        {
            var resp = await http.PostAsJsonAsync(commandsUrl, command);
            if (!resp.IsSuccessStatusCode)
                return ((DateTime.UtcNow - createdAt).TotalMilliseconds, false, null);
        }
        catch
        {
            return ((DateTime.UtcNow - createdAt).TotalMilliseconds, false, null);
        }

        var deadline = createdAt.AddMilliseconds(DeliveryTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await clock.WaitNextAsync();
            try
            {
                var pending = await http.GetFromJsonAsync<CommandListResponse>(commandsUrl);
                var hit = pending?.Commands.FirstOrDefault(c => c.Id == command.Id);
                if (hit is not null)
                    return ((DateTime.UtcNow - createdAt).TotalMilliseconds, true, hit);
            }
            catch
            {
            }
        }

        return ((DateTime.UtcNow - createdAt).TotalMilliseconds, false, null);
    }

    private sealed class PollClock
    {
        private readonly int _periodMs;
        private DateTime _next = DateTime.UtcNow;

        public PollClock(int periodMs) => _periodMs = periodMs;

        public async Task WaitNextAsync()
        {
            _next = _next.AddMilliseconds(_periodMs);
            var delay = _next - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            else _next = DateTime.UtcNow;
        }
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

    private static TelemetryDto SyntheticTelemetry() => new()
    {
        Lat           = 43.8563,
        Lon           = 18.4131,
        GpsAccuracyMm = 12f,
        GpsFixType    = 3,
        HeadingRad    = 1.57f,
        LinearSpeed   = 0.3f,
        AccZ          = 9.81f,
        BatteryPct    = 85,
        State         = 0,
        TimestampUtc  = DateTime.UtcNow
    };

    private static GpsPointDto[] SyntheticRoute(int count)
    {
        var points = new GpsPointDto[count];
        for (int i = 0; i < count; i++)
            points[i] = new GpsPointDto { Lat = 43.8563 + i / MetersPerDegree, Lon = 18.4131 };
        return points;
    }

    private static void Summarize(string metric, IEnumerable<Row> allRows)
    {
        var values = allRows.Where(r => r.Metric == metric).Select(r => r.ValueMs).ToList();
        if (values.Count == 0) return;

        int ok = allRows.Count(r => r.Metric == metric && r.Success);
        Console.WriteLine(
            $"{metric,-17} n={values.Count,-4} srednje={F(values.Average(), "0.00"),8} ms  " +
            $"min={F(values.Min(), "0.00"),8} ms  max={F(values.Max(), "0.00"),8} ms  " +
            $"uspjeh={ok}/{values.Count}");
    }

    private static void WriteCsv(string path, IEnumerable<Row> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("metric,run_index,value_ms,success");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                r.Metric,
                r.RunIndex.ToString(CultureInfo.InvariantCulture),
                F(r.ValueMs, "0.###"),
                r.Success ? "1" : "0"));
    }

    private static int Int(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : fallback;

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
