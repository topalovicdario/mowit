# 13 — DEFENCE SCRIPT AND EXPECTED QUESTIONS

---

## 1. A 12-minute presentation script

### Slide 1 · The problem (1 min)
> "Robotic lawn mowers traditionally need a physical wire buried around the lawn. I wanted to replace that wire with software: the user drives the robot around the garden once, the app records the corners as GPS coordinates, and from then on that polygon *is* the boundary. To do that you need centimetre-accurate positioning, real geometry, and a reliable link to the robot — and those three things are what this project is about."

### Slide 2 · What the system is (1 min)
Show the four-box diagram.
> "There are four programs. The MAUI client — one C# codebase that runs on Android, iOS, macOS and Windows. A shared DTO library. An ASP.NET Core server with SQLite. And a console robot simulator, so the whole system can be demonstrated with no hardware. The app can reach the mower three ways: Classic Bluetooth serial, Bluetooth Low Energy, or WiFi through the server."

### Slide 3 · Architecture (2 min)
Show the four rings.
> "Clean Architecture. Domain in the centre — entities, the GPS geometry, the mowing algorithms — with zero external dependencies. Application around it — use cases, the geofence, the command pipeline. Infrastructure — Bluetooth, HTTP, SQLite, the simulator. Presentation on the outside — pages and ViewModels in MVVM.
>
> The dependency rule is what makes this pay off. Because the mower can be reached three ways, I defined five small interfaces — scanner, connection, sensors, control, boundary — and wrote one implementation per transport. Everything above those interfaces is written once. Switching from the simulator to real hardware is **one line** in the composition root."

### Slide 4 · The geometry (2 min) — *your strongest technical slide*
> "You cannot do geometry on latitude and longitude, because a degree of longitude is 111 km at the equator and zero at the poles. So I implemented a proper local tangent-plane projection: geodetic to ECEF on the WGS-84 ellipsoid, then rotate into East–North–Up metres anchored at a datum point the user captures. All the geometry runs in that flat metre frame — ray-casting point-in-polygon for the geofence, point-to-segment distance for the breach report, the shoelace formula for area, and the mowing route planning — and the results are projected back to GPS before they go to the robot. The inverse uses Bowring's formula, which is a non-iterative closed-form approximation accurate to well under a millimetre."

### Slide 5 · The algorithms (1.5 min)
> "Two mowing strategies behind one interface. Boustrophedon sweeps horizontal scan-lines through the polygon; for each line I compute the crossings with the polygon edges, sort them, and take them **in pairs** — that's what makes it work correctly on concave, L-shaped gardens — then alternate direction each row so the robot snakes. The Spiral strategy shrinks the outline toward its centroid repeatedly.
>
> The spacing is adaptive: never below 50 cm, and never more than about 2 500 waypoints, because at 50 ms per BLE packet a naive 30 cm spacing on a large field would take over an hour to upload."

### Slide 6 · Live demo (3 min)
Run the three-process cloud demo (see [`02-SOLUTION-AND-BUILD.md §8`](02-SOLUTION-AND-BUILD.md)):
1. Connect over WiFi; show `RX cmd Motor` appearing in the robot console as you move the joystick.
2. Press **Set Base** immediately → **it fails**. Explain the accuracy gate. Wait, press again → succeeds.
3. Record → Mark ×4 → New outline → Save. Show the polygon on the Skia map.
4. Show the event log filling with the exact protocol strings.
5. Drive out of the zone → the geofence stops the robot and toasts a warning.

### Slide 7 · Safety and reliability (1.5 min)
> "Five independent safety mechanisms: the geofence stops the robot when it leaves the saved zone, edge-triggered so it fires once and logs how many metres past the boundary and what the GPS accuracy was; a connection guard drops every command when the link is down; leaving the Control page sends a Stop; the robot has a 600 ms motor watchdog, which is why the app sends a keep-alive every 250 ms; and a scheduled mow is skipped, never queued, if the robot is not connected — you do not want blades starting unexpectedly three hours late."

