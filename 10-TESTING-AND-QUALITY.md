# 10 — TESTING AND QUALITY

**Read this chapter before the defence. The professor *will* ask about testing.**

---

## 1. The honest current state

> **There is no automated test project in this solution.**

`mowit.sln` contains exactly four projects — `MowIT`, `MowIT.Shared`, `MowIT.ScheduleServer`, `MowIT.RobotSimulator`. None of them references xUnit, NUnit, MSTest, Moq or FluentAssertions. There are no `*.Tests` folders and no `[Fact]`/`[Test]` methods anywhere in the repository.

**Do not pretend otherwise.** If you are asked "how did you test it?", the honest and defensible answer is:

> "Testing in this project is currently **manual and simulation-driven**, not automated. I built a full software robot — `SimulatedRobotService` in the app and `VirtualRobot` in the network simulator — that reproduces the real firmware's protocol, timing, physics and failure modes, so I can exercise every path of the application without hardware. I also built instrumentation into the app: a session event log, a GPS trace CSV, and link-latency statistics, which give me measurable evidence rather than 'it seemed to work'. What is missing is an xUnit project. The architecture was built for it — everything depends on interfaces — and I can show you exactly which tests I would write and how, because nothing in the design blocks them."

That answer is strong. "I tested it thoroughly" with no artefacts is weak. Read the rest of this chapter so you can back it up.

---

## 2. What testing infrastructure *does* exist

### 2.1 The simulator as a test harness

[`SimulatedRobotService`](MowIT/Infrastructure/Simulator/SimulatedRobotService.cs) (616 lines) is not a stub. It is a behavioural double that reproduces:

* motor acceleration limits (0.8 m/s², 3.0 rad/s²) — so UI smoothing and latency behave realistically;
* dead-reckoning integration of position into real latitude/longitude;
* an **RTK accuracy ramp** (800 mm → 12 mm at 100 mm/s) — so the `Set Base` accuracy gate is genuinely exercised, not bypassed;
* a **600 ms manual-mode watchdog** — so the joystick keep-alive logic is genuinely required;
* the full capture state machine with realistic rejections (`NO_DATUM`, `NOT_READY`, `TOO_FEW_POINTS`, `NO_POLYGON`, `NO_EXIT`, `ACCURACY`);
* route following at 0.3 m/s with a 0.30 m waypoint radius;
* the exact ASCII protocol strings the real firmware emits, with an artificial 120 ms latency.

**This is the difference between a mock and a simulator, and it is worth saying explicitly:** a mock returns canned answers; a simulator has *state and dynamics*, so it can produce failure modes you did not think to script. Every bug in the boundary-recording flow was found by driving the simulator.

`VirtualRobot` in [`MowIT.RobotSimulator`](MowIT.RobotSimulator/VirtualRobot.cs) does the same for the network path, and — importantly — it is a **pure class with no I/O**, so it is directly unit-testable today with no refactoring.

### 2.2 Instrumentation = evidence

| Artefact | Produced by | What it proves |
|---|---|---|
| `logs/session_*.log` | [`EventLogService`](MowIT/Application/Logging/EventLogService.cs) | Every command and reply with ms timestamps — a complete audit trail of a run |
| `logs/gpstrace_*.csv` | [`GpsTraceService`](MowIT/Application/Services/GpsTraceService.cs) | The actual path driven + accuracy over time — plottable experimental data |
| Link statistics | [`GreenTitanSppService.LogLinkStats`](MowIT/Infrastructure/ClassicBt/GreenTitanSppService.cs) | `sent / acked / %, latency avg / min / max` — quantitative link quality |
| Geofence breach lines | [`GeofenceMonitor`](MowIT/Application/Services/GeofenceMonitor.cs) | Metres past the boundary **and** GPS accuracy at that instant |

The git history records commits adding "session log and GPS trace files for measurements" — the instrumentation was built deliberately for measurement, and you should present those files as results.

### 2.3 Null objects = ready-made test doubles

[`NullRobotService`](MowIT/Infrastructure/NullRobotService.cs) already implements all five robot interfaces with empty observables and no-op methods. It is currently unused in the app but is exactly the base class a test fake would derive from.

