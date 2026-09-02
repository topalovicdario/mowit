# 08 — THE SERVER SIDE

Two projects live outside the app: **`MowIT.ScheduleServer`** (the backend) and **`MowIT.RobotSimulator`** (a fake robot that behaves like a real one on the network). They speak through **`MowIT.Shared`**.

---

## 1. Why a server exists at all

Bluetooth requires you to be ~10 m from the mower. Two things need more range:

1. **Schedules must survive.** If you set "mow every Tuesday at 08:00" on your phone and then leave the phone at work, the schedule should still be somewhere the robot can read it.
2. **Remote control.** You should be able to check on the mower, or stop it, from anywhere.

A home robot on a domestic WiFi has **no public address** — you cannot open a connection *to* it. Therefore the design is a **mailbox / relay**:

```
   APP                              SERVER                             ROBOT
    │  POST /commands  ───────────▶ [ command queue ]  ◀─────────────  GET /commands   (poll 2 Hz)
    │  GET  /telemetry ◀─────────── [ latest telemetry ] ◀───────────  POST /telemetry (push 2 Hz)
    │  GET  /events?since=N ◀────── [ event log + seq ] ◀────────────  POST /events
    │  POST /schedules ───────────▶ [ SQLite, versioned ] ◀──────────  GET  /schedules (poll 0.2 Hz)
```

Both sides only ever make **outbound** HTTP calls. No firewall configuration, no port forwarding, no WebSocket infrastructure. That is the key architectural insight of the cloud half of this project.

**The server never makes a decision.** It stores and forwards. All logic lives in the app (planning, geofencing, scheduling triggers) and in the robot (motion, safety). This keeps the backend stateless-ish, trivially testable and easy to explain.

---

## 2. `MowIT.Shared` — the contract

[`MowIT.Shared/`](MowIT.Shared/) — plain `net8.0`, **no package references**, only `record` types. Both the app and the server compile against it, so a change to a field breaks the build on both sides at compile time rather than at runtime. This is the compile-time equivalent of an OpenAPI schema.

### 2.1 `TelemetryDto` — [file](MowIT.Shared/Telemetry/TelemetryDto.cs)

The robot's complete state in one flat record: `Lat`, `Lon`, `GpsAccuracyMm`, `GpsFixType (int)`, `HeadingRad`, `PosX`, `PosY`, `LinearSpeed`, `AccX/Y/Z`, `GyroX/Y/Z`, `IsManualMode`, `IsMotorMoving`, `BatteryPct`, `State (int)`, `BladeOn`, `RainDetected`, `UptimeMinutes`, `TimestampUtc`.

**Note the enums are `int`, not the C# enum types.** Deliberate: the DTO must not depend on `MowIT.Domain`, and an integer is a stable wire representation. `WifiRobotService` casts back: `(GpsFixType)t.GpsFixType`, `(RobotState)t.State`.

### 2.2 `TelemetryEnvelope` — [file](MowIT.Shared/Telemetry/TelemetryEnvelope.cs)

```csharp
public sealed record TelemetryEnvelope {
    public string RobotId; public bool IsOnline; public DateTime LastSeenUtc; public TelemetryDto? Latest; }
public sealed record ActiveRobotsResponse { public TelemetryEnvelope[] Robots = []; }
```
The envelope adds **liveness** metadata around the payload. `Latest` is nullable because a robot may be registered but currently silent.

### 2.3 `RobotCommandDto` — [file](MowIT.Shared/Telemetry/RobotCommandDto.cs)

```csharp
public sealed record RobotCommandDto {
    public Guid Id = Guid.NewGuid();  public string Kind = CommandKinds.Action;
    public string? ActionName;  public int ActionCode;  public int Param;
    public float LinearVel, AngularVel;  public BoundaryUploadDto? Boundary;
    public DateTime CreatedUtc = DateTime.UtcNow; }

public static class CommandKinds { public const string Action="Action", Motor="Motor", Boundary="Boundary"; }
```
One envelope for three command kinds, discriminated by the `Kind` string. `ActionName` carries the enum **name** (`"StartMowing"`) and `ActionCode` the numeric value — the robot matches on the name (`case "StartMowing":`), which makes captured JSON human-readable in Swagger and in logs. Slightly redundant, but a deliberate readability/debuggability trade-off.