### Slide 8 · Honest assessment (1 min)
> "What is not done: there is no automated test project. Testing today is manual and simulator-driven, plus the instrumentation I built — session logs, GPS traces, and link-latency statistics. The architecture is ready for tests: everything depends on interfaces, and I can show the exact test suite I would write. Two guard handlers in the command pipeline are implemented but not yet wired in. The server security is a static bearer token over plain HTTP, which is fine for a lab but not for deployment."

### Slide 9 · Numbers (30 s)
| Metric | Value |
|---|---|
| Projects | 4 |
| C# files | ~90 |
| Design patterns applied | 25 |
| Platforms from one codebase | 4 |
| Transports | 4 (SPP, BLE, WiFi, simulator) |
| Domain interfaces | 11 |
| REST endpoints | 12 |
| Measured BT round-trip latency | typically 70–140 ms |

---

## 2. Expected questions, with answers

### Architecture

**Q: Why Clean Architecture for a student project? Isn't it over-engineering?**
> "It earned its keep three times over. The mower is reachable over Classic Bluetooth, BLE and WiFi, and I also needed a simulator. Four implementations, one set of interfaces, and the UI was written once. If I had put `BluetoothSocket` in the ViewModels, adding WiFi would have meant rewriting the UI. The proof is that switching transports is a single line in `MauiProgram`."

**Q: Show me that the dependency rule actually holds.**
> "Open any file in `Domain/`. The `using` statements only reference `MowIT.Domain.*` and `System.*`. No SQLite, no Plugin.BLE, no MAUI types on the model classes. And I'll be honest about the one exception: `EventLogService` sits in `Application/` but uses MAUI's `Color`, `MainThread` and `FileSystem`. Strictly it should be an `IEventLog` interface in Domain with the implementation in Infrastructure."

**Q: Why five interfaces instead of one `IRobot`?**
> "Interface Segregation. `ScanViewModel` has no business being able to send motor commands. With segregated interfaces, a class's constructor signature declares exactly which capabilities it needs, and test fakes only implement what they use."

**Q: How does one object serve five interfaces?**
> "`services.AddSingleton<IRobotScanner>(sp => sp.GetRequiredService<SimulatedRobotService>())` — a factory lambda that resolves the same singleton. If I had written `AddSingleton<IRobotScanner, SimulatedRobotService>()` four times I would have got four separate robots with four different GPS positions."

**Q: Why are the tab ViewModels singletons but Login/Scan transient?**
> "State and subscriptions. The tab ViewModels subscribe to sensor streams in their constructors and hold the recorded polygon, the trail and the drawn boundary. Making them transient would create a new subscription on every tab switch — a leak — and would wipe the user's work. Login and Scan are visited once and discarded."

### Geometry and algorithms

**Q: Explain your coordinate transformation.**
> Use Slide 4's answer. Then: "The projection object caches the origin's sines and cosines in its constructor, so `ToLocal` is three subtractions and six multiplications with no trigonometry — which matters when I project thousands of route waypoints."

**Q: Why not just use `Math.Sqrt(dLat² + dLon²)`?**
> "Because that mixes units. Degrees of latitude and longitude are different distances, and the longitude scale depends on the latitude by a factor of cos(lat). At 43.8° north that factor is 0.72 — so a naive Euclidean distance would be wrong by roughly 28 % in one axis."

**Q: How does the point-in-polygon test work?**
> "Ray casting. Shoot a ray from the point and count how many polygon edges it crosses; odd means inside. The condition `(yi > py) != (yj > py)` checks whether the edge straddles the point's horizontal line — and that half-open comparison also handles a ray passing exactly through a vertex, counting it once instead of twice. It's O(n) and allocation-free apart from the projection."

**Q: Does Boustrophedon handle a non-convex garden?**
> "Yes — that's exactly why the crossings are sorted and taken **in pairs**. A U-shaped lawn produces four crossings on some scan-lines, and the pairs `(0,1)` and `(2,3)` are the two separate interior strips. What it does *not* do is rotate the rows: they are always east–west, so a long diagonal garden is mowed less efficiently than optimal. The fix is to align the rows with the polygon's minimum-area bounding box."

