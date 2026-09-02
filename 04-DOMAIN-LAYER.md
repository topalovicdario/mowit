# 04 — THE DOMAIN LAYER

`MowIT/Domain/` — the innermost ring. **Zero external dependencies.** This is the part you could hand to a mathematician and they would understand it without knowing what MAUI is.

---

## 1. Entities

### 1.1 `GpsPoint` — [`Domain/Entities/GpsPoint.cs`](MowIT/Domain/Entities/GpsPoint.cs)

```csharp
public readonly record struct GpsPoint(double Latitude, double Longitude)
```

**Plain:** a place on Earth, in degrees.

**Technically — three deliberate choices in one line:**
* `struct` → it is a small value (16 bytes). Thousands of them are created when planning a route; making it a class would produce thousands of heap allocations and GC pressure.
* `readonly` → immutable; you cannot accidentally mutate a point that is stored inside a polygon.
* `record` → the compiler generates value equality (`a == b` compares latitude and longitude, not references), `GetHashCode`, deconstruction and `with`-expressions. Used e.g. in [`BoundaryZoneBuilder.Close()`](MowIT/Domain/Builders/BoundaryZoneBuilder.cs#L34) which compares `_points[0] != _points[^1]`.

**`DistanceTo` — the haversine formula:**

```csharp
const double R = 6371000;                        // mean Earth radius in metres
double dLat = (other.Latitude  - Latitude)  * π/180;
double dLon = (other.Longitude - Longitude) * π/180;
double a = sin²(dLat/2) + cos(lat₁)·cos(lat₂)·sin²(dLon/2);
return R * 2 * atan2(√a, √(1-a));
```

*Plain:* the shortest distance between two points **over the curved surface of the Earth**, in metres. Used to decide "has the robot reached the next waypoint?" (`< 0.30 m`), "has the robot moved far enough to add another trail point?" (`> 0.3 m` / `> 0.5 m`), and to compute total route length on the Map page.

*Why haversine and not Pythagoras?* Because subtracting degrees is meaningless as a distance — a degree of longitude shrinks by `cos(latitude)`. Haversine is the standard great-circle formula and is numerically stable for small distances (the `atan2` form avoids the precision loss the simpler `acos` form suffers at short range).

**`BearingTo` — initial great-circle bearing:**

```csharp
y = sin(Δlon)·cos(lat₂)
x = cos(lat₁)·sin(lat₂) − sin(lat₁)·cos(lat₂)·cos(Δlon)
bearing = (atan2(y,x)·180/π + 360) mod 360
```

*Plain:* "which compass direction do I have to face to head towards that point?", normalised to 0–360°. The simulator uses it in `FollowRouteStep` to point the virtual robot at the next waypoint.

---

### 1.2 `LocalPoint` — [`Domain/Entities/LocalPoint.cs`](MowIT/Domain/Entities/LocalPoint.cs)

```csharp
public readonly record struct LocalPoint(float XCm, float YCm)
{
    public float XMeters => XCm / 100f;
    public float YMeters => YCm / 100f;
}
```

**Plain:** a position on a flat map measured in **centimetres east and north of the base point**. This exists because the GreenTitan firmware reports positions this way (`MOWER/CAPTURE/POINT/OK/1234,-560`). `float` is enough: ±32 000 cm = ±320 m at full float precision, far bigger than any garden.

---

### 1.3 `BoundaryZone` — [`Domain/Entities/BoundaryZone.cs`](MowIT/Domain/Entities/BoundaryZone.cs)

The richest entity in the project. It is a named, timestamped **polygon** of `GpsPoint`s, and it carries four pieces of real geometry.

```csharp
public int Id; public string Name; public List<GpsPoint> Points; public DateTime CreatedAt;
public bool IsValid => Points.Count >= 3;         // a polygon needs at least a triangle
```

#### `Contains(GpsPoint)` — the ray-casting point-in-polygon test

```csharp
var proj    = new LocalProjection(Points[0]);          // 1. flatten to metres
var polygon = Points.Select(proj.ToLocal).ToList();
var (px,py) = proj.ToLocal(point);

bool inside = false;
for (int i = 0, j = n - 1; i < n; j = i++)
{
    var (xi,yi) = polygon[i];  var (xj,yj) = polygon[j];
    bool intersects = ((yi > py) != (yj > py))
                   && (px < (xj - xi) * (py - yi) / (yj - yi) + xi);
    if (intersects) inside = !inside;
}
return inside;
```

**Plain explanation you can give verbally:** imagine standing at the point and shooting a ray straight to the left (towards −∞ east). Count how many edges of the polygon that ray crosses. **Odd = inside, even = outside.** That is the whole algorithm.

**Line by line:**
* `(yi > py) != (yj > py)` — "does this edge straddle my horizontal line?" One endpoint must be above and the other below. This half-open comparison also elegantly handles the case where the ray passes exactly through a vertex (it is counted once, not twice).
* `(xj-xi)*(py-yi)/(yj-yi) + xi` — linear interpolation: the east-coordinate at which the edge crosses my latitude line.
* `px < …` — is that crossing to the *right* of me? Then the ray shot left does not hit it… (this variant shoots right; either direction works as long as it is consistent).
* `inside = !inside` — flip the parity.

Complexity **O(n)**, no trigonometry, no allocations beyond the projection. Called at 2 Hz by the geofence.

#### `DistanceToBoundaryMeters(GpsPoint)`

Projects to metres, then computes the minimum **point-to-segment** distance over all edges:

```csharp
double t = ((px-ax)·dx + (py-ay)·dy) / |d|²;   // projection parameter along the segment
t = clamp(t, 0, 1);                            // clamp so we stay ON the segment
closest = a + t·d;
return |p - closest|;
```

*Plain:* drop a perpendicular from the point onto the infinite line through the edge; if the foot of the perpendicular falls outside the segment, use the nearer endpoint instead. That is what `Math.Clamp(t,0,1)` does. Used by `GeofenceMonitor` to log **how many metres past the fence** the robot got — which is exactly the number that tells you whether a breach was real or a GPS glitch.

#### `AreaSquareMeters()` — the shoelace formula

```csharp
area = ½ |Σ (xᵢ·yᵢ₊₁ − xᵢ₊₁·yᵢ)|
```

*Plain:* walk around the polygon multiplying coordinates crosswise; the sum is twice the signed area. `Math.Abs` removes the sign so the winding direction (clockwise/anticlockwise) does not matter. Because the points were first projected into metres, the result is genuinely square metres — you could not do this on raw lat/lon.

#### `GetChunkedEnumerator(int chunkSize)`

A `yield return` iterator that hands out the points in batches. Written for chunked BLE upload; **currently unused** (BLE uploads one point per 20-byte packet). Be honest about that if asked.

---

### 1.4 The rest of the entities

| Entity | Fields | Notes |
|---|---|---|
| [`MowerDevice`](MowIT/Domain/Entities/MowerDevice.cs) | `Guid Id`, `string Name`, `int Rssi`, `RssiLabel` | One discovered device. On Android SPP the MAC address is packed into the Guid (`MacToGuid`); on WiFi the Guid is an MD5 of the robot id — a deterministic identity so re-scans do not duplicate rows |
| [`RobotStatus`](MowIT/Domain/Entities/RobotStatus.cs) | `State, BatteryPct, BladeOn, RainDetected, UptimeMinutes, Timestamp` | `record` with `init` setters → immutable snapshot; updated with `with { }` |
| [`SensorSnapshot`](MowIT/Domain/Entities/SensorSnapshot.cs) | GPS + accuracy + fix, IMU (`AccX/Y/Z`, `GyroX/Y/Z`), odometry (`PosX/PosY/HeadingRad/LinearSpeed`), `IsManualMode`, `IsMotorMoving`, `Timestamp` | **The single "current truth" object.** Because it is a record, partial updates are done with `existing with { AccX = … }` — three separate BLE characteristics can each merge their part into one snapshot without losing the others |
| [`MowingSchedule`](MowIT/Domain/Entities/MowingSchedule.cs) | `ActiveDays[]`, `StartTime`, `DurationMinutes`, `IsActive`, `LastExecuted`, `ZoneName`, `ZoneId?` | Implements `ICloneable`; has display helpers `DaysLabel` ("Mon, Wed, Fri") and `ZoneLabel`. `ZoneId` is nullable = "no zone linked, use the robot's onboard boundary" |
| [`MowingRoute`](MowIT/Domain/Entities/MowingRoute.cs) | `Id, Name, Waypoints, GeneratedAt` | A planned path. Defined for completeness; routes are currently passed around as bare `List<GpsPoint>` |

**Why records with `init` for telemetry, but classes with `set` for zones/schedules?** Telemetry is a *snapshot of an instant* — it must never change after creation, and value-equality lets Rx operators de-duplicate. Zones and schedules are *edited by the user* and are given an `Id` by the database after insert, so they need mutability.

---

## 2. Enums — [`Domain/Enums/`](MowIT/Domain/Enums/)

```csharp
public enum RobotState : byte {
    Idle=0, Mowing=1, Paused=2, Returning=3, Docking=4,
    Charging=5, Leaving=6, RecordingBoundary=7, Error=255 }

public enum RobotAction : byte {
    StartMowing=0x01, Pause=0x02, Resume=0x03, Stop=0x04, ReturnToBase=0x05,
    BladeOn=0x06, BladeOff=0x07,
    BoundaryRecordStart=0x08, BoundaryCapturePoint=0x09, BoundaryRecordEnd=0x0A,
    BoundaryClear=0x0B, StartRoute=0x0C,
    ManualModeOn=0x0D, ManualModeOff=0x0E,
    CaptureBase=0x0F, CaptureOutline=0x10, CaptureExit=0x11 }

public enum GpsFixType : byte { NoFix=0, Standard=1, RtkFloat=2, RtkFixed=3 }

public enum RobotConnectionState { Disconnected, Scanning, Connecting, Connected }
```

**Why `: byte` and why explicit values?** Because these enums **are** the wire format for the BLE transport: `SerializeActionCommand` writes `new[]{ (byte)action, param }` and `DeserializeStatus` reads `(RobotState)data[0]`. The numbers are therefore part of the protocol contract with the firmware and must never be reordered. `Error = 255` is placed at the top of the byte range on purpose so it can never collide with a future state.

`GpsFixType` is ordered by quality, so `fix >= GpsFixType.RtkFloat` is a meaningful comparison.

---

## 3. `LocalProjection` — the geometry engine

[`Domain/Geometry/LocalProjection.cs`](MowIT/Domain/Geometry/LocalProjection.cs) — the most mathematically serious file in the repository. Learn this one properly; it is the best "deep understanding" question you can be asked.

### 3.1 What problem it solves

You cannot do geometry on latitude/longitude. `lat2 - lat1` is in degrees; converting to metres needs a different factor for latitude (≈111 320 m/°, roughly constant) than for longitude (111 320 · cos(lat), which is 111 km at the equator and 0 at the poles). And for a task like "is this point inside this polygon" you want a genuine flat plane.

The standard solution in geodesy is a **local tangent plane** in **ENU (East, North, Up)** coordinates, anchored at a chosen origin.

### 3.2 The constants — the WGS-84 ellipsoid

```csharp
const double A   = 6_378_137.0;              // semi-major axis (equatorial radius), metres
const double F   = 1.0 / 298.257223563;      // flattening
const double E2  = F * (2.0 - F);            // first eccentricity squared
const double B   = A * (1.0 - F);            // semi-minor axis (polar radius)
const double Ep2 = (A*A - B*B) / (B*B);      // second eccentricity squared
```

*Plain:* the Earth is not a sphere; it is squashed by about 1 part in 298. These five numbers are the official WGS-84 definition — the same ellipsoid GPS itself uses. Using a sphere instead would introduce ~0.3 % error, i.e. **tens of centimetres over a 100 m garden** — unacceptable for a mower.

### 3.3 Step 1: geodetic → ECEF

ECEF = *Earth-Centred, Earth-Fixed*: a 3-D Cartesian system whose origin is the centre of the Earth, Z through the north pole, X through (0°N, 0°E).

```csharp
double n = A / sqrt(1 − E2·sin²(lat));       // radius of curvature in the prime vertical
x = n·cos(lat)·cos(lon);
y = n·cos(lat)·sin(lon);
z = n·(1 − E2)·sin(lat);
```

The `(1 − E2)` on Z is precisely the ellipsoid squash. (Height above the ellipsoid is assumed 0 — fine for a lawn.)

### 3.4 Step 2: ECEF → ENU (rotate into the local tangent plane)

The constructor pre-computes and caches the origin's ECEF position and its four trig values:

```csharp
_sinLat, _cosLat, _sinLon, _cosLon;  (_x0,_y0,_z0) = GeodeticToEcef(originLat, originLon);
```

Then for any point:

```csharp
dx = x − _x0;  dy = y − _y0;  dz = z − _z0;

east  = −sinLon·dx + cosLon·dy;
north = −sinLat·cosLon·dx − sinLat·sinLon·dy + cosLat·dz;
```

*Plain:* subtract the origin (translate), then rotate the axes so that "X" points east and "Y" points north **at that spot on the globe**. Those two lines are the first two rows of the standard ECEF→ENU rotation matrix; the third row (Up) is discarded because the lawn is treated as flat.

**Performance note worth mentioning:** the four sines/cosines of the *origin* are computed **once in the constructor**. `ToLocal` is then 3 subtractions + 6 multiplications — no trigonometry at all. When `BoustrophedonStrategy` projects thousands of route points, that caching matters.

### 3.5 Step 3: the inverse, `ToGps(east, north)`

Applies the transposed rotation and then converts ECEF back to geodetic using **Bowring's formula**:

```csharp
p     = sqrt(x² + y²);
theta = atan2(z·A, p·B);
lat   = atan2(z + Ep2·B·sin³θ,  p − E2·A·cos³θ);
lon   = atan2(y, x);
```

*Plain:* going from Cartesian back to latitude is not algebraically solvable in closed form on an ellipsoid; Bowring's method is a famous **non-iterative approximation** that is accurate to well under a millimetre for points near the surface. That is why it is one line instead of a Newton loop.

**Where the inverse is used:** the mowing strategies plan the route in flat metres and then call `proj.ToGps(east, north)` on every waypoint to turn it back into GPS before sending it to the robot. Also `ControlViewModel.BuildWalkedGpsZone` converts robot-reported centimetre points back into a GPS polygon so the geofence can use it.

### 3.6 The property that makes this correct

`ToLocal(origin)` returns exactly `(0,0)`, and `ToGps(0,0)` returns the origin. Every consumer builds the projection with `new LocalProjection(zone.Points[0])` or `new LocalProjection(baseGps)` — i.e. the origin is always a real point *inside* the working area, which keeps the tangent-plane approximation error at essentially zero over garden distances.

---

## 4. Mowing strategies — the Strategy pattern

```csharp
public interface IMowingStrategy {
    string Name { get; }
    List<GpsPoint> GenerateRoute(BoundaryZone zone, float spacingMeters = 0.3f);
}
```

Two implementations exist. Both follow the same three-phase shape: **project to metres → do 2-D geometry → project back to GPS.**

### 4.1 `BoustrophedonStrategy` — [`Domain/Strategies/BoustrophedonStrategy.cs`](MowIT/Domain/Strategies/BoustrophedonStrategy.cs)

> *Boustrophedon* is Greek for "as the ox turns while ploughing" — the up-down-up-down pattern.

```
   ┌─────────────────────┐
   │ ────────────────▶   │   row 4  (left → right)
   │   ◀────────────────  │   row 3  (right → left)
   │ ────────────────▶   │   row 2
   │   ◀────────────────  │   row 1
   └─────────────────────┘
```

**Algorithm:**
1. Project the polygon into ENU metres.
2. Find `minNorth` and `maxNorth` — the vertical extent.
3. Sweep a horizontal scan-line from `minNorth + spacing/2` upwards in steps of `spacing`.
4. For each scan-line, `RowCrossings` finds every polygon edge that straddles it and computes the east-coordinate of the crossing by linear interpolation:
   ```csharp
   if ((a.North <= north && b.North > north) || (b.North <= north && a.North > north)) {
       double t = (north − a.North) / (b.North − a.North);
       crossings.Add(a.East + t*(b.East − a.East));
   }
   ```
5. `crossings.Sort()` — now they are in left-to-right order. Taking them **in pairs** `(0,1), (2,3), …` gives exactly the *interior* spans. **This is what makes the algorithm handle concave (L-shaped, U-shaped) gardens correctly** — a U-shaped lawn produces 4 crossings on some rows, and the pairs are the two separate strips.
6. `if (!leftToRight) crossings.Reverse();` then flip the flag → alternate direction each row, so the robot snakes instead of teleporting back to the left edge.
7. Each span is sub-divided into intermediate points every `spacing` metres, so the robot gets dense waypoints it can track rather than just two endpoints.
8. Project every point back to GPS.

**Complexity:** O(rows × edges). For a 20 m × 20 m lawn at 0.5 m spacing that is 40 rows × ~5 edges — instant.

**Known limitation to admit:** the rows are always **east–west**. A long, narrow, diagonally-oriented garden would be mowed less efficiently than if the rows followed its long axis. The classic fix is to rotate the polygon to align with its minimum-area bounding box, mow, then rotate back — a good "future work" answer.

### 4.2 `SpiralInwardStrategy` — [`Domain/Strategies/SpiralInwardStrategy.cs`](MowIT/Domain/Strategies/SpiralInwardStrategy.cs)

```
   ┌─────────────────┐
   │ ┌─────────────┐ │
   │ │ ┌─────────┐ │ │
   │ │ │   ···   │ │ │
   │ │ └─────────┘ │ │
   │ └─────────────┘ │
   └─────────────────┘
```

```csharp
while (shell.Count >= 3) { route.AddRange(shell); shell = Shrink(shell, spacing); }
```

`Shrink` computes the **centroid as the arithmetic mean of the vertices**, then moves every vertex a fixed number of metres towards that centroid:

```csharp
dist  = |vertex − centre|;
if (dist <= amountMeters) return [];          // terminate: the ring has collapsed
scale = (dist − amountMeters) / dist;
shrunk = centre + (vertex − centre) * scale;
```

*Plain:* draw the outline, then draw a slightly smaller copy of the outline inside it, and repeat until nothing is left.

**Honest limitation:** this is a *radial* shrink towards the centroid, not a true geometric **polygon offset** (Minkowski erosion). For a convex, roughly round shape they are nearly identical. For a very elongated or concave polygon a radial shrink can self-intersect or leave uncut corners. A real product would use Clipper/`NetTopologySuite`'s `Buffer(-d)`. Saying this out loud demonstrates that you know the difference — it is a much stronger answer than pretending it is exact.

### 4.3 `MowingRoutePlanner` — choosing and tuning

[`Application/Services/MowingRoutePlanner.cs`](MowIT/Application/Services/MowingRoutePlanner.cs) is the Application-layer front end for the strategies (documented fully in chapter 5), but the interesting maths belongs here:

```csharp
public static float AdaptiveSpacing(IReadOnlyList<GpsPoint> points)
{
    var proj  = new LocalProjection(points[0]);
    var local = points.Select(proj.ToLocal).ToList();
    double widthM  = local.Max(p => p.East)  − local.Min(p => p.East);
    double heightM = local.Max(p => p.North) − local.Min(p => p.North);
    double areaM2  = Math.Max(1.0, widthM * heightM);
    double spacing = Math.Sqrt(areaM2 / MaxRouteWaypoints);   // MaxRouteWaypoints = 2500
    return (float)Math.Max(RowSpacingMeters, spacing);        // RowSpacingMeters = 0.5
}
```

**Plain:** *"never generate more than about 2500 waypoints, and never mow rows closer than 50 cm."*

**Why:** if you naively use 0.3 m spacing on a 100 m × 100 m field you get ~110 000 waypoints. Over BLE at 50 ms per 20-byte packet that upload would take **92 minutes**. The adaptive rule caps the count by widening the spacing on large fields, while the 0.5 m floor guarantees the blade (which is wider than 50 cm on a real mower) still overlaps between rows so no grass is missed. This is a genuine engineering trade-off — exactly the sort of thing a professor wants to hear you justify.

---

## 5. `BoundaryZoneBuilder` — the Builder pattern

[`Domain/Builders/BoundaryZoneBuilder.cs`](MowIT/Domain/Builders/BoundaryZoneBuilder.cs)

```csharp
var zone = new BoundaryZoneBuilder()
    .Named("Front lawn")
    .AddPoint(43.8563, 18.4131)
    .AddPoint(43.8564, 18.4133)
    .AddPoint(43.8562, 18.4134)
    .Close()          // appends the first point again if the ring is open
    .Build();         // throws if fewer than 3 points
```

Fluent interface: every method returns `this`. `Build()` enforces the invariant `Points.Count >= 3` by throwing `InvalidOperationException`, so an invalid zone can never be constructed through the builder.

**Honest note:** this class is **not currently called anywhere** in the app — zones are built with object initialisers in the ViewModels. It is a demonstration of the pattern and a ready-made API. If the professor spots it, say exactly that; do not pretend it is on the hot path.

---

## 6. The domain interfaces (all 11)

| Interface | Purpose |
|---|---|
| `IRobotScanner` | Discover mowers |
| `IRobotConnection` | Connect / disconnect / observe link state |
| `IRobotSensors` | Observe telemetry streams |
| `IRobotControl` | Send motor + action commands |
| `IRobotBoundary` | Upload boundaries and routes |
| `IBoundaryRepository` | CRUD for `BoundaryZone` |
| `IScheduleRepository` | CRUD for `MowingSchedule` |
| `IScheduleSyncService` | Push/delete schedules to the cloud (`IsEnabled` lets callers skip cheaply) |
| `IMowingStrategy` | Generate a route inside a zone |
| `IBlePermissionService` | Ask the OS for Bluetooth permission / check the radio is on |
| `IRobotServiceFactory` | Register a whole transport stack into the DI container |

Note that `IRobotServiceFactory` takes `IServiceCollection` — that is the one place where a Domain interface touches a Microsoft.Extensions type. Strictly, a purist would move that interface to Infrastructure. Another honest, easy-to-defend point.