`CommandKinds` uses `const string` rather than an enum so JSON stays readable without a converter.

### 2.4 `RobotEventDto` — [file](MowIT.Shared/Telemetry/RobotEventDto.cs)

```csharp
public sealed record RobotEventDto {
    public long Seq;  public string Type = "";  public int XCm, YCm;
    public bool Success;  public string? Reason;  public DateTime CreatedUtc = DateTime.UtcNow; }

public static class RobotEventTypes {
    public const string Connected="Connected", BaseCaptured="BaseCaptured",
        BoundaryPointCaptured="BoundaryPointCaptured", OutlineCaptured="OutlineCaptured",
        ExitCaptured="ExitCaptured", CaptureEnd="CaptureEnd",
        BoundaryCleared="BoundaryCleared", Error="Error"; }

public sealed record EventListResponse { public RobotEventDto[] Events = []; public long Cursor; }
```

**`Seq` is the whole reason events are reliable.** The server stamps it; the client tracks the highest seen and asks `?since=N`. See §5.3.

### 2.5 `ScheduleDto` / responses — [file](MowIT.Shared/Schedules/ScheduleDto.cs), [file](MowIT.Shared/Schedules/ScheduleListResponse.cs)

```csharp
ScheduleDto { Id, int[] ActiveDays, long StartTimeTicks, int DurationMinutes,
              bool IsActive, string ZoneName, int? ZoneId, DateTime LastExecutedUtc }
ScheduleListResponse   { RobotId, long Version, DateTime UpdatedUtc, ScheduleDto[] Schedules }
ScheduleVersionResponse{ RobotId, long Version, DateTime UpdatedUtc }
ScheduleUploadRequest  { ScheduleDto[] Schedules }
```

`DayOfWeek[]` becomes `int[]`, `TimeSpan` becomes `long Ticks` — both are framework types that do not serialise portably. `Version` is a monotonic counter used for **cheap change detection**: the robot can call `/schedules/version` (a tiny response) and only fetch the full list when the number changed. (The current robot simply compares the version inside the full response every 10 ticks, but the endpoint exists for the efficient path.)

---

## 3. The server — `Program.cs`

[`MowIT.ScheduleServer/Program.cs`](MowIT.ScheduleServer/Program.cs) — 133 lines, top-level statements, **Minimal API** style.

### 3.1 Startup

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RobotTokenOptions>(builder.Configuration.GetSection("RobotTokens"));

var dbPath  = builder.Configuration.GetValue<string>("Database:Path") ?? "schedules.db";
var connStr = new SqliteConnectionStringBuilder {
    DataSource = dbPath,
    Mode       = SqliteOpenMode.ReadWriteCreate,     // create the file if missing
    Cache      = SqliteCacheMode.Shared              // one shared page cache across connections
}.ToString();

builder.Services.AddSingleton<IRobotTokenStore, InMemoryRobotTokenStore>();
builder.Services.AddSingleton<IScheduleStore>(_ => new SqliteScheduleStore(connStr));
builder.Services.AddSingleton<ITelemetryStore, InMemoryTelemetryStore>();
builder.Services.AddSingleton<ICommandQueue,  InMemoryCommandQueue>();
builder.Services.AddSingleton<IRobotEventLog, InMemoryRobotEventLog>();