**Q: Is your spiral a true polygon offset?**
> "No, and I want to be precise about that. It's a radial shrink toward the centroid. For a convex, roughly round shape it's nearly identical to a true Minkowski erosion, but on an elongated or concave polygon it can self-intersect or leave uncut corners. A production version would use NetTopologySuite's `Buffer(-d)` — the library is already referenced for the map."

**Q: Why cap the route at 2 500 waypoints?**
> "Upload time. The BLE boundary protocol sends one point per 20-byte packet with a 50 ms gap. At 30 cm spacing a 100 × 100 m field would be about 110 000 waypoints — over 90 minutes of uploading. `AdaptiveSpacing` widens the spacing on large fields to hold the count near 2 500, with a hard 50 cm floor so the blade still overlaps between rows."

### Protocols and transports

**Q: How do you know where one message ends over Bluetooth SPP?**
> "That was the hardest bug. SPP is a raw byte stream with no framing, and the firmware doesn't reliably terminate replies, so two messages can arrive glued together. `FindMessageSplit` prefers a newline; if there isn't one, it splits immediately before the next occurrence of `GPS/` or `MOWER/`, searching from index 1 so the current message's own prefix doesn't match; if neither is found, it returns −1 meaning 'incomplete' and the buffer keeps accumulating."

**Q: How do you know the link is healthy?**
> "The app measures it. Every outgoing command is timestamped under a correlation key derived from its path; every reply is reduced to the same key and matched. On disconnect it logs sent, acknowledged, percentage, and average/min/max latency. Telemetry polls are excluded so they don't dominate the statistics. I have real numbers from real sessions rather than an impression."

**Q: Why don't you retry motor commands?**
> "A velocity command is only valid at the instant it's issued. Re-sending a 300 ms-old velocity would make the robot lurch, and the next command is only 100 ms away anyway. Actions like Start and Stop are discrete, idempotent and important, so those get three attempts 100 ms apart. That asymmetry is deliberate."

**Q: Why does the joystick send a keep-alive?**
> "The robot has a 600 ms watchdog that zeroes the motors if it hears nothing — standard safety behaviour so a dropped link stops the machine. But holding the stick still produces no new touch events, so the app has to repeat the last value. I repeat every 250 ms, comfortably inside the watchdog. The Rx `Switch()` operator tears down the old repeater whenever a new value arrives, so I never accumulate timers."

**Q: Why does WiFi poll instead of using WebSockets or SignalR?**
> "A robot on a home WiFi has no public address — you cannot open a connection *to* it, it can only call out. So both sides poll, and the server is a mailbox. This needs no port forwarding, no persistent-connection infrastructure, and it survives NAT and firewalls. The cost is latency: worst case about a second end-to-end. That's why I present WiFi as a supervisory channel and Bluetooth, at around 90 ms, as the driving channel."

**Q: Won't polling at 10 Hz while the robot polls at 2 Hz cause a backlog?**
> "It would, which is why the server's command queue **coalesces**: when a Motor command is enqueued, any older Motor command for that robot is removed, because only the newest velocity is meaningful. Actions and boundary uploads are never coalesced. Without that the robot would execute five stale velocities per poll and fall further and further behind."

**Q: How do you guarantee the app doesn't miss or duplicate an event?**
> "A cursor. The server assigns a monotonically increasing `Seq` to every event; the client requests `?since=N` with the highest sequence it has seen and updates `N` from the response. Same mechanism as a Kafka offset. Even if a poll fails, the next one picks up exactly where the client left off."

### Database

**Q: Why are boundary points normalised but schedule days stored as JSON?**
> "Different data, different trade-off. A polygon has an unbounded number of vertices and they need an explicit `Order` column, because SQL result order isn't guaranteed and shuffled vertices are a different polygon — so that's a proper one-to-many table. `ActiveDays` is a fixed set of at most seven small integers that I never query on, so a JSON column avoids a join table for no loss. The point is that I chose per-case rather than applying one rule everywhere."

**Q: Why is the server's schedule payload a JSON blob?**
> "Because the server doesn't interpret schedules — it stores and versions them. The columns it queries on, `RobotId` and `ScheduleId`, are real columns; the body is opaque. Adding a field to `ScheduleDto` needs no migration. The cost is that I can't write `WHERE IsActive = 1` in SQL, which the server never needs to do."

