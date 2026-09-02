# 05 — THE APPLICATION LAYER

`MowIT/Application/` — ring 2. **The stories of the system.** It orchestrates Domain objects and asks Infrastructure for services through interfaces. It never knows *which* Bluetooth library or *which* database is behind them.

---

## 1. Use cases

A *use case* class = one user intention, one public `ExecuteAsync`, all the business rules for that intention in one place. All four are registered `Transient` (cheap, stateless).

### 1.1 `ConnectToMowerUseCase` — [file](MowIT/Application/UseCases/ConnectToMowerUseCase.cs)

```csharp
public async Task<bool> ExecuteAsync(MowerDevice device, CancellationToken ct = default)
    => await _connection.ConnectAsync(device, ct);
```

A one-line pass-through today. **Be honest about it:** it exists as the seam where connection policy (retry, timeout, "remember last device", telemetry) would go. It is currently not injected anywhere — `ScanViewModel` calls `IRobotConnection` directly. That is a real (small) inconsistency in the codebase and a fine thing to mention as "the next refactor".

### 1.2 `SaveScheduleUseCase` — [file](MowIT/Application/UseCases/SaveScheduleUseCase.cs)

```csharp
if (schedule.ActiveDays.Length == 0)
    throw new InvalidOperationException("At least one day must be selected");
if (schedule.DurationMinutes <= 0)
    throw new InvalidOperationException("Duration must be greater than zero");
await _repo.SaveAsync(schedule);
```

**This is where the *business rules* live**, not in the UI. `ScheduleViewModel` also checks "at least one day" for a fast, friendly error message, but the use case is the authority — so a schedule created by any other code path (a future API, a test) is validated too. That duplication is intentional defence-in-depth, not an oversight.

### 1.3 `SendBoundaryUseCase` — [file](MowIT/Application/UseCases/SendBoundaryUseCase.cs)

```csharp
var zone = await _repo.GetByIdAsync(zoneId) ?? throw new InvalidOperationException($"Zone {zoneId} not found");
if (zone.Points.Count < 3) throw new InvalidOperationException("Boundary must have at least 3 points");
await _boundary.SendBoundaryAsync(zone, progress);
```

Load → validate → send, with an `IProgress<int>` passed through so the UI can show a progress bar without the use case knowing what a progress bar is.

### 1.4 `SendZoneToRobotUseCase` — [file](MowIT/Application/UseCases/SendZoneToRobotUseCase.cs)

The most interesting one — it makes a **decision**:

```csharp
var zone  = await _repo.GetByIdAsync(zoneId) ?? throw …;
if (zone.Points.Count < 3) throw …;

var route = _planner.Plan(zone);              // try to compute a mowing route

if (route.Count > 0) await _robotBoundary.SendRouteAsync(route, progress);   // prefer the route
else                 await _robotBoundary.SendBoundaryAsync(zone, progress); // fall back to the outline

if (startMowingAfter) await _control.SendActionAsync(RobotAction.StartMowing);
```

**Plain:** *"If I can plan a proper mowing pattern for this garden, send the pattern. If the garden is too small or degenerate for the planner to produce anything, at least send the outline and let the robot's own firmware figure it out. Then, optionally, start."*

It also exposes `StartMowingOnlyAsync()` for schedules that have **no** linked zone (`ZoneId == null`) — meaning "use whatever boundary is already stored on the robot".

This class is used by both `ScheduleViewModel.MowNowAsync` (user presses ▶) and `MowingSchedulerService.TickAsync` (a timer fires). **One rule, two triggers** — that is exactly the value of a use-case class.

---

## 2. Application services

### 2.1 `MowingRoutePlanner` — [file](MowIT/Application/Services/MowingRoutePlanner.cs)

```csharp
public MowingRoutePlanner(IEnumerable<IMowingStrategy> strategies) => _strategies = strategies.ToList();
public IReadOnlyList<IMowingStrategy> Strategies => _strategies;
public int SelectedIndex { get; set; }
public string StrategyName(int i) => _strategies.Count > 0 ? _strategies[i % _strategies.Count].Name : "-";
public List<GpsPoint> Plan(BoundaryZone zone) => Plan(zone, SelectedIndex);
public List<GpsPoint> Plan(BoundaryZone zone, int strategyIndex) {
    if (_strategies.Count == 0 || zone.Points.Count < 3) return new();
    return _strategies[strategyIndex % _strategies.Count]
             .GenerateRoute(zone, AdaptiveSpacing(zone.Points));
}
```