builder.Services.AddAuthentication(RobotAuth.Scheme)
       .AddScheme<RobotTokenAuthOptions, RobotTokenAuthHandler>(RobotAuth.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseAuthentication();
app.UseAuthorization();
```

**Why `SqliteConnectionStringBuilder` instead of a string literal?** It escapes the path correctly (spaces, backslashes on Windows) and makes the mode/cache options explicit and type-checked.

**Every store is behind an interface** (`IScheduleStore`, `ITelemetryStore`, `ICommandQueue`, `IRobotEventLog`) even though there is one implementation each. That is what makes the endpoints testable with `WebApplicationFactory` and a fake store — see chapter 10.

**Storage strategy — a deliberate split:**

| Data | Store | Why |
|---|---|---|
| Schedules | **SQLite** (`SqliteScheduleStore`) | Must survive a server restart — it is user intent |
| Telemetry | in-memory | Only the *latest* value matters; history would need a time-series DB |
| Commands | in-memory | Valid for seconds; a stale command replayed after a restart would be dangerous |
| Events | in-memory ring (200) | Short-lived notifications, only interesting to a currently-connected app |

Being able to justify "why is telemetry not persisted?" with *"a command replayed after a restart could start a mower with spinning blades unexpectedly"* is a strong answer.

`public partial class Program { }` at the very bottom exists so that a future integration-test project can reference `Program` in `WebApplicationFactory<Program>` — top-level-statement programs generate an internal `Program` class otherwise.

### 3.2 The endpoints

```csharp
app.MapGet("/robots/active", (ITelemetryStore t) =>
        Results.Ok(new ActiveRobotsResponse { Robots = t.GetActive() }))
   .RequireAuthorization();

var robots = app.MapGroup("/robots/{robotId}").RequireAuthorization();
```

`MapGroup` gives a common prefix **and** applies `RequireAuthorization()` once to every child route — no chance of forgetting it on one endpoint.

| Method | Route | Body / query | Returns | Used by |
|---|---|---|---|---|
| GET | `/robots/active` | — | `ActiveRobotsResponse` | app scan |
| GET | `/robots/{id}/schedules` | — | `ScheduleListResponse` | robot |
| GET | `/robots/{id}/schedules/version` | — | `ScheduleVersionResponse` | robot (cheap poll) |
| POST | `/robots/{id}/schedules` | `ScheduleUploadRequest` | `ScheduleListResponse` | app |
| DELETE | `/robots/{id}/schedules/{scheduleId:int}` | — | `204` / `404` | app |
| POST | `/robots/{id}/telemetry` | `TelemetryDto` | `204` | robot |
| GET | `/robots/{id}/telemetry` | — | `TelemetryEnvelope` | app (2 Hz) |
| POST | `/robots/{id}/commands` | `RobotCommandDto` | `202 Accepted` | app |
| GET | `/robots/{id}/commands` | — | `CommandListResponse` (**and drains**) | robot (2 Hz) |
| POST | `/robots/{id}/events` | `RobotEventDto` | the stored event (with `Seq`) | robot |
| GET | `/robots/{id}/events?since=N` | — | `EventListResponse` | app (2 Hz) |
| GET | `/healthz` | — | `{status,utc}` | monitoring — **not** authorised |

Correct status-code usage is worth pointing out: **`202 Accepted`** for enqueueing a command (it has been accepted for later processing, not executed), **`204 No Content`** for a telemetry write and a successful delete, **`404`** for deleting something that is not there, **`200`** with a body for reads.

`{scheduleId:int}` is a **route constraint** — a non-numeric id gets a 404 from the router before any code runs.

`/healthz` is deliberately unauthenticated so a load balancer or `curl` can check liveness.

---

## 4. Authentication

### 4.1 `RobotTokenStore` — [file](MowIT.ScheduleServer/Auth/RobotTokenStore.cs)

```csharp
public sealed class RobotTokenOptions { public Dictionary<string,string> Tokens { get; init; } = new(); }

public sealed class InMemoryRobotTokenStore : IRobotTokenStore {
    private readonly Dictionary<string,string> _tokenToRobot;
    public InMemoryRobotTokenStore(IOptions<RobotTokenOptions> opts)
        => _tokenToRobot = opts.Value.Tokens.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public string? Resolve(string token) {
        foreach (var (storedToken, robotId) in _tokenToRobot)
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedToken),
                                                        Encoding.UTF8.GetBytes(token)))
                return robotId;
        return null;
    }
}
```

Configuration maps **robotId → token**; the store inverts it to **token → robotId** because lookups go that way.

**`CryptographicOperations.FixedTimeEquals` is the detail to highlight.** A normal string comparison returns as soon as it finds a differing character, so the *time* it takes leaks how many leading characters were correct. An attacker can exploit that to guess a secret one character at a time — a **timing attack**. `FixedTimeEquals` always examines the whole buffer, so the duration reveals nothing. Knowing *why* that call is there is a genuine security-awareness point.

**Honest limitation:** the comparison loops over every configured token, so lookup is O(n) and the fixed-time guarantee is per-token, not per-request. With a handful of robots this is fine; a real deployment would store hashed tokens and index them.

### 4.2 `RobotTokenAuthHandler` — [file](MowIT.ScheduleServer/Auth/RobotTokenAuthHandler.cs)

```csharp
protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
    if (!Request.Headers.TryGetValue("Authorization", out var values)) return NoResult();
    var raw = values.ToString();
    if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))  return NoResult();

    var token   = raw["Bearer ".Length..].Trim();
    var robotId = _tokens.Resolve(token);
    if (robotId is null) return Fail("Invalid robot token");

    var identity = new ClaimsIdentity(new[] {
        new Claim(RobotAuth.RobotIdClaim, robotId),
        new Claim(ClaimTypes.NameIdentifier, robotId) }, RobotAuth.Scheme);
    return Success(new AuthenticationTicket(new ClaimsPrincipal(identity), RobotAuth.Scheme));
}
```

A **custom authentication scheme** built by deriving from ASP.NET Core's `AuthenticationHandler<TOptions>`. Note the correct distinction between **`NoResult()`** ("there are no credentials here — let another scheme try, or fall through to a 401 challenge") and **`Fail()`** ("credentials were present and are wrong"). Getting that distinction right is a sign you understand the pipeline rather than copying a snippet.

The resolved robot id becomes a **claim**, so an endpoint could later assert that the caller's token matches the `{robotId}` in the route.

**Say this out loud before the professor does:**
> "The security here is deliberately minimal and I know its limits: a static shared bearer token over plain HTTP, and the endpoints do not yet check that the token's robot id equals the `{robotId}` in the URL — so any valid token can currently read any robot. For a real deployment I would add HTTPS, per-device rotating tokens or mTLS, and an authorisation policy comparing the claim to the route value. The token is stored in configuration (with `UserSecretsId` already set up) rather than hard-coded, and the comparison is timing-safe, so the *structure* for doing it properly is in place."

That answer scores far better than pretending it is production-grade.

---

## 5. The four stores

### 5.1 `SqliteScheduleStore` — [file](MowIT.ScheduleServer/Storage/ScheduleStore.cs)

**Schema (created on construction, `CREATE TABLE IF NOT EXISTS`):**
```sql
CREATE TABLE Robots (
    RobotId    TEXT PRIMARY KEY,
    Version    INTEGER NOT NULL DEFAULT 0,
    UpdatedUtc TEXT    NOT NULL DEFAULT (datetime('now')));