### 2.4 The server is integration-test-ready

[`MowIT.ScheduleServer/Program.cs`](MowIT.ScheduleServer/Program.cs) ends with:

```csharp
public partial class Program { }
```

That line exists **only** so a test project can write `WebApplicationFactory<Program>` — top-level-statement programs otherwise generate an inaccessible `Program` class. Its presence shows integration testing was designed for.

---

## 3. Manual test procedures actually used

These are the scripts followed during development. Present them as your test protocol.

### TP-1 · Connection lifecycle
1. Launch → Login → Scan → *Scan for Mowers*.
2. **Expect:** two devices within ~0.5 s; status chip turns from grey to green on connect; navigation to Dashboard.
3. Force a disconnect (stop the robot simulator process, or call `DisconnectAsync`).
4. **Expect:** `AppShell` navigates back to `//scan` and shows the "Disconnected" alert exactly **once**.

### TP-2 · GPS accuracy gate
1. Connect and immediately press **Set Base**.
2. **Expect:** failure. Event log shows `GPS/CAPTURE/BASE/FAIL/ACCURACY (have ~800 mm, need < 50 mm)`; the Dashboard toasts *"GPS not accurate enough yet - wait for RTK"*.
3. Wait ~8 s, press again.
4. **Expect:** success; `HasDatum=true`; the local map switches from the "Set base to enable map" hint to a live grid; `Record` becomes enabled.

### TP-3 · Boundary recording, happy path
1. With a datum set, press **Record** → press **Mark** four times, driving between presses.
2. **Expect:** the point counter increments, dots appear on the Skia map, a toast per point.
3. Press **New outline** → **Expect:** the polygon fills in, `PolygonCount = 1`, the point counter resets to 0.
4. Press **Save boundary**.
5. **Expect:** two commands go out — `MOWER/CAPTURE/EXIT` then `MOWER/CAPTURE/END`; then `CaptureEndMessage(true)`; `HasPath = true`; the zone appears in SQLite; the event log shows `walked boundary saved as GPS zone "Walked HH:mm" (N pts) - geofence armed`.

> **Why the extra EXIT command:** both simulators reject `CAPTURE/END` with `NO_EXIT` unless an exit point was marked, and no UI button ever sent `RobotAction.CaptureExit`. `SaveBoundaryAsync` now sends it immediately before `BoundaryRecordEnd`, recording the mower's current position as the exit. On the real GreenTitan SPP transport `CaptureExit` has no command mapping, so it logs "not supported by firmware" and is a harmless no-op.

### TP-4 · Boundary recording, failure paths
| Action | Expected |
|---|---|
| **Mark** before setting a base | `CAPTURE_POINT_FAIL/NO_DATUM`; recording aborted; all collections cleared; "please press Record to start over" |
| **New outline** with 2 points | button disabled (`CanCaptureOutline` requires ≥ 3) |
| **Save** with 0 polygons and 2 points | button disabled (`CanSaveBoundary`) |
| **Save** after `CAPTURE/START` but with no closed polygon | `CAPTURE/END/FAIL/NO_POLYGON` |

### TP-5 · Geofence
1. Save a small boundary by **any** of the three routes — walk it on the Control page, capture it in Plan mode, or draw it on the Map page. In every case the event log should immediately show `active zone "<name>" (N pts)`; **no reconnect should be required**.
2. Drive the robot out of it with the joystick.
3. **Expect:** within 500 ms a `Stop` command; a warning toast `⚠ Left zone "…" - stopping`; a log line stating the metres past the boundary and the GPS accuracy; **exactly one** breach line, not a repeated flood.
4. Drive back inside → **Expect:** one `robot back inside zone` line.
5. **Boundary-following regression:** plan a route on that zone and press Mow. **Expect:** mowing runs to completion. The route starts exactly on the polygon edge and the simulator teleports onto waypoint 0, so a fence without the `MinBreachMarginMeters` tolerance stops the mow within 500 ms of pressing the button — non-deterministically, because a point exactly on an edge is decided by floating-point noise.
6. **Zone-mismatch regression:** save zone A, then draw a different zone B somewhere else and press Mow on B **without** saving it first. **Expect:** mowing runs. Before the fix the fence was still armed on A and stopped B's mow instantly with a breach distance of tens or hundreds of metres.