Three responsibilities:
* **Holds the strategy list** injected by DI (see [`03-ARCHITECTURE.md §4.3`](03-ARCHITECTURE.md) — two `AddTransient<IMowingStrategy,…>` registrations become one `IEnumerable`).
* **Guards** with `% Count` so an out-of-range index from the UI wraps instead of throwing.
* **Chooses the spacing** via `AdaptiveSpacing` (the maths is explained in [`04-DOMAIN-LAYER.md §4.3`](04-DOMAIN-LAYER.md)).

### 2.2 `GeofenceMonitor` — [file](MowIT/Application/Services/GeofenceMonitor.cs) — **the safety system**

**Plain:** a background guard that watches the robot's GPS and slams the brakes if it leaves the saved garden.

**Lifecycle:**

```csharp
// constructor — subscribe to the LINK, not yet to the sensors
_connectionSub = _connection.ConnectionState.Subscribe(state => _ = OnConnectionStateAsync(state));

// on Connected:
await ReloadAsync();                       // load zones from SQLite, pick the newest VALID one
_sensorSub = _sensors.SensorStream
                 .Sample(TimeSpan.FromMilliseconds(500))
                 .Subscribe(CheckGeofence);
_evt.State("GEOFENCE", zone is null ? "idle - no boundary to watch"
                                    : $"armed on \"{name}\" ({n} pts, {area:F1} m2)");

// on anything else: dispose the sensor subscription, reset the latch
```

`ReloadAsync` picks the zone:
```csharp
_zone = zones.Where(z => z.IsValid).OrderByDescending(z => z.CreatedAt).FirstOrDefault();
```
i.e. **the most recently created zone with ≥ 3 points**.

`ReloadAsync` is public precisely so the fence can be re-armed whenever the set of saved zones changes, without waiting for a reconnect. It is called from five places:

| Caller | Why |
|---|---|
| `GeofenceMonitor.OnConnectionStateAsync` | arm on connect |
| `ControlViewModel.PersistWalkedZoneAsync` | a walked boundary was just saved |
| `ControlViewModel.SavePlanZoneAsync` | a zone captured in Plan mode was just saved |
| `MapViewModel.SaveZoneLocallyAsync` / `SendBoundaryToRobotAsync` | a zone drawn on the map was just saved |
| `MapViewModel.SendRouteToRobotAsync` | **about to mow** — guard the polygon this route was planned for |
| `ControlViewModel.MowPlanNowAsync` | **about to mow** — same rule for Plan mode |
| `MapViewModel.DeleteZoneAsync` | the armed zone may have just been deleted — re-pick, or go idle |

Without those calls the fence would keep watching whatever it loaded at connection time, so a zone drawn on the map would protect nothing until the next reconnect, and deleting the armed zone would leave the fence guarding a polygon that no longer exists.

The two **"about to mow"** entries close the most damaging version of that gap: the mow commands used to send a route without telling the fence which polygon it belonged to, so if any *other* zone had been saved more recently the robot would teleport to the first waypoint of the new route, land far outside the guarded polygon, and be stopped immediately. Both paths now persist the zone they are mowing and re-arm on it first.

**The check itself:**

```csharp
private void CheckGeofence(SensorSnapshot s)
{
    if (_zone is null) return;
    if (s.Gps.Latitude == 0 && s.Gps.Longitude == 0) return;   // ignore "no fix yet"

    bool   inside = _zone.Contains(s.Gps);
    double past   = inside ? 0.0 : _zone.DistanceToBoundaryMeters(s.Gps);
    double tolerance = Math.Max(MinBreachMarginMeters, s.GpsAccuracyMm / 1000.0);   // ← hysteresis

    if (inside || past < tolerance) {                           // inside, or merely on the line
        if (_outside) _evt.Info(Source, "robot back inside zone …");
        _outside = false;  return;
    }
    if (_outside) return;                                       // already reported — don't spam

    _outside = true;                                            // ← edge-triggered latch
    _evt.Warn(Source, $"robot left zone … {past:F2} m past the boundary, GPS ±{acc:F2} m - stopping");
    WeakReferenceMessenger.Default.Send(new GeofenceBreachMessage(s.Gps, _zone.Name));
    _ = _control.SendActionAsync(RobotAction.Stop);
}
```