CREATE TABLE Schedules (
    RobotId     TEXT    NOT NULL,
    ScheduleId  INTEGER NOT NULL,
    PayloadJson TEXT    NOT NULL,
    PRIMARY KEY (RobotId, ScheduleId),
    FOREIGN KEY (RobotId) REFERENCES Robots(RobotId) ON DELETE CASCADE);
```

**The schedule body is stored as JSON in one column.** A hybrid relational/document design: the columns you *query on* (`RobotId`, `ScheduleId`) are real columns; the payload is opaque. Adding a field to `ScheduleDto` needs **no migration**. The trade-off — you cannot write `WHERE IsActive = 1` in SQL. For this workload (fetch all schedules for one robot) that is a good trade. Be ready to defend it as a conscious choice, not laziness.

**`ReplaceAsync` — the atomic full replace:**
```csharp
using var c  = (SqliteConnection)Open();
using var tx = c.BeginTransaction();

await c.ExecuteAsync(@"INSERT INTO Robots (RobotId, Version, UpdatedUtc) VALUES (@robotId, 1, datetime('now'))
                       ON CONFLICT (RobotId) DO UPDATE
                         SET Version = Version + 1, UpdatedUtc = datetime('now');", new { robotId }, tx);
await c.ExecuteAsync("DELETE FROM Schedules WHERE RobotId = @robotId", new { robotId }, tx);
foreach (var s in schedules)
    await c.ExecuteAsync("INSERT INTO Schedules (RobotId, ScheduleId, PayloadJson) VALUES (@robotId,@scheduleId,@payload);",
                         new { robotId, scheduleId = s.Id, payload = JsonSerializer.Serialize(s) }, tx);
tx.Commit();
return await GetAsync(robotId, ct);
```

Three things to name:
* **`ON CONFLICT … DO UPDATE`** — SQLite's UPSERT. One statement that creates the robot row on first use and bumps the version thereafter. No read-then-write race.
* **A transaction wraps delete + all inserts** — a robot polling mid-update can never see an empty schedule list.
* **Full replace, not merge.** Combined with the app pushing the whole list, this is last-writer-wins. Simple and conflict-free; the cost is that two phones editing simultaneously would overwrite each other. Correct trade-off to state.

**Dapper parameterisation** (`@robotId`) is used everywhere — **no string concatenation, therefore no SQL-injection surface**. Point this out; examiners look for it.

`ParseUtc` defensively converts SQLite's text datetimes back to `DateTimeKind.Utc`, returning `DateTime.MinValue` if parsing fails.

*Minor note:* `GetAsync`/`GetVersionAsync` accept a `CancellationToken` but do not forward it to Dapper. An easy, honest improvement.

### 5.2 `InMemoryTelemetryStore` — [file](MowIT.ScheduleServer/Storage/TelemetryStore.cs)

```csharp
private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(5);
private sealed record Entry(TelemetryDto Telemetry, DateTime LastSeenUtc);
private readonly ConcurrentDictionary<string, Entry> _latest = new(StringComparer.Ordinal);

public void Update(string robotId, TelemetryDto t) => _latest[robotId] = new Entry(t, DateTime.UtcNow);

public TelemetryEnvelope Get(string robotId) =>
    _latest.TryGetValue(robotId, out var e)
      ? new TelemetryEnvelope { RobotId = robotId, IsOnline = DateTime.UtcNow - e.LastSeenUtc < OnlineWindow,
                                LastSeenUtc = e.LastSeenUtc, Latest = e.Telemetry }
      : new TelemetryEnvelope { RobotId = robotId, IsOnline = false };
```

**Liveness by heartbeat, not by a connection.** The robot posts telemetry at 2 Hz; if nothing has arrived for 5 seconds it is considered offline. There is no socket to watch, so freshness *is* the liveness signal. 5 s = 10 missed heartbeats — tolerant of a hiccup, quick enough to notice a crash.

`ConcurrentDictionary` because many robots may POST simultaneously while apps GET. No `lock` needed for a whole-value replace.

### 5.3 `InMemoryRobotEventLog` — [file](MowIT.ScheduleServer/Storage/RobotEventLog.cs)

```csharp
private const int MaxEntries = 200;
private sealed class Channel { public long Seq; public readonly LinkedList<RobotEventDto> Events = new(); }
private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);