### TP-6 · Route planning
1. Draw a boundary on the Map page (≥ 3 taps in drawing mode).
2. Press **Calc Route** with *Boustrophedon*, then with *Spiral*.
3. **Expect:** an orange path renders; `RouteInfo` shows waypoint count, total metres and strategy name; the two patterns look visibly different.
4. Edit the boundary (undo a point).
5. **Expect:** the route is cleared and `RouteInfo` returns to "No route - tap Calc Route".

### TP-7 · Scheduling
1. Save a schedule for today, one minute in the future, linked to a saved zone.
2. **Expect:** within 20 s of that minute, `MowingSchedulerService` fires; the route uploads; mowing starts; `LastExecuted` is persisted.
3. Wait through the rest of the minute.
4. **Expect:** it does **not** fire a second time.
5. Disconnect the robot and repeat.
6. **Expect:** a warning "Scheduled mow skipped - robot not connected" and no start.

### TP-8 · Cloud round trip (`MultiTransportFactory`)
1. Start the server, start the robot simulator, run the app, choose **WiFi**, connect.
2. Move the joystick → **Expect:** `RX cmd Motor …` in the robot console; the position updates in the app within ~1 s.
3. Save a schedule → **Expect:** `schedules synced from cloud: v1, 1 schedule(s)` in the robot console.
4. Kill the robot process → **Expect:** after ~3 s (6 failed polls) the app drops the connection and returns to Scan.
5. Stop the server instead → **Expect:** the Scan page shows *"Can't reach the server. Tried: … Is MowIT.ScheduleServer running…"*.

### TP-9 · Cross-platform smoke
Run the Windows head and the Android emulator head; verify layout switching (`OnIdiom Desktop` moves the joystick to a side panel and widens the pages), that the Android BLE permission dialog appears in Bluetooth mode, and that `10.0.2.2` is chosen automatically on the emulator.

---

## 4. The test suite that *should* exist

If you want to strengthen the project before the defence, this is the highest-value work — and it is achievable in a day because the architecture already supports it.

### 4.1 Setup

```powershell
dotnet new xunit -o MowIT.Tests
dotnet sln mowit.sln add MowIT.Tests/MowIT.Tests.csproj
dotnet add MowIT.Tests reference MowIT/MowIT.csproj MowIT.Shared/MowIT.Shared.csproj
dotnet add MowIT.Tests package FluentAssertions
dotnet add MowIT.Tests package Microsoft.Reactive.Testing
```

> **One practical caveat, and it is worth knowing:** `MowIT` is a MAUI project with platform-specific target frameworks, so a plain `net8.0` test project cannot reference it directly. The clean fix is to move `Domain/` (and ideally `Application/`) into a `MowIT.Core` class library targeting plain `net8.0`, which both `MowIT` and `MowIT.Tests` reference. **That refactor is itself a great thing to describe in the defence**, because it shows you understand *why* the layers are separated: the pure layers have no MAUI dependency, so nothing prevents extracting them.
>
> `MowIT.Shared`, `MowIT.ScheduleServer` and `MowIT.RobotSimulator` are all plain `net8.0` and can be tested **today with no refactoring at all**.

### 4.2 Domain tests — pure functions, no mocks needed

