using System.Runtime.CompilerServices;
using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;

namespace MowIT.Benchmarks;

public sealed record CoverageResult(
    double CoveragePct, double OverlapPct, double OutsidePct,
    int InsideCells, int CoveredCells, double CellSizeM, double SwathWidthM);

public sealed record CoverageRaster(
    CoverageResult Result, int[] Cells,
    double MinEastM, double MinNorthM, int Nx, int Ny, double CellSizeM);

public static class CoverageMeter
{
    private static readonly Dictionary<GridKey, InsideMask> MaskCache = new();

    private readonly record struct GridKey(BoundaryZone Zone, double CellSizeM, double SwathWidthM);

    private sealed record InsideMask(
        double MinEast, double MinNorth, int Nx, int Ny, bool[] Inside, int InsideCount);

    public static CoverageResult Measure(
        BoundaryZone zone, IReadOnlyList<GpsPoint> route,
        double swathWidthM, double cellSizeM = 0.05)
        => Compute(zone, route, swathWidthM, cellSizeM, withCells: false).Result;

    public static CoverageRaster MeasureWithRaster(
        BoundaryZone zone, IReadOnlyList<GpsPoint> route,
        double swathWidthM, double cellSizeM = 0.05)
        => Compute(zone, route, swathWidthM, cellSizeM, withCells: true);

    private static CoverageRaster Compute(
        BoundaryZone zone, IReadOnlyList<GpsPoint> route,
        double swathWidthM, double cellSizeM, bool withCells)
    {
        var mask = GetMask(zone, swathWidthM, cellSizeM);

        int[] cells = withCells ? new int[mask.Nx * mask.Ny] : [];
        if (withCells)
            for (int i = 0; i < cells.Length; i++) cells[i] = mask.Inside[i] ? 0 : -1;

        if (mask.InsideCount == 0)
            return Wrap(new CoverageResult(0, 0, 0, 0, 0, cellSizeM, swathWidthM), mask, cells, cellSizeM);

        var proj = new LocalProjection(zone.Points[0]);
        var pts  = new (double East, double North)[route.Count];
        for (int i = 0; i < route.Count; i++) pts[i] = proj.ToLocal(route[i]);

        int segmentCount = Math.Max(0, pts.Length - 1);
        if (segmentCount == 0)
            return Wrap(new CoverageResult(0, 0, 0, mask.InsideCount, 0, cellSizeM, swathWidthM),
                        mask, cells, cellSizeM);

        double half   = swathWidthM / 2.0;
        double half2  = half * half;
        var    buckets = SegmentGrid.Build(pts, mask.MinEast, mask.MinNorth, mask.Nx, mask.Ny, cellSizeM, swathWidthM);

        var stamp = new int[segmentCount];
        int generation = 0;

        var hitBuffer = new List<int>(32);

        int coveredInside = 0, coveredOutside = 0;
        long overlapSum = 0;

        for (int gy = 0; gy < mask.Ny; gy++)
        {
            double cy = mask.MinNorth + (gy + 0.5) * cellSizeM;
            int rowBase = gy * mask.Nx;

            for (int gx = 0; gx < mask.Nx; gx++)
            {
                double cx = mask.MinEast + (gx + 0.5) * cellSizeM;
                generation++;

                int hits = buckets.CountCovering(cx, cy, half2, pts, stamp, generation, hitBuffer);
                if (hits == 0) continue;

                if (mask.Inside[rowBase + gx])
                {
                    coveredInside++;
                    overlapSum += hits;
                    if (withCells) cells[rowBase + gx] = hits;
                }
                else
                {
                    coveredOutside++;
                    if (withCells) cells[rowBase + gx] = -2;
                }
            }
        }

        double coveragePct = 100.0 * coveredInside / mask.InsideCount;
        double overlapPct  = coveredInside == 0 ? 0 : 100.0 * (overlapSum - coveredInside) / coveredInside;
        double outsidePct  = coveredInside == 0 ? 0 : 100.0 * coveredOutside / coveredInside;

        var result = new CoverageResult(
            coveragePct, overlapPct, outsidePct,
            mask.InsideCount, coveredInside, cellSizeM, swathWidthM);

        return Wrap(result, mask, cells, cellSizeM);
    }