public RobotEventDto Append(string robotId, RobotEventDto evt) {
    var ch = _channels.GetOrAdd(robotId, _ => new Channel());
    lock (ch) {
        var stored = evt with { Seq = ++ch.Seq };            // ← the server assigns the sequence number
        ch.Events.AddLast(stored);
        while (ch.Events.Count > MaxEntries) ch.Events.RemoveFirst();
        return stored;
    }
}

public EventListResponse GetSince(string robotId, long cursor) {
    if (!_channels.TryGetValue(robotId, out var ch)) return new EventListResponse { Cursor = cursor };
    lock (ch) {
        var newer = ch.Events.Where(e => e.Seq > cursor).ToArray();
        var next  = newer.Length > 0 ? newer[^1].Seq : Math.Max(cursor, ch.Seq);
        return new EventListResponse { Events = newer, Cursor = next };
    }
}
```

**Design points:**
* **The server, not the robot, assigns `Seq`.** A single authority means the sequence is always consistent even if the robot restarts.
* **`LinkedList` + trim from the front** = an O(1) bounded ring buffer. A `List` would be O(n) per removal.
* **`lock (ch)`** — per-robot locking, so two robots never block each other. `ConcurrentDictionary` handles the outer map; the inner lock protects the read-modify-write of `Seq` + list.
* **Cursor semantics on an empty result:** returning `Math.Max(cursor, ch.Seq)` moves the client forward even when it has nothing new, so a client that connects late does not replay 200 old events.

### 5.4 `InMemoryCommandQueue` — [file](MowIT.ScheduleServer/Storage/CommandQueue.cs)

```csharp
public void Enqueue(string robotId, RobotCommandDto command) {
    var list = _pending.GetOrAdd(robotId, _ => new List<RobotCommandDto>());
    lock (list) {
        if (command.Kind == CommandKinds.Motor) list.RemoveAll(c => c.Kind == CommandKinds.Motor);  // ← coalescing
        list.Add(command);
    }
}