```csharp
public class LocalProjectionTests
{
    [Fact]
    public void Origin_projects_to_zero()
    {
        var origin = new GpsPoint(43.8563, 18.4131);
        var (e, n) = new LocalProjection(origin).ToLocal(origin);
        e.Should().BeApproximately(0, 1e-6);
        n.Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void Round_trip_is_lossless_within_a_millimetre()
    {
        var proj = new LocalProjection(new GpsPoint(43.8563, 18.4131));
        var back = proj.ToGps(120.0, -45.0);
        var (e, n) = proj.ToLocal(back);
        e.Should().BeApproximately(120.0, 1e-3);
        n.Should().BeApproximately(-45.0, 1e-3);
    }

    [Fact]
    public void East_offset_matches_haversine_distance()
    {
        var origin = new GpsPoint(43.8563, 18.4131);
        var proj   = new LocalProjection(origin);
        var p      = proj.ToGps(100.0, 0.0);
        origin.DistanceTo(p).Should().BeApproximately(100.0, 0.05);   // agree within 5 cm
    }
}

public class BoundaryZoneTests
{
    private static BoundaryZone Square() => new() { Points = {
        new(43.8560,18.4130), new(43.8560,18.4140), new(43.8570,18.4140), new(43.8570,18.4130) } };

    [Fact] public void Centre_is_inside()      => Square().Contains(new GpsPoint(43.8565,18.4135)).Should().BeTrue();
    [Fact] public void Far_point_is_outside()  => Square().Contains(new GpsPoint(43.8600,18.4200)).Should().BeFalse();

    [Theory]
    [InlineData(43.8565, 18.4135, true)]
    [InlineData(43.8555, 18.4135, false)]   // south of the square
    [InlineData(43.8565, 18.4150, false)]   // east of the square
    public void Containment_matrix(double lat, double lon, bool expected)
        => Square().Contains(new GpsPoint(lat, lon)).Should().Be(expected);

    [Fact]
    public void Area_is_positive_and_plausible()
        => Square().AreaSquareMeters().Should().BeInRange(7_000, 10_000);

    [Fact]
    public void Area_is_independent_of_winding_direction()
    {
        var cw  = Square();
        var ccw = new BoundaryZone { Points = Enumerable.Reverse(cw.Points).ToList() };
        ccw.AreaSquareMeters().Should().BeApproximately(cw.AreaSquareMeters(), 0.01);
    }

    [Fact]
    public void Distance_to_boundary_is_zero_on_a_vertex()
        => Square().DistanceToBoundaryMeters(Square().Points[0]).Should().BeApproximately(0, 0.01);

    [Fact]
    public void Fewer_than_three_points_is_invalid()
        => new BoundaryZone { Points = { new(1,1), new(2,2) } }.IsValid.Should().BeFalse();
}

public class BoustrophedonStrategyTests
{
    [Fact]
    public void Every_waypoint_lies_inside_the_zone()
    {
        var zone  = Square();
        var route = new BoustrophedonStrategy().GenerateRoute(zone, 1.0f);
        route.Should().NotBeEmpty();
        route.Should().OnlyContain(p => zone.Contains(p));    // ← the property that actually matters
    }

    [Fact]
    public void Degenerate_zone_yields_no_route()
        => new BoustrophedonStrategy().GenerateRoute(new BoundaryZone { Points = { new(1,1), new(2,2) } })
           .Should().BeEmpty();

    [Fact]
    public void Tighter_spacing_produces_more_waypoints()
    {
        var s = new BoustrophedonStrategy();
        s.GenerateRoute(Square(), 0.5f).Count.Should().BeGreaterThan(s.GenerateRoute(Square(), 2.0f).Count);
    }
}

public class MowingRoutePlannerTests
{
    [Fact]
    public void Adaptive_spacing_never_goes_below_the_floor()
        => MowingRoutePlanner.AdaptiveSpacing(TinyZone().Points).Should().BeGreaterThanOrEqualTo(0.5f);

    [Fact]
    public void Adaptive_spacing_grows_for_large_fields()
        => MowingRoutePlanner.AdaptiveSpacing(HugeZone().Points).Should().BeGreaterThan(0.5f);
}
```

### 4.3 Application tests — with hand-written fakes