    private static CoverageRaster Wrap(CoverageResult result, InsideMask mask, int[] cells, double cellSizeM) =>
        new(result, cells, mask.MinEast, mask.MinNorth, mask.Nx, mask.Ny, cellSizeM);

    private static InsideMask GetMask(BoundaryZone zone, double swathWidthM, double cellSizeM)
    {
        var key = new GridKey(zone, cellSizeM, swathWidthM);
        if (MaskCache.TryGetValue(key, out var cached)) return cached;

        var proj  = new LocalProjection(zone.Points[0]);
        var local = zone.Points.Select(proj.ToLocal).ToList();

        double half = swathWidthM / 2.0;
        double minE = local.Min(p => p.East)  - half;
        double maxE = local.Max(p => p.East)  + half;
        double minN = local.Min(p => p.North) - half;
        double maxN = local.Max(p => p.North) + half;

        int nx = Math.Max(1, (int)Math.Ceiling((maxE - minE) / cellSizeM));
        int ny = Math.Max(1, (int)Math.Ceiling((maxN - minN) / cellSizeM));

        var inside = new bool[nx * ny];
        int count = 0;

        for (int gy = 0; gy < ny; gy++)
        {
            double cy = minN + (gy + 0.5) * cellSizeM;
            int rowBase = gy * nx;

            for (int gx = 0; gx < nx; gx++)
            {
                double cx = minE + (gx + 0.5) * cellSizeM;
                if (!zone.Contains(proj.ToGps(cx, cy))) continue;
                inside[rowBase + gx] = true;
                count++;
            }
        }

        var mask = new InsideMask(minE, minN, nx, ny, inside, count);
        MaskCache[key] = mask;
        return mask;
    }

    private sealed class SegmentGrid
    {
        private readonly double _minE, _minN, _cell;
        private readonly int _nx, _ny;
        private readonly int[] _start;
        private readonly int[] _items;

        private SegmentGrid(double minE, double minN, double cell, int nx, int ny, int[] start, int[] items)
        {
            _minE = minE; _minN = minN; _cell = cell; _nx = nx; _ny = ny; _start = start; _items = items;
        }

        public static SegmentGrid Build(
            (double East, double North)[] pts,
            double minE, double minN, int cellsX, int cellsY, double cellSizeM, double bucketSizeM)
        {
            double spanE = cellsX * cellSizeM, spanN = cellsY * cellSizeM;
            int nx = Math.Max(1, (int)Math.Ceiling(spanE / bucketSizeM));
            int ny = Math.Max(1, (int)Math.Ceiling(spanN / bucketSizeM));

            int segments = pts.Length - 1;
            var counts = new int[nx * ny + 1];
            var hit    = new List<int>(32);

            for (int s = 0; s < segments; s++)
            {
                Traverse(pts[s], pts[s + 1], minE, minN, bucketSizeM, nx, ny, hit);
                foreach (int b in hit) counts[b + 1]++;
            }

            for (int i = 1; i < counts.Length; i++) counts[i] += counts[i - 1];

            var start  = counts;
            var cursor = (int[])start.Clone();
            var items  = new int[start[^1]];

            for (int s = 0; s < segments; s++)
            {
                Traverse(pts[s], pts[s + 1], minE, minN, bucketSizeM, nx, ny, hit);
                foreach (int b in hit) items[cursor[b]++] = s;
            }

            return new SegmentGrid(minE, minN, bucketSizeM, nx, ny, start, items);
        }

        public int CountCovering(
            double px, double py, double half2,
            (double East, double North)[] pts, int[] stamp, int generation, List<int> hitBuffer)
        {
            int bx = (int)((px - _minE) / _cell);
            int by = (int)((py - _minN) / _cell);

            int x0 = Math.Max(0, bx - 1), x1 = Math.Min(_nx - 1, bx + 1);
            int y0 = Math.Max(0, by - 1), y1 = Math.Min(_ny - 1, by + 1);

            hitBuffer.Clear();
            for (int y = y0; y <= y1; y++)
            {
                int rowBase = y * _nx;
                for (int x = x0; x <= x1; x++)
                {
                    int b = rowBase + x;
                    for (int k = _start[b]; k < _start[b + 1]; k++)
                    {
                        int seg = _items[k];
                        if (stamp[seg] == generation) continue;
                        stamp[seg] = generation;

                        if (DistanceSquaredToSegment(px, py, pts[seg], pts[seg + 1]) <= half2)
                            hitBuffer.Add(seg);
                    }
                }
            }

            if (hitBuffer.Count == 0) return 0;
            hitBuffer.Sort();

            int passes = 1;
            for (int i = 1; i < hitBuffer.Count; i++)
                if (hitBuffer[i] - hitBuffer[i - 1] > 1) passes++;

            return passes;
        }