public RobotCommandDto[] Drain(string robotId) {
    if (!_pending.TryGetValue(robotId, out var list)) return [];
    lock (list) { if (list.Count == 0) return []; var snap = list.ToArray(); list.Clear(); return snap; }
}
```

**Motor-command coalescing is the smartest thing in the server.** The app sends motor commands at 10 Hz; the robot polls at 2 Hz. Without coalescing the robot would receive **five stale velocities** each poll and would try to execute them in order — jerky, laggy, and increasingly behind reality. Because only the **newest** motor command is meaningful, older ones are discarded on arrival. Actions and boundary uploads are *not* coalesced — every one of those must be delivered.

**`Drain` is destructive read** — get-and-clear in one atomic step, so a command is delivered exactly once. This is at-most-once delivery: if the HTTP response is lost the command is gone. For velocity commands that is correct (a new one is 100 ms away). For a `StartMowing` it is a theoretical weakness — a real system would use an ack/id-based protocol. Name this if asked about reliability; the `RobotCommandDto.Id` field is already there to support it.

---

## 6. `MowIT.RobotSimulator` — the virtual robot

### 6.1 `Program.cs` — the client loop

[file](MowIT.RobotSimulator/Program.cs)

```csharp
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)                       // later sources win → CLI overrides everything
    .Build();