**Q: How do you handle schema changes?**
> "Two mechanisms, and both are pragmatic rather than industrial. The app tries `ALTER TABLE … ADD COLUMN ZoneId` and swallows the exception if the column exists, and if `CreateTable` throws it deletes and recreates the database. A production app would use a `PRAGMA user_version` migration ladder. The server uses `CREATE TABLE IF NOT EXISTS`."

**Q: SQL injection?**
> "Not possible. Dapper parameters on the server (`@robotId`) and `sqlite-net` parameterised commands in the app. There is no string concatenation into SQL anywhere in the repository."

### Concurrency

**Q: Telemetry arrives on a background thread. How do you update the UI safely?**
> "Every subscription that touches UI state wraps its body in `MainThread.BeginInvokeOnMainThread`. `EventLogService` even checks `MainThread.IsMainThread` first so it doesn't pay for a marshal it doesn't need. And `Progress<T>` in the upload paths captures the synchronisation context automatically, which is why those callbacks don't need an explicit marshal."

**Q: Where do you actually need locks?**
> "Four places, each for a different reason. A `SemaphoreSlim` for the async database initialisation, because you can't `await` inside a `lock`. A `lock` around every file write in the event log and the GPS trace, because the sensor stream and the connection stream fire on different threads. `ConcurrentDictionary` plus a per-robot `lock` in the server's event log, to protect the read-modify-write of the sequence counter. And `Interlocked.CompareExchange` as a lock-free gate on the manual-mode recovery, because MOVE failures can arrive in bursts and I must not fire a storm of recoveries."

**Q: Is there a race in the scheduler?**
> "There's a theoretical one and I know about it. `_ticking` is a plain `bool`, and the timer doesn't wait for the previous `TickAsync`. Two threads could read `false` simultaneously. `Interlocked.CompareExchange(ref _ticking, 1, 0)` would fix it. In practice the tick is 20 seconds and the work is milliseconds, but it's a real defect and it's on my list."

### Testing

**Q: How did you test this?**
> Use the §1 Slide 8 answer, then walk through [`10-TESTING-AND-QUALITY.md §2 and §3`](10-TESTING-AND-QUALITY.md): the simulator as a behavioural double, the session logs and GPS traces as evidence, and the nine written manual test procedures.

**Q: Why don't you have unit tests?**
> "Time, and one real technical obstacle I should name: the `MowIT` project targets platform-specific frameworks like `net8.0-android`, so a plain `net8.0` test project can't reference it directly. The clean fix is to extract `Domain` and `Application` into a `MowIT.Core` library targeting plain `net8.0` — which is possible precisely *because* those layers have no MAUI dependency. That refactor is the first thing I would do. Meanwhile `MowIT.Shared`, the server and the robot simulator are all plain `net8.0` and are testable today with no refactoring at all — I've written out those tests in my documentation."

**Q: What would you test first?**
> "The geometry and the geofence. `LocalProjection` round-trip accuracy, `Contains` on a known square, `AreaSquareMeters` independent of winding direction, and the property that every waypoint a strategy generates lies inside the zone. Then a test that the geofence sends exactly **one** Stop when the robot leaves — that's the safety-critical behaviour, so it's the one that most deserves a regression test."

### Honest weaknesses

**Q: Is the server secure?**
> "No, and I'd rather say so precisely than be vague. It's a static shared bearer token over plain HTTP, and the endpoints don't yet verify that the token's robot-id claim matches the `{robotId}` in the URL, so any valid token can currently read any robot. What *is* right: the token lives in configuration rather than in code, `UserSecretsId` is already configured, and the comparison uses `CryptographicOperations.FixedTimeEquals` so it isn't vulnerable to a timing attack. The next steps are HTTPS, per-device rotating tokens, and an authorisation policy comparing the claim to the route value."

**Q: I see `BatteryCheckHandler` and `StateGuardHandler` — are they used?**
> "No. They're implemented and tested by hand, but `CommandPipeline` currently links only logging → dispatch. Wiring them in is two lines: `logging.SetNext(guard).SetNext(battery).SetNext(dispatch)`. I left them out during hardware bring-up because a mis-reported firmware state could lock the user out of the controls, and I didn't want a state guard fighting the real robot before I trusted its status reports. Turning them on with the state machine as the source of truth is the correct end state."