```csharp
internal sealed class FakeSensors : IRobotSensors {
    public readonly Subject<SensorSnapshot> Sensors = new();
    public readonly Subject<RobotStatus>    Status  = new();
    public IObservable<SensorSnapshot> SensorStream => Sensors;
    public IObservable<RobotStatus>    StatusStream => Status;
    public SensorSnapshot? LastSensor { get; set; }
    public RobotStatus?    LastStatus { get; set; }
}

internal sealed class SpyControl : IRobotControl {
    public readonly List<RobotAction> Actions = new();
    public readonly List<(float lin, float ang)> Motors = new();
    public Task SendActionAsync(RobotAction a, byte p = 0) { Actions.Add(a); return Task.CompletedTask; }
    public Task SendMotorCommandAsync(float l, float a)    { Motors.Add((l,a)); return Task.CompletedTask; }
}

public class RobotStateMachineTests
{
    [Fact] public void Idle_allows_StartMowing() {
        var sm = new RobotStateMachine(); var s = new FakeSensors(); sm.Start(s);
        s.Status.OnNext(new RobotStatus { State = RobotState.Idle });
        sm.CanExecute(RobotAction.StartMowing).Should().BeTrue();
    }
    [Fact] public void Idle_forbids_Resume() { … sm.CanExecute(RobotAction.Resume).Should().BeFalse(); }
    [Fact] public void Stop_is_always_allowed() {
        foreach (var st in Enum.GetValues<RobotState>()) { …; sm.CanExecute(RobotAction.Stop).Should().BeTrue(); } }
}

public class SaveScheduleUseCaseTests
{
    [Fact]
    public async Task Rejects_a_schedule_with_no_days()
        => await FluentActions.Awaiting(() => new SaveScheduleUseCase(new FakeScheduleRepo())
               .ExecuteAsync(new MowingSchedule { ActiveDays = [], DurationMinutes = 60 }))
           .Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one day*");

    [Fact]
    public async Task Rejects_a_non_positive_duration() { … }
}

public class CommandPipelineTests   // after wiring the guard handlers, see §5
{
    [Fact]
    public async Task Aborts_an_action_that_the_state_machine_forbids() { … spy.Actions.Should().BeEmpty(); }

    [Fact]
    public async Task Aborts_when_the_battery_is_critical() {
        sensors.LastStatus = new RobotStatus { BatteryPct = 3 };
        await pipeline.SendActionAsync(RobotAction.StartMowing);
        spy.Actions.Should().BeEmpty();
    }
}
```

**Testing the geofence deserves its own emphasis** — it is the safety feature, so it is the one that most needs a test:

```csharp
[Fact]
public async Task Sends_Stop_exactly_once_when_the_robot_leaves_the_zone()
{
    var sensors = new FakeSensors(); var control = new SpyControl();
    var conn    = new FakeConnection();
    var monitor = new GeofenceMonitor(sensors, RepoWith(Square()), conn, control, NullEventLog());

    conn.Push(RobotConnectionState.Connected);
    await Task.Delay(50);                                   // allow ReloadAsync to complete

    sensors.Sensors.OnNext(Snapshot(43.8565, 18.4135));     // inside
    sensors.Sensors.OnNext(Snapshot(43.8600, 18.4200));     // outside
    sensors.Sensors.OnNext(Snapshot(43.8601, 18.4201));     // still outside
    await Task.Delay(700);                                  // Sample(500 ms) window

    control.Actions.Should().ContainSingle().Which.Should().Be(RobotAction.Stop);
}
```

> Note: because `GeofenceMonitor` uses `Sample(500 ms)` on the real scheduler, a rigorous version would inject an `IScheduler` and use `Microsoft.Reactive.Testing`'s `TestScheduler` to make the test deterministic instead of using `Task.Delay`. **That is a genuine testability improvement to name**: the Rx operators currently take the default scheduler, so time is not injectable.

### 4.4 Server tests — runnable today, no refactoring