var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / tickHz));   // default 2 Hz
while (!cts.IsCancellationRequested && await SafeWaitAsync(timer, cts.Token))
{
    robot.Tick(dt);                                                   // ① advance the physics

    await http.PostAsJsonAsync(telemetryUrl, robot.ToTelemetry(), ct); // ② publish state

    var pending = await http.GetFromJsonAsync<CommandListResponse>(commandsUrl, ct);   // ③ fetch commands
    foreach (var command in pending?.Commands ?? []) {
        foreach (var evt in robot.ApplyCommand(command))
            await http.PostAsJsonAsync(eventsUrl, evt, ct);            // ④ publish resulting events
    }

    if (tickCount++ % 10 == 0) {                                       // ⑤ every 5 s: schedule sync
        var schedules = await http.GetFromJsonAsync<ScheduleListResponse>(schedulesUrl, ct);
        if (schedules is not null && schedules.Version != scheduleVersion) {
            scheduleVersion = schedules.Version;
            Log($"schedules synced from cloud: v{schedules.Version}, {schedules.Schedules.Length} schedule(s)");
            foreach (var s in schedules.Schedules) Log($"   - \"{s.ZoneName}\" …");
        }
    }
}
```

* **`PeriodicTimer`** (.NET 6+) is the correct modern timer for an async loop — it does not drift and cooperates with cancellation, unlike `Task.Delay` in a loop or `System.Timers.Timer` with an `async void` handler.
* **Ctrl-C is handled gracefully:** `Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };`
* **The catch-all prints a helpful hint** rather than a stack trace: `Log($"! {ex.Message} (is the server running?)")` — and the loop keeps going, so starting the robot before the server is harmless.
* **Version-based schedule sync (⑤)** only logs when the version actually changed, which keeps the console readable during a demo while proving the sync works.

### 6.2 `VirtualRobot.cs` — the model

[file](MowIT.RobotSimulator/VirtualRobot.cs) — a **pure, dependency-free** simulation class: no HTTP, no logging, no I/O. `Tick(dt)`, `ToTelemetry()`, `ApplyCommand(cmd)` and nothing else. That purity means it could be unit-tested directly with zero infrastructure (see chapter 10).

It mirrors [`SimulatedRobotService`](MowIT/Infrastructure/Simulator/SimulatedRobotService.cs) closely — same constants (`0.8 m/s²`, `3.0 rad/s²`, `800→12 mm` accuracy ramp at `100 mm/s`, `0.6 s` manual watchdog, `0.3 m/s` mow speed, `0.30 m` waypoint radius, start position 43.8563/18.4131), the same capture state machine and the same failure codes.

Two deliberate differences from the in-app simulator:

1. **State is `int`, not `RobotState`** — `const int StateIdle=0, StateMowing=1, StateRecordingBoundary=7;`. The simulator project does not reference `MowIT`, so it cannot use the enum; it uses the same numeric values, which is exactly the contract the wire format defines.
2. **Simpler local projection.** Instead of the ECEF path it uses the flat approximation:
   ```csharp
   posX = (lon − baseLon) · 111319.444 · cos(baseLat);
   posY = (lat − baseLat) · 111319.444;
   ```
   Accurate to a few centimetres over a garden and adequate for a stand-in robot. **The app still uses the rigorous `LocalProjection`** — the precision lives where it matters.

**Commands become events:**
```csharp
public IReadOnlyList<RobotEventDto> ApplyCommand(RobotCommandDto command) {
    if (command.Kind == CommandKinds.Motor)    { ApplyMotor(command.LinearVel, command.AngularVel); return []; }
    if (command.Kind == CommandKinds.Boundary) { LoadRoute(command.Boundary);                       return []; }
    return ApplyAction(command.ActionName ?? "");
}
```
Motor and boundary commands produce no events (nothing to report); actions return a list of `RobotEventDto`s that `Program.cs` posts to the server, which the app then receives through its cursor poll. **That is the full round trip.**

`EndRecording()` reproduces the same validation ladder as the firmware:
```csharp
if (!wasCapturing)              return Fail("NOT_RECORDING");
if (_closedPolygons.Count == 0) return Fail("NO_POLYGON");
if (_exitPoint is null)         return Fail("NO_EXIT");
_pathSaved = true;
return new RobotEventDto { Type = RobotEventTypes.CaptureEnd, Success = true };
```

---

## 7. End-to-end: one joystick nudge over WiFi

| # | Where | What happens |
|---|---|---|
| 1 | App | Finger moves the joystick → `JoystickView.MoveThumb` → `JoystickMoved` event |
| 2 | App | `ControlViewModel.OnJoystickMoved` maps to `lin=0.35, ang=-0.20` and pushes onto `_joystickSubject` |
| 3 | App | Rx `Sample(100 ms)` + keep-alive `Switch` → `IRobotControl.SendMotorCommandAsync(0.35, -0.20)` |
| 4 | App | `CommandPipeline` → `LoggingCommandHandler` → `DispatchHandler` → `ConnectionGuardProxy` → `RetryDecorator` → `RobotTransportRouter` → `WifiRobotService` |
| 5 | Wire | `POST /robots/demo-robot-01/commands` with `{"Kind":"Motor","LinearVel":0.35,"AngularVel":-0.2,…}` + `Authorization: Bearer dev-token-…` |
| 6 | Server | `RobotTokenAuthHandler` validates the token in fixed time → claim `robot_id=demo-robot-01` |
| 7 | Server | `InMemoryCommandQueue.Enqueue` removes any older Motor command, appends this one → `202 Accepted` |
| 8 | Robot | Next 500 ms tick: `GET /robots/demo-robot-01/commands` → `InMemoryCommandQueue.Drain` returns and clears |
| 9 | Robot | `VirtualRobot.ApplyCommand` → `ApplyMotor` sets `_targetLinear/_targetAngular`, resets the watchdog |
| 10 | Robot | `Tick(0.5)` ramps actual velocity toward target (0.8 m/s² limit) and integrates lat/lon |
| 11 | Robot | `POST /robots/demo-robot-01/telemetry` with the new `TelemetryDto` |
| 12 | Server | `InMemoryTelemetryStore.Update` stores it with `LastSeenUtc = now` |
| 13 | App | Poll loop: `GET /robots/demo-robot-01/telemetry` → `TelemetryEnvelope{IsOnline:true, Latest:{…}}` |
| 14 | App | `PublishTelemetry` → `_sensorSubject.OnNext(...)` and `_statusSubject.OnNext(...)` |
| 15 | App | `ControlViewModel` (10 Hz), `DashboardViewModel` (2 Hz), `MapViewModel` (1 Hz), `GeofenceMonitor` (2 Hz), `GpsTraceService` (every sample) all react |
| 16 | App | Skia map smooths the icon toward the new position at 30 fps; the trail grows if it moved > 0.5 m |

**Latency budget:** up to 100 ms Rx sampling + one network hop + up to 500 ms until the robot polls + up to 500 ms until the app polls ≈ **0.6–1.2 s** worst case. That is why WiFi is presented as a *supervisory* channel and Bluetooth (~90 ms measured) as the *driving* channel — a good, honest engineering statement to make in the defence.