**Q: There are classes that are never called.**
> "Yes — `BoundaryZoneBuilder`, `NullRobotService`, `GpsStatusBadge`, `MotorCommandDto`, `BoundaryDto`, the BLE schedule serialiser, and about eight converters. Some are demonstrations of a pattern, some were superseded when the UI changed. I have them listed; they should either be used or deleted, and leaving them is untidy."

**Q: What would you do with another month?**
> "In order: extract `MowIT.Core` and write the test suite; wire in the two guard handlers; move the transport choice from a compile-time constant to a settings screen; put the API on HTTPS with per-device tokens and a robot-id authorisation policy; replace the spiral's radial shrink with a true NTS polygon offset and rotate the boustrophedon rows to the garden's principal axis; and add multi-zone support with exclusion polygons — the capture protocol already supports several outlines, the app just keeps the largest one."

### Curveballs

**Q: Your geofence has a 0.5 m tolerance — isn't that a weaker safety guarantee?**
> "It's the opposite, and this was a real bug I had to fix. The Boustrophedon planner builds each row by intersecting a scan-line with the polygon edges, so every row starts and ends *exactly on* the boundary — and the simulator teleports the robot onto waypoint zero. A point sitting precisely on an edge is decided by a strict `<` comparison in the ray-casting test, so floating-point noise at the 1e-9 level determined inside versus outside. The result was that pressing Mow stopped the mow immediately, at random, on the robot's own legal route. A fence that trips on the path you planned for it is a false positive, not a guarantee. So the breach threshold is now `max(0.5 m, current GPS accuracy)` — hysteresis, which every real geofence has, and it scales with how much you can actually trust the position."

**Q: Did you hit any coordinate-frame bugs?**
> "Two, and they were the same mistake twice. The codebase measures heading as a **compass bearing** — clockwise from North — because that is what the motion model integrates (`north += cos h`, `east += sin h`) and what the dashboard's N/NE/E label assumes. But two pieces of code were written as if it were a standard maths angle, counter-clockwise from East. First the joystick: I negated the angular command to convert 'screen X grows right' into 'positive yaw is counter-clockwise', which is the maths convention — so the mower steered inverted, stick left and it turned right. With a compass bearing, increasing heading *is* clockwise, so the sign must be positive. Second, and subtler: `HeadingRad` is a compass bearing, clockwise from North, because that's what the motion model integrates and what the dashboard's N/NE/E label assumes. But a canvas is a maths plane, counter-clockwise from East, with Y pointing down. I was drawing the mower's nose with `cos` on X and `sin` on Y, which is the maths-angle reading — so the arrow sat exactly 90° out of phase with the direction of travel. Heading due east drove the icon right but pointed the nose up. The fix is `sin` for the east component and `-cos` for the north component. It's invisible in a static screenshot; you only see it once the icon moves."

**Q: What happens if the phone loses GPS mid-mow?**
> "The transport keeps publishing snapshots with the last known position, and the geofence explicitly ignores (0,0) — Null Island — which is what an uninitialised GPS struct reports. The accuracy value keeps flowing, and the fix type degrades from RTK Fixed to Standard, which the Dashboard shows in orange. What the app does *not* currently do is stop the robot on a fix-quality degradation — arguably it should, and that's a good safety improvement."

**Q: Two phones edit schedules at the same time. What happens?**
> "Last writer wins. The app pushes the *whole* schedule list and the server does a full replace inside a transaction with a version bump. There's no merge, so the second push overwrites the first. The `Version` field is the hook for doing better — the client could send the version it based its edit on and the server could reject a stale write with a 409."

**Q: The robot goes out of Bluetooth range mid-command.**
> "The read loop's blocking `Read` throws, the exception filter distinguishes it from a deliberate cancellation, the service publishes `Disconnected` and a `RobotDisconnectedMessage`, `AppShell` navigates back to Scan and shows an alert, `GpsTraceService` closes its CSV, and `GeofenceMonitor` disposes its sensor subscription. The `ConnectionGuardProxy` then silently drops any command issued afterwards."