```csharp
public class ScheduleStoreTests
{
    private static SqliteScheduleStore NewStore()
        => new($"Data Source={Path.GetTempFileName()};Mode=ReadWriteCreate");

    [Fact]
    public async Task Replace_increments_the_version()
    {
        var store = NewStore();
        (await store.ReplaceAsync("r1", [Sched(1)], default)).Version.Should().Be(1);
        (await store.ReplaceAsync("r1", [Sched(1), Sched(2)], default)).Version.Should().Be(2);
    }

    [Fact]
    public async Task Replace_removes_schedules_that_are_no_longer_present()
    {
        var store = NewStore();
        await store.ReplaceAsync("r1", [Sched(1), Sched(2)], default);
        var after = await store.ReplaceAsync("r1", [Sched(1)], default);
        after.Schedules.Should().ContainSingle().Which.Id.Should().Be(1);
    }

    [Fact]
    public async Task Robots_do_not_see_each_others_schedules()
    {
        var store = NewStore();
        await store.ReplaceAsync("r1", [Sched(1)], default);
        await store.ReplaceAsync("r2", [Sched(9)], default);
        (await store.GetAsync("r1", default)).Schedules.Should().ContainSingle().Which.Id.Should().Be(1);
    }

    [Fact]
    public async Task Delete_of_a_missing_schedule_returns_false() { … }
}

public class CommandQueueTests
{
    [Fact]
    public void Only_the_newest_motor_command_survives()
    {
        var q = new InMemoryCommandQueue();
        q.Enqueue("r1", Motor(0.1f, 0f));
        q.Enqueue("r1", Motor(0.2f, 0f));
        q.Enqueue("r1", Motor(0.3f, 0f));
        q.Drain("r1").Should().ContainSingle().Which.LinearVel.Should().Be(0.3f);
    }

    [Fact]
    public void Action_commands_are_never_coalesced()
    {
        var q = new InMemoryCommandQueue();
        q.Enqueue("r1", Action("StartMowing")); q.Enqueue("r1", Action("Stop"));
        q.Drain("r1").Should().HaveCount(2);
    }

    [Fact]
    public void Drain_is_destructive()
    {
        var q = new InMemoryCommandQueue(); q.Enqueue("r1", Action("Stop"));
        q.Drain("r1").Should().HaveCount(1);
        q.Drain("r1").Should().BeEmpty();
    }
}

public class RobotEventLogTests
{
    [Fact]
    public void Sequence_numbers_are_monotonic()
    {
        var log = new InMemoryRobotEventLog();
        log.Append("r1", new()).Seq.Should().Be(1);
        log.Append("r1", new()).Seq.Should().Be(2);
    }

    [Fact]
    public void GetSince_returns_only_newer_events_and_advances_the_cursor()
    {
        var log = new InMemoryRobotEventLog();
        log.Append("r1", new()); log.Append("r1", new()); log.Append("r1", new());
        var r = log.GetSince("r1", 1);
        r.Events.Should().HaveCount(2);
        r.Cursor.Should().Be(3);
    }

    [Fact]
    public void The_ring_buffer_is_bounded_at_200()
    {
        var log = new InMemoryRobotEventLog();
        for (int i = 0; i < 250; i++) log.Append("r1", new());
        log.GetSince("r1", 0).Events.Should().HaveCount(200);
    }
}

public class TelemetryStoreTests
{
    [Fact] public void Unknown_robot_is_offline()
        => new InMemoryTelemetryStore().Get("nobody").IsOnline.Should().BeFalse();

    [Fact] public void A_just_updated_robot_is_online()
    { var s = new InMemoryTelemetryStore(); s.Update("r1", new()); s.Get("r1").IsOnline.Should().BeTrue(); }
}
```

### 4.5 API integration tests (`WebApplicationFactory<Program>`)

```csharp
public class ScheduleApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public ScheduleApiTests(WebApplicationFactory<Program> f)
    {
        _client = f.WithWebHostBuilder(b => b.UseEnvironment("Development")).CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "dev-token-please-replace-in-prod");
    }

    [Fact] public async Task Healthz_needs_no_token()
        => (await new HttpClient{BaseAddress=_client.BaseAddress}.GetAsync("/healthz"))
           .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact] public async Task Endpoints_reject_a_missing_token() { … 401 … }
    [Fact] public async Task Endpoints_reject_a_wrong_token()   { … 401 … }

    [Fact]
    public async Task Posting_a_command_returns_202_and_it_can_be_drained_once()
    {
        (await _client.PostAsJsonAsync("/robots/demo-robot-01/commands",
            new RobotCommandDto { Kind = CommandKinds.Action, ActionName = "StartMowing" }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await _client.GetFromJsonAsync<CommandListResponse>("/robots/demo-robot-01/commands"))!
            .Commands.Should().ContainSingle();
        (await _client.GetFromJsonAsync<CommandListResponse>("/robots/demo-robot-01/commands"))!
            .Commands.Should().BeEmpty();
    }

    [Fact] public async Task Deleting_a_missing_schedule_returns_404() { … }
}
```

