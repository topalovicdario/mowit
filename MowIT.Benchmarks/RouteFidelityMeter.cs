using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MowIT.Domain.Entities;
using MowIT.Infrastructure.Ble;
using MowIT.Shared.Telemetry;

namespace MowIT.Benchmarks;

public sealed record RouteFidelityRow(
    string Transport, int RoutePoints, int ReceivedPoints, bool OrderOk,
    double MaxDeviationMm, double TransferTimeMs, bool Pass);

public static class RouteFidelityMeter
{
    private static readonly int[] RouteSizes = [4, 50, 255, 256, 1000, 2500];

    private const double ToleranceMm = 1e-7 * 111_319.444 * 1000.0;

    private const double StartLat = 43.8563;
    private const double StartLon = 18.4131;

    private const double StepDeg = 0.00001;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void RunAndWrite(string resultsDir)
    {
        Directory.CreateDirectory(resultsDir);

        var rows = new List<RouteFidelityRow>();
        foreach (int n in RouteSizes) rows.Add(MeasureJson(n));
        foreach (int n in RouteSizes) rows.Add(MeasureBle(n));

        foreach (var r in rows)
            Console.WriteLine(
                $"  {r.Transport,-10} n={r.RoutePoints,4}  primljeno={r.ReceivedPoints,4}  " +
                $"poredak={(r.OrderOk ? "OK" : "PAD")}  najvece odstupanje={F(r.MaxDeviationMm, "0.####"),10} mm  " +
                $"{F(r.TransferTimeMs, "0.###"),8} ms  {(r.Pass ? "PROLAZ" : "PAD")}");

        WriteCsv(Path.Combine(resultsDir, "route_fidelity.csv"), rows);
        Console.WriteLine($"  route_fidelity.csv zapisan ({rows.Count} redaka)");
    }

    private static RouteFidelityRow MeasureJson(int n)
    {
        var route = SyntheticRoute(n);
        var dtoPoints = new GpsPointDto[n];
        for (int i = 0; i < n; i++)
            dtoPoints[i] = new GpsPointDto { Lat = route[i].Latitude, Lon = route[i].Longitude };

        var command = new RobotCommandDto
        {
            Kind     = CommandKinds.Boundary,
            Boundary = new BoundaryUploadDto { Name = "fidelity-test", Points = dtoPoints }
        };

        var sw = Stopwatch.StartNew();
        string json = JsonSerializer.Serialize(command);
        var roundTripped = JsonSerializer.Deserialize<RobotCommandDto>(json);
        sw.Stop();

        var received = roundTripped?.Boundary?.Points ?? Array.Empty<GpsPointDto>();

        bool orderOk = received.Length == n;
        for (int i = 1; i < received.Length && orderOk; i++)
            if (received[i].Lat <= received[i - 1].Lat) orderOk = false;

        double maxDevMm = MaxDeviationMm(route, received, n);
        bool pass = orderOk && received.Length == n && maxDevMm <= ToleranceMm;

        return new RouteFidelityRow("dto_json", n, received.Length, orderOk, maxDevMm,
            sw.Elapsed.TotalMilliseconds, pass);
    }

    private static RouteFidelityRow MeasureBle(int n)
    {
        var route = SyntheticRoute(n);
        var reassembled = new GpsPoint?[n];
        bool orderOk = true;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            byte[] chunk = BlePacketSerializer.SerializeBoundaryChunk((ushort)i, (ushort)n, 1, route[i]);

            ushort idx = BitConverter.ToUInt16(chunk, 0);
            ushort tot = BitConverter.ToUInt16(chunk, 2);
            double lat = BitConverter.ToDouble(chunk, 6);
            double lon = BitConverter.ToDouble(chunk, 14);

            if (idx != i || tot != n) orderOk = false;
            if (idx < n) reassembled[idx] = new GpsPoint(lat, lon);
        }
        sw.Stop();

        int receivedCount = reassembled.Count(p => p.HasValue);
        double maxDevMm = 0;
        for (int i = 0; i < n; i++)
        {
            if (reassembled[i] is not { } point) { orderOk = false; continue; }
            double devMm = route[i].DistanceTo(point) * 1000.0;
            if (devMm > maxDevMm) maxDevMm = devMm;
        }

        bool pass = orderOk && receivedCount == n && maxDevMm <= ToleranceMm;
        return new RouteFidelityRow("ble_packet", n, receivedCount, orderOk, maxDevMm,
            sw.Elapsed.TotalMilliseconds, pass);
    }

    private static double MaxDeviationMm(GpsPoint[] sent, GpsPointDto[] received, int sentCount)
    {
        double maxDevMm = 0;
        int compareCount = Math.Min(sentCount, received.Length);
        for (int i = 0; i < compareCount; i++)
        {
            var back = new GpsPoint(received[i].Lat, received[i].Lon);
            double devMm = sent[i].DistanceTo(back) * 1000.0;
            if (devMm > maxDevMm) maxDevMm = devMm;
        }
        return maxDevMm;
    }

    private static GpsPoint[] SyntheticRoute(int count)
    {
        var points = new GpsPoint[count];
        for (int i = 0; i < count; i++)
            points[i] = new GpsPoint(StartLat + i * StepDeg, StartLon);
        return points;
    }

    private static void WriteCsv(string path, IEnumerable<RouteFidelityRow> rows)
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

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