**Five design points worth saying out loud:**
1. **Edge-triggered, not level-triggered.** The `_outside` boolean means the Stop command and the warning fire exactly **once** per exit, not twice a second forever. Re-entering resets the latch and logs the recovery.
2. **It logs the evidence.** `{past:F2} m past the boundary` together with `GPS ±{accuracy} m` lets you decide afterwards whether a breach was a real excursion or a GPS wobble. That single log line is worth a lot in a defence — it is the difference between a demo and an engineered system.
3. **Two reactions, decoupled.** It sends a *message* (so the Dashboard can toast the user) **and** sends a *command* (so the robot actually stops). Neither knows about the other.
4. **`_ = _control.SendActionAsync(...)`** — the discard tells the compiler "I know this is fire-and-forget". `CheckGeofence` is a synchronous Rx callback; making it `async void` would be worse. Being able to explain that `_ =` is deliberate is a nice detail.
5. **A tolerance band, not a strict test.** `MinBreachMarginMeters = 0.5`, widened to the current GPS accuracy when that is worse. This is not sloppiness — it is required for correctness. `BoustrophedonStrategy` builds each row by intersecting a scan-line with the polygon edges, so **every row starts and ends exactly *on* the boundary**, and `SimulatedRobotService.HandleStartMowing` teleports the robot exactly onto waypoint 0. A point sitting precisely on an edge is classified by `Contains` on a strict `<` comparison, so floating-point noise at the 1e-9 level decides inside vs. outside — meaning a zero-tolerance fence stops the mow at the first waypoint, roughly at random. Real geofences always carry hysteresis for exactly this reason; a fence that trips on its own planned route is a false positive, not a safety feature.

**Zero-coordinate guard:** `if (Lat == 0 && Lon == 0) return;` — (0,0) is "Null Island" in the Gulf of Guinea and is what every uninitialised GPS struct reports. Without this guard the fence would fire the moment you connect.

### 2.3 `GpsTraceService` — [file](MowIT/Application/Services/GpsTraceService.cs) — **the flight recorder**

Subscribes to `ConnectionState`; on **Connected** it opens `logs/gpstrace_yyyyMMdd_HHmmss.csv` and subscribes to the **unthrottled** sensor stream; on disconnect it closes the file and logs how many samples were captured.

CSV header:
```
utc_iso,elapsed_ms,latitude,longitude,accuracy_m,fix,pos_changed,manual,moving
```

Details that matter:
* **`CultureInfo.InvariantCulture` everywhere.** On a machine with a Croatian/Bosnian locale, `43,8563` would be written with a comma — inside a comma-separated file. Forcing invariant culture is the difference between a usable dataset and a corrupted one.
* **`pos_changed`** is 1 only when the coordinates actually differ from the previous written row. The link polls at a fixed rate but the GPS may not have updated, so this column lets you distinguish "the robot did not move" from "the GPS did not refresh".
* **`lock (_fileLock)`** around every write and around `Stop()` — the sensor stream fires on a background thread while the connection stream may call `Stop()` on another.
* **`AutoFlush = true`** — if the app crashes or the phone dies mid-run, everything already written survives. You trade throughput for durability, correct for a diagnostic log.
* **Self-disabling on error:** if a write throws, the file is disposed, `_csv` is set to `null`, and the service silently stops recording instead of throwing on every subsequent GPS sample.

*Why this matters for your defence:* the repository history contains commits adding "session log and GPS trace files for measurements". Those CSVs are your **experimental data** — you can plot the recorded path, compute accuracy statistics, and show a graph in the presentation.

### 2.4 `LastMowSession` — [file](MowIT/Application/Services/LastMowSession.cs)

The smallest service in the project, and a good example of scope discipline:

```csharp
private const string Key = "mowit.last_mow_at_ticks";
public DateTime? LastMowAtLocal { get {
    var ticks = Preferences.Default.Get<long>(Key, 0L);
    return ticks == 0L ? null : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(); } }
public void MarkMowedNow() { Preferences.Default.Set(Key, DateTime.UtcNow.Ticks); Changed?.Invoke(this, EventArgs.Empty); }
```

**Stored as UTC ticks, displayed as local time.** That is the correct pattern for timestamps — if the user flies to another time zone, "3 hours ago" stays true. The `Changed` event lets `DashboardViewModel` refresh its "LAST MOWED" tile immediately instead of waiting for a page reload.

---

## 3. `RobotStateMachine` — [file](MowIT/Application/StateMachine/RobotStateMachine.cs)