### 4.6 `VirtualRobot` tests — pure logic, zero infrastructure

```csharp
public class VirtualRobotTests
{
    [Fact]
    public void Capture_base_fails_before_the_accuracy_has_converged()
        => new VirtualRobot().ApplyCommand(Action("CaptureBase"))
           .Should().ContainSingle().Which.Reason.Should().Be("BASE_CAPTURE_FAIL/ACCURACY");

    [Fact]
    public void Capture_base_succeeds_after_the_accuracy_ramp()
    {
        var r = new VirtualRobot();
        for (int i = 0; i < 20; i++) r.Tick(0.5);                // 10 s → 800−1000 → clamps to 12 mm
        r.ApplyCommand(Action("CaptureBase")).Should().ContainSingle()
         .Which.Type.Should().Be(RobotEventTypes.BaseCaptured);
    }

    [Fact]
    public void Start_mowing_without_a_datum_fails()
        => new VirtualRobot().ApplyCommand(Action("StartMowing"))
           .Should().ContainSingle().Which.Reason.Should().Be("MOWER_START_FAIL/NO_DATUM");

    [Fact]
    public void The_manual_watchdog_stops_the_motors_after_600_ms()
    {
        var r = new VirtualRobot();
        r.ApplyCommand(Motor(0.5f, 0f));
        for (int i = 0; i < 10; i++) r.Tick(0.2);                 // 2 s with no new command
        r.ToTelemetry().IsMotorMoving.Should().BeFalse();
    }

    [Fact]
    public void Ending_a_recording_with_no_closed_polygon_fails()
    { … Reason.Should().Be("NO_POLYGON"); }

    [Fact]
    public void Acceleration_is_rate_limited()
    {
        var r = new VirtualRobot(); r.ApplyCommand(Motor(0.5f, 0f));
        r.Tick(0.1);
        r.ToTelemetry().LinearSpeed.Should().BeLessThan(0.5f);    // 0.8 m/s² · 0.1 s = 0.08 m/s
    }
}
```

### 4.7 Coverage targets

| Layer | Target | Rationale |
|---|---|---|
| Domain (geometry, strategies, entities) | **90 %+** | Pure functions, no dependencies — no excuse for less |
| Application (use cases, state machine, geofence) | **80 %+** | The rules that must not silently break |
| Server stores + endpoints | **80 %+** | Small, pure, and testable with no refactoring |
| Infrastructure transports | ~30 % | Dominated by platform APIs; better covered by the simulator + manual runs |
| Presentation (ViewModels) | ~50 % | Computed properties and command guards are testable; rendering is not |

---

## 5. Known defects and gaps (say these before you are asked)