**Q: Why is the app called MowIT and the robot GreenTitan?**
> "MowIT is the application; GreenTitan is the mower hardware and its firmware protocol. The separation is real — the application ID is `com.greentitan.mowit`, and the code refers to GreenTitan only inside the transport implementations. The Domain layer never mentions it, which is exactly the point: a different mower would be a new adapter, not a new app."

---

## 3. Things to have open during the defence

| Purpose | File |
|---|---|
| The one-line transport switch | [`MowIT/MauiProgram.cs:25`](MowIT/MauiProgram.cs#L25) |
| The five interfaces | [`MowIT/Domain/Interfaces/`](MowIT/Domain/Interfaces/) |
| The geometry | [`MowIT/Domain/Geometry/LocalProjection.cs`](MowIT/Domain/Geometry/LocalProjection.cs) |
| The point-in-polygon test | [`MowIT/Domain/Entities/BoundaryZone.cs:14`](MowIT/Domain/Entities/BoundaryZone.cs#L14) |
| The scan-line algorithm | [`MowIT/Domain/Strategies/BoustrophedonStrategy.cs`](MowIT/Domain/Strategies/BoustrophedonStrategy.cs) |
| The safety system | [`MowIT/Application/Services/GeofenceMonitor.cs:80`](MowIT/Application/Services/GeofenceMonitor.cs#L80) |
| The Rx joystick pipeline | [`MowIT/Presentation/ViewModels/ControlViewModel.cs:214`](MowIT/Presentation/ViewModels/ControlViewModel.cs#L214) |
| The message framing fix | [`MowIT/Infrastructure/ClassicBt/GreenTitanSppService.cs:585`](MowIT/Infrastructure/ClassicBt/GreenTitanSppService.cs#L585) |
| The stream-of-streams switch | [`MowIT/Infrastructure/Transport/RobotTransportRouter.cs:64`](MowIT/Infrastructure/Transport/RobotTransportRouter.cs#L64) |
| The whole API | [`MowIT.ScheduleServer/Program.cs`](MowIT.ScheduleServer/Program.cs) |
| Command coalescing | [`MowIT.ScheduleServer/Storage/CommandQueue.cs:17`](MowIT.ScheduleServer/Storage/CommandQueue.cs#L17) |
| A real session log | `logs/session_*.log` on the device |

---

## 4. The complete honesty list

Keep this in front of you. Volunteering these makes you the author; being caught by them makes you a passenger.

1. No automated test project — testing is manual and simulator-driven (chapter 10).
2. `BatteryCheckHandler` and `StateGuardHandler` are implemented but not wired into the pipeline.
3. The transport is a compile-time constant in `MauiProgram.cs`, not a setting.
4. Unused code: `BoundaryZoneBuilder`, `NullRobotService`, `GpsStatusBadge`, `MotorCommandDto`, `BoundaryDto`, `BlePacketSerializer.SerializeSchedule`, `ConnectToMowerUseCase`, ~8 converters; `LowBatteryWarningMessage` and `ExitCapturedMessage` are published but have no subscriber.
5. `EventLogService` lives in `Application/` but uses MAUI types.
6. `IRobotServiceFactory` (a Domain interface) takes `IServiceCollection`.
7. `AppShell` uses a service-locator call rather than constructor injection.
8. Server security is a static bearer token over plain HTTP, with no robot-id claim check.
9. `MowingSchedulerService._ticking` is not atomic.
10. `BoundaryRepository.SaveAsync` is not transactional.
11. `CancellationToken` is accepted but not forwarded to Dapper in `ScheduleStore`.
12. The BLE boundary protocol caps at 255 points (single-byte index).
13. `SpiralInwardStrategy` is a radial shrink, not a true polygon offset.
14. Boustrophedon rows are always east–west.
15. Rx uses the default scheduler, so time is not injectable for deterministic tests.
16. `MowIT/Domain/Enums/mowit.code-workspace` is a stray editor file in the wrong folder.
17. Multi-polygon capture is supported by the protocol, but only the largest polygon is saved as the geofence zone.
18. The app does not stop the robot when GPS fix quality degrades mid-mow.