**Plain:** a rule table saying which buttons make sense in which robot state. You should not be able to press "Resume" when the mower is idle, or "Pause" when it is already docked.

```csharp
private RobotState _current = RobotState.Idle;
public void Start(IRobotSensors sensors) => _sub = sensors.StatusStream.Subscribe(s => _current = s.State);
```

It does **not** guess the state — it *mirrors* whatever the robot reports. The robot is the single source of truth; the app only caches it.

**Two rule sets:**

```csharp
AlwaysAllowed = { BladeOn, BladeOff, BoundaryCapturePoint, BoundaryClear, Stop,
                  CaptureBase, CaptureOutline, CaptureExit, ManualModeOn, ManualModeOff };

ValidTransitions = {
  (Idle,              StartMowing),  (Idle,     StartRoute), (Idle, BoundaryRecordStart),
  (Mowing,            Pause),        (Mowing,   ReturnToBase),
  (Paused,            Resume),       (Paused,   StartMowing), (Paused, ReturnToBase),
  (RecordingBoundary, BoundaryRecordEnd), (RecordingBoundary, BoundaryCapturePoint),
  (Returning,         Stop),         (Docking,  Stop),
  (Charging,          StartMowing),  (Charging, StartRoute) };

public bool CanExecute(RobotAction a) => AlwaysAllowed.Contains(a) || ValidTransitions.Contains((_current, a));
```

**Why `HashSet<(RobotState, RobotAction)>`?** A `Contains` on a hash set of value tuples is O(1) and the whole table reads as data rather than as a `switch` with 15 cases. Using C# tuples as a composite key is idiomatic and allocation-free (they are structs).

**Why is `Stop` in `AlwaysAllowed`?** Safety. An emergency stop must never be blocked by a state guard — including when the state is `Error` or unknown.

> **Honest note:** this class is fully implemented and is instantiated and `Start()`-ed by every factory, but the handler that *uses* it (`StateGuardHandler`) is **not currently inserted into the command pipeline** — see §4.3. So today the state machine is live but advisory. Know this before you are asked.

---

## 4. The command pipeline — Chain of Responsibility

`Application/Pipeline/` implements the classic GoF **Chain of Responsibility**: a command travels through a chain of handlers, and any handler may stop it.

### 4.1 `RobotCommandContext` — the travelling object

```csharp
public RobotAction? Action; public byte Param;          // action command…
public float LinearVel, AngularVel;                     // …or motor command
public bool IsMotorCommand => Action is null;           // discriminated by null-ness
public bool IsAborted { get; private set; }
public string? AbortReason { get; private set; }
public void Abort(string reason) { IsAborted = true; AbortReason = reason; }
```

One context type carries both kinds of command; `Action is null` distinguishes them. `IsAborted`/`AbortReason` have **private setters** and can only be changed through `Abort()`, so a handler cannot accidentally un-abort a command.

### 4.2 `CommandHandler` — the abstract link

```csharp
public abstract class CommandHandler {
    private CommandHandler? _next;
    public CommandHandler SetNext(CommandHandler next) { _next = next; return next; }  // returns NEXT → fluent chaining
    public async Task HandleAsync(RobotCommandContext ctx) {
        if (ctx.IsAborted) return;
        await ProcessAsync(ctx);
        if (!ctx.IsAborted && _next is not null) await _next.HandleAsync(ctx);
    }
    protected abstract Task ProcessAsync(RobotCommandContext ctx);
}
```

`SetNext` returns the **next** handler (not `this`) so you can write `a.SetNext(b).SetNext(c)` and build a chain in one expression. The abort check happens both before and after `ProcessAsync`, so a handler that aborts stops the chain immediately.

### 4.3 The four handlers

| Handler | What it does | Wired in? |
|---|---|---|
| [`LoggingCommandHandler`](MowIT/Application/Pipeline/Handlers/LoggingCommandHandler.cs) | `LogDebug` for motor commands, `LogInformation` for actions | ✅ yes |
| [`DispatchHandler`](MowIT/Application/Pipeline/Handlers/DispatchHandler.cs) | The terminal link: actually calls the inner `IRobotControl` | ✅ yes |
| [`BatteryCheckHandler`](MowIT/Application/Pipeline/Handlers/BatteryCheckHandler.cs) | Aborts non-motor commands when `LastStatus.BatteryPct` is between 1 and 4 | ❌ **not wired** |
| [`StateGuardHandler`](MowIT/Application/Pipeline/Handlers/StateGuardHandler.cs) | Aborts if `!stateMachine.CanExecute(action)` | ❌ **not wired** |

