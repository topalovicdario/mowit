using System.Diagnostics;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Benchmarks;

public sealed record TimingResult(
    int RoutePoints, double RouteLengthM, int Turns,
    double MeanMs, double MedianMs, double StdDevMs, double MinMs, double MaxMs);

public static class TimingMeter
{
    private const int WarmupRuns = 3;
    private const int Repetitions = 20;

    public static TimingResult Measure(IMowingStrategy strategy, BoundaryZone zone, double spacingMeters)
    {
        float spacing = (float)spacingMeters;

        for (int i = 0; i < WarmupRuns; i++) _ = strategy.GenerateRoute(zone, spacing);

        var samples = new double[Repetitions];
        List<GpsPoint> route = [];

        for (int i = 0; i < Repetitions; i++)
        {
            var sw = Stopwatch.StartNew();
            route = strategy.GenerateRoute(zone, spacing);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);

        double mean   = samples.Average();
        double median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

        double variance = samples.Sum(v => (v - mean) * (v - mean)) / (samples.Length - 1);

        return new TimingResult(
            route.Count,
            CoverageMeter.RouteLength(zone, route),
            CoverageMeter.CountTurns(zone, route),
            mean, median, Math.Sqrt(variance), sorted[0], sorted[^1]);
    }
}