| # | Issue | Where | Severity | Fix |
|---|---|---|---|---|
| 1 | **No automated tests** | whole solution | High | §4 |
| 2 | `BatteryCheckHandler` and `StateGuardHandler` are written but **not wired** into the chain | [`CommandPipeline.cs:22-26`](MowIT/Application/Pipeline/CommandPipeline.cs#L22) | Medium | `logging.SetNext(guard).SetNext(battery).SetNext(dispatch);` |
| 3 | `CommandPipeline` takes `sensors`/`stateMachine` it never uses | same | Low | resolved by fixing #2 |
| 4 | Transport choice is a **compile-time constant** | [`MauiProgram.cs:25`](MowIT/MauiProgram.cs#L25) | Medium | read from configuration or a settings screen |
| 5 | `MowingSchedulerService._ticking` is a plain `bool`, not atomic | [`MowingSchedulerService.cs:33`](MowIT/Infrastructure/Services/MowingSchedulerService.cs#L33) | Low | `Interlocked.CompareExchange` |
| 6 | `BoundaryRepository.SaveAsync` is not transactional | [`BoundaryRepository.cs:52`](MowIT/Infrastructure/Persistence/BoundaryRepository.cs#L52) | Low | `RunInTransactionAsync` |
| 7 | Server does not verify the token's robot id matches `{robotId}` | [`Program.cs:53`](MowIT.ScheduleServer/Program.cs#L53) | Medium (security) | authorisation policy on the `robot_id` claim |
| 8 | Plain HTTP, static shared token | server + `WifiRobotOptions` | Medium (security) | HTTPS + per-device rotating tokens |
| 9 | `CancellationToken` accepted but not forwarded to Dapper | [`ScheduleStore.cs:53`](MowIT.ScheduleServer/Storage/ScheduleStore.cs#L53) | Low | use `CommandDefinition(..., cancellationToken: ct)` |
| 10 | BLE boundary index/total are single bytes → 255-point cap | [`BlePacketSerializer.cs:63`](MowIT/Infrastructure/Ble/BlePacketSerializer.cs#L63) | Low | widen to 16-bit |
| 11 | Unused code: `BoundaryZoneBuilder`, `NullRobotService`, `GpsStatusBadge`, `MotorCommandDto`, `BoundaryDto`, `SerializeSchedule`, 8 converters | various | Cosmetic | use or delete |
| 12 | `EventLogService` sits in `Application/` but uses MAUI types | [`EventLogService.cs`](MowIT/Application/Logging/EventLogService.cs) | Low (purity) | `IEventLog` in Domain, implementation in Infrastructure |
| 13 | `AppShell` uses a service-locator call instead of injection | [`AppShell.xaml.cs:19`](MowIT/AppShell.xaml.cs#L19) | Low | pass `IRobotConnection` from `App` |
| 14 | `SpiralInwardStrategy` uses a radial shrink, not a true polygon offset | [`SpiralInwardStrategy.cs:30`](MowIT/Domain/Strategies/SpiralInwardStrategy.cs#L30) | Low | NTS `Buffer(-d)` |
| 15 | Boustrophedon rows are always east–west | [`BoustrophedonStrategy.cs`](MowIT/Domain/Strategies/BoustrophedonStrategy.cs) | Low | rotate to the polygon's principal axis |
| 16 | Rx uses the default scheduler → time is not injectable in tests | geofence, joystick, all VMs | Low | inject `IScheduler` |
| 17 | Stray file `MowIT/Domain/Enums/mowit.code-workspace` | — | Cosmetic | delete |
| 18 | `LowBatteryWarningMessage` is published but nothing subscribes | [`DashboardViewModel.cs:321`](MowIT/Presentation/ViewModels/DashboardViewModel.cs#L321) | Cosmetic | handle it or remove it |

**Presenting a numbered defect list like this is a strength, not a weakness.** It demonstrates that you have audited your own work, and it lets *you* choose which weaknesses get discussed.

---

## 6. Quality practices that *are* in the code

Do not undersell these — point at them explicitly:

* **Nullable reference types enabled** on all four projects; `?` and `!` are used deliberately.
* **`ConfigureAwait`-free but `MainThread`-correct** — every UI mutation from a background stream is explicitly marshalled.
* **Defensive parsing everywhere** — `TryParse` with `InvariantCulture`, length checks before every binary read, `?? throw` for required lookups.
* **Bounded resources** — 2000 log entries, 200 server events, ~2500 route waypoints, 30 s scan timeout, 4 s HTTP timeout, 6 consecutive-failure limit. Nothing in the system grows without a ceiling.
* **Correct disposal** — `IDisposable` on every service holding subscriptions, `using var` on every `SKPaint`, `Dispose` on all Rx subjects.
* **Thread safety where it matters** — `SemaphoreSlim` for async DB init, `lock` around file writes and mutable lists, `ConcurrentDictionary` in the server, `Interlocked` for the manual-mode recovery gate.
* **No SQL injection surface** — Dapper parameters and `sqlite-net` parameterised commands throughout.
* **Timing-safe token comparison** on the server.
* **Structured logging** — `_logger.LogInformation("Action {Action} -> {Cmd}", action, cmd)` with named placeholders, not string interpolation.
* **Graceful degradation** — a failed log file, a failed HTTP push and a failed command write all degrade quietly instead of crashing the app.