### 4.4 `CommandPipeline` — the head of the chain

```csharp
public class CommandPipeline : IRobotControl
{
    public CommandPipeline(IRobotControl inner, IRobotSensors sensors,
                           RobotStateMachine stateMachine, ILogger<CommandPipeline> logger)
    {
        var logging  = new LoggingCommandHandler(logger);
        var dispatch = new DispatchHandler(inner);
        logging.SetNext(dispatch);
        _head = logging;
    }

    public async Task SendActionAsync(RobotAction action, byte param = 0) {
        var ctx = new RobotCommandContext { Action = action, Param = param };
        await _head.HandleAsync(ctx);
        if (ctx.IsAborted) _logger.LogWarning("Action {Action} aborted: {Reason}", action, ctx.AbortReason);
    }
    public async Task SendMotorCommandAsync(float lin, float ang) { …same with LinearVel/AngularVel… }
}
```

Note `CommandPipeline` **is itself an `IRobotControl`** — that is what lets it be substituted for the real transport everywhere without any caller noticing.

> **The honest bit, again.** The constructor accepts `sensors` and `stateMachine` but currently does not use them, because only `logging → dispatch` are linked. Turning the guards on is two lines:
> ```csharp
> var battery = new BatteryCheckHandler(sensors);
> var guard   = new StateGuardHandler(stateMachine);
> logging.SetNext(guard).SetNext(battery).SetNext(dispatch);
> ```
> Have that answer ready — "the chain is built to be extended, here is the exact diff" is a much better answer than being surprised by the question. (It was likely left off during hardware bring-up so that a mis-reported firmware state could not lock the user out of the controls.)

### 4.5 Pipeline vs. decorators — two different mechanisms, both present

Do not confuse them; a professor may probe this.

```
IRobotControl (as seen by ViewModels)
   └─ CommandPipeline            ← Chain of Responsibility (can ABORT a command)
        └─ ConnectionGuardProxy  ← Proxy    (drops commands when not connected)
             └─ RetryDecorator   ← Decorator(retries failed ACTIONS 3×, 100 ms apart)
                  └─ SimulatedRobotService / SppService / BleService / Router
```

* **Chain of Responsibility** = a *sequence of independent inspectors*, each of which can veto.
* **Decorator/Proxy** = *nesting wrappers* that each add one behaviour around the same interface.