        private static void Traverse(
            (double East, double North) a, (double East, double North) b,
            double minE, double minN, double cell, int nx, int ny, List<int> visited)
        {
            visited.Clear();

            double ax = (a.East - minE) / cell, ay = (a.North - minN) / cell;
            double bx = (b.East - minE) / cell, by = (b.North - minN) / cell;

            int cx = Clamp((int)Math.Floor(ax), nx), cy = Clamp((int)Math.Floor(ay), ny);
            int ex = Clamp((int)Math.Floor(bx), nx), ey = Clamp((int)Math.Floor(by), ny);

            visited.Add(cy * nx + cx);
            if (cx == ex && cy == ey) return;

            double dx = bx - ax, dy = by - ay;
            int stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0;
            int stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;

            double tMaxX = stepX == 0 ? double.PositiveInfinity
                         : ((stepX > 0 ? cx + 1 : cx) - ax) / dx;
            double tMaxY = stepY == 0 ? double.PositiveInfinity
                         : ((stepY > 0 ? cy + 1 : cy) - ay) / dy;

            double tDeltaX = stepX == 0 ? double.PositiveInfinity : Math.Abs(1.0 / dx);
            double tDeltaY = stepY == 0 ? double.PositiveInfinity : Math.Abs(1.0 / dy);

            int guard = nx + ny + 2;
            while (guard-- > 0 && (cx != ex || cy != ey))
            {
                if (tMaxX < tMaxY) { cx += stepX; tMaxX += tDeltaX; }
                else               { cy += stepY; tMaxY += tDeltaY; }

                if (cx < 0 || cy < 0 || cx >= nx || cy >= ny) break;
                visited.Add(cy * nx + cx);
            }
        }

        private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double DistanceSquaredToSegment(
        double px, double py, (double East, double North) a, (double East, double North) b)
    {
        double dx = b.East - a.East, dy = b.North - a.North;
        double len2 = dx * dx + dy * dy;

        double t = len2 <= 0 ? 0 : ((px - a.East) * dx + (py - a.North) * dy) / len2;
        if (t < 0) t = 0; else if (t > 1) t = 1;

        double qx = a.East + t * dx - px, qy = a.North + t * dy - py;
        return qx * qx + qy * qy;
    }

    public static double RouteLength(BoundaryZone zone, IReadOnlyList<GpsPoint> route)
    {
        if (route.Count < 2) return 0;

        var proj = new LocalProjection(zone.Points[0]);
        var prev = proj.ToLocal(route[0]);

        double total = 0;
        for (int i = 1; i < route.Count; i++)
        {
            var cur = proj.ToLocal(route[i]);
            double dx = cur.East - prev.East, dy = cur.North - prev.North;
            total += Math.Sqrt(dx * dx + dy * dy);
            prev = cur;
        }
        return total;
    }

    public static int CountTurns(BoundaryZone zone, IReadOnlyList<GpsPoint> route, double thresholdDeg = 45.0)
    {
        if (route.Count < 3) return 0;

        var proj  = new LocalProjection(zone.Points[0]);
        var local = new (double East, double North)[route.Count];
        for (int i = 0; i < route.Count; i++) local[i] = proj.ToLocal(route[i]);

        int turns = 0;
        double? previousHeading = null;

        for (int i = 1; i < local.Length; i++)
        {
            double dx = local[i].East - local[i - 1].East;
            double dy = local[i].North - local[i - 1].North;
            if (dx == 0 && dy == 0) continue;

            double heading = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (previousHeading is double prev)
            {
                double delta = heading - prev;
                while (delta > 180) delta -= 360;
                while (delta < -180) delta += 360;
                if (Math.Abs(delta) > thresholdDeg) turns++;
            }
            previousHeading = heading;
        }
        return turns;
    }
}