Both are built in the factories, e.g. [`SimulatorServiceFactory.cs:36`](MowIT/Infrastructure/Factories/SimulatorServiceFactory.cs#L36):
```csharp
IRobotControl inner = new ConnectionGuardProxy(new RetryDecorator(sim), connection);
return new CommandPipeline(inner, sensors, stateMachine, logger);
```

---

## 5. `EventLogService` — [file](MowIT/Application/Logging/EventLogService.cs)

**Plain:** the app's own black box. Every command sent, every reply received and every state change is recorded with a millisecond timestamp, a level, a source tag and a message — simultaneously to (a) an in-memory list the UI can display, (b) a file on disk, and (c) the standard `ILogger` (so it also appears in the debugger).

```csharp
public enum EventLogLevel { Tx, Rx, Info, State, Warn, Error }

public sealed record EventLogEntry(DateTime Timestamp, EventLogLevel Level, string Source, string Message) {
    public string TimeText  => Timestamp.ToString("HH:mm:ss.fff");
    public string LevelTag  => Level switch { Tx=>"TX", Rx=>"RX", Info=>"i", State=>"=", Warn=>"!", Error=>"x", _=>"?" };
    public Color  LevelColor => …;   // green TX, blue RX, grey info, purple state, orange warn, red error
}
```

Six one-line entry points keep call sites readable:
```csharp
_evt.Tx("BT", "MOWER/START");      _evt.Rx("BT", "MOWER/START/OK  (+143 ms)");
_evt.Info("SCAN", "scan started"); _evt.State("GEOFENCE", "armed on \"Walked 14:32\" (5 pts, 412.7 m2)");
_evt.Warn(…);                      _evt.Error(…);
```

**Source tags used across the codebase:** `BT`, `SIM`, `WIFI`, `ROUTE`, `SYNC`, `SCAN`, `DASH`, `VM`, `GEOFENCE`, `GPSTRACE`. Grep-friendly by design.

**Implementation details worth defending:**
* **Bounded memory:** `MaxEntries = 2000`; the oldest entry is removed once the list exceeds it. A device that runs for hours cannot OOM.
* **Newest first:** `Entries.Insert(0, entry)` so the UI list reads top-down chronologically-reversed without sorting.
* **Thread-safe file writes** behind `lock (_fileLock)`, `AutoFlush = true`, and if the file ever fails the service degrades to memory-only rather than throwing.
* **UI-thread marshalling** with a fast path: `if (MainThread.IsMainThread) AppendOnUi(entry); else MainThread.BeginInvokeOnMainThread(...)`.
* **Session file** `logs/session_yyyyMMdd_HHmmss.log`, tab-separated with a `#` comment header — trivially loadable into Excel/pandas.

---

## 6. Messages — [file](MowIT/Application/Messages/BleMessages.cs)

Thirteen tiny `record` types used with `WeakReferenceMessenger`:

| Message | Sent by | Handled by |
|---|---|---|
| `RobotConnectedMessage(DeviceName)` | every transport on connect | `DashboardViewModel` → toast |
| `RobotDisconnectedMessage(Reason)` | every transport on disconnect / dropped read loop | `DashboardViewModel` |
| `BaseCapturedMessage()` | SPP `GPS/CAPTURE/BASE/DONE`, simulator, WiFi event | `ControlViewModel` (`HasDatum=true`, remember `_baseGps`), `DashboardViewModel` |
| `BoundaryPointCapturedMessage(XCm, YCm)` | simulator / WiFi (local-frame firmware) | `ControlViewModel` → add to `ActivePolygon` |
| `BoundaryGpsPointCapturedMessage(Point)` | SPP (`MOWER/CAPTURE/POINT/OK/lat,lon`) | `ControlViewModel` → add to `ActiveGpsPolygon` **and** project into `ActivePolygon` |
| `OutlineCapturedMessage()` | all transports | `ControlViewModel` → close current polygon, `PolygonCount++` |
| `ExitCapturedMessage(XCm, YCm)` | simulator / WiFi | (declared; no UI handler yet) |
| `CaptureEndMessage(Success, FailReason?)` | all transports | `ControlViewModel` (persist walked zone + re-arm geofence) and `DashboardViewModel` (`HasPath`) |
| `BoundaryClearedMessage()` | firmware clears its stored path on `CAPTURE/START` | both VMs set `HasPath=false` |
| `RobotErrorMessage(Code)` | everywhere | `DashboardViewModel` (friendly toast), `ControlViewModel` (abort recording), `ScanViewModel` (only `WIFI/…` codes) |
| `GeofenceBreachMessage(Position, ZoneName)` | `GeofenceMonitor` | `DashboardViewModel` → warning toast |
| `LowBatteryWarningMessage(Pct)` | `DashboardViewModel` when battery crosses below 20 | *no subscriber yet* |
| `UserDisconnectRequestedMessage()` | `DashboardViewModel.DisconnectAsync`, just before dropping the link | `AppShell` — marks the next disconnect as deliberate so the "connection was lost" alert is skipped |

**Why two different "point captured" messages?** Because the two firmware families report differently: the real GreenTitan over SPP replies with **latitude/longitude**, while the simulator and the cloud robot report **centimetres in the local frame**. `ControlViewModel` handles both and unifies them, which is precisely the kind of protocol-heterogeneity detail that shows the code was written against a real device.

---

## 7. DTOs — [`Application/DTOs/`](MowIT/Application/DTOs/)

```csharp
public record BoundaryDto(int ZoneId, string ZoneName, int PointCount);

public record MotorCommandDto(float LinearVel, float AngularVel) {
    public static MotorCommandDto Stop => new(0f, 0f);
    public MotorCommandDto Clamped() => new(Math.Clamp(LinearVel, -0.5f, 0.5f),
                                            Math.Clamp(AngularVel, -1.0f, 1.0f));
}
```

The clamp limits (±0.5 m/s linear, ±1.0 rad/s angular) are the physical safety envelope of the mower, and they match what `ControlViewModel.OnJoystickMoved` applies directly:
```csharp
float lin =  normalizedY * 0.5f;
float ang = -normalizedX * 1.0f;
```
**Honest note:** both DTOs are currently unused — the clamping lives in the ViewModel instead. The clean version would be to have the ViewModel build a `MotorCommandDto(...).Clamped()` and pass that down, so the limits are defined in exactly one place. Good "what would you improve" answer.
