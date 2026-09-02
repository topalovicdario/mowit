# 12 — DATA FLOWS, TRACED LINE BY LINE

*If you can narrate these five flows from memory, you can answer almost any question about the code.*

---

## FLOW 1 — Pressing the joystick moves the robot

**The question a professor asks:** *"Show me what happens between my finger and the motor."*

| # | Layer | File | What happens |
|---|---|---|---|
| 1 | UI | [`JoystickView.cs:94`](MowIT/Presentation/Controls/JoystickView.cs#L94) | `OnTouch(SKTouchAction.Moved)` fires. `e.Handled = true` stops the parent `ScrollView` stealing the gesture |
| 2 | UI | `JoystickView.MoveThumb` | Computes `dx,dy` from the centre; if `dist > _maxRadius`, clamps to the circle edge; raises `JoystickMoved(dx/maxR, **−**dy/maxR)` — the minus makes "up = forward" |
| 3 | View | [`ControlPage.xaml.cs:392`](MowIT/Presentation/Pages/ControlPage.xaml.cs#L392) | `OnJoystickMoved` forwards to the ViewModel. **No logic in the code-behind** |
| 4 | VM | [`ControlViewModel.cs:461`](MowIT/Presentation/ViewModels/ControlViewModel.cs#L461) | `lin = ny * 0.5f` (±0.5 m/s), `ang = nx * 1.0f` (±1.0 rad/s; positive = clockwise, matching the compass-bearing heading). Updates two observable properties, then `_joystickSubject.OnNext((lin, ang))` |
| 5 | Rx | [`ControlViewModel.cs:214`](MowIT/Presentation/ViewModels/ControlViewModel.cs#L214) | `Sample(100 ms)` → at most 10 values/s. Non-idle values become `Return(v).Concat(Interval(250 ms).Select(_ => v))` — send now, then keep-alive every 250 ms. `Switch()` discards the previous keep-alive loop |
| 6 | Rx | same | `SelectMany(Observable.FromAsync(() => _control.SendMotorCommandAsync(lin, ang)))` |
| 7 | App | [`CommandPipeline.cs:37`](MowIT/Application/Pipeline/CommandPipeline.cs#L37) | Builds a `RobotCommandContext { LinearVel, AngularVel }` (`Action` stays null → `IsMotorCommand == true`) and runs the chain |
| 8 | App | `LoggingCommandHandler` | `LogDebug("Motor cmd lin={Lin:F2} ang={Ang:F2}")` |
| 9 | App | `DispatchHandler` | `IsMotorCommand` → calls `_inner.SendMotorCommandAsync(...)` |
| 10 | Infra | `ConnectionGuardProxy` | If `!IsConnected`, returns `Task.CompletedTask` — the command is silently dropped |
| 11 | Infra | `RetryDecorator` | Motor commands **pass straight through, no retry** (a stale velocity would make the robot lurch) |
| 12 | Infra | the active transport | **SPP:** ensure manual mode, then `MOWER/MOVE/0.350,-0.200<` · **BLE:** 8-byte little-endian write, no response · **WiFi:** `POST /commands` with `Kind:"Motor"` · **Simulator:** set `_targetLinear/_targetAngular`, stamp `_lastMoveCmdAt` |
| 13 | Robot | firmware / simulator | Ramps actual velocity toward target at 0.8 m/s² and 3.0 rad/s²; integrates position |
| 14 | Robot | | If no MOVE arrives for 600 ms → the **watchdog** zeroes the target. This is exactly why step 5 sends a 250 ms keep-alive |

**Then the loop closes:**

| # | | |
|---|---|---|
| 15 | Transport | Publishes a new `SensorSnapshot` on `_sensorSubject` |
| 16 | VM | `ControlViewModel` (10 Hz) updates `CurrentPosition`, `GpsAccuracyMm`, `IsManualMode`, `IsMotorMoving`, `MowerLocal`, `MowerHeadingRad`, and appends to `RobotTrail` if it moved > 0.5 m |
| 17 | View | `ControlPage`'s 33 ms timer eases `_dispX/_dispY/_dispHeading` 25 % toward the target and repaints the Skia canvas |
| 18 | Others | `DashboardViewModel` (2 Hz) updates the tiles; `MapViewModel` (1 Hz) moves the map pin; `GeofenceMonitor` (2 Hz) checks containment; `GpsTraceService` writes a CSV row for **every** sample |

**One sentence summary:** *finger → normalised vector → clamped velocities → Rx throttle + keep-alive → chain of responsibility → proxy → decorator → transport → wire → robot; telemetry returns on an observable stream and fans out to five consumers, each at its own rate.*

---

## FLOW 2 — Walking a boundary and arming the geofence

**The most important flow in the app.** It touches every layer.

### Phase A — set the datum

| # | Where | What |
|---|---|---|
| 1 | User | Presses **Set Base**. The button is enabled only while `HasGpsFix` |
| 2 | VM | `CaptureBaseAsync` logs `user pressed Set Base (HasGpsFix=True acc=48mm)` then `RunSafeAsync(() => _control.SendActionAsync(RobotAction.CaptureBase), "Base capture failed - ensure GPS has a good fix")` |
| 3 | Pipeline | `RobotCommandContext { Action = CaptureBase }` → logging → dispatch → proxy → **retry (3 attempts)** → transport |
| 4 | Transport | `GPS/CAPTURE/BASE<` |
| 5 | Robot | If `accuracy > 50 mm` → `GPS/CAPTURE/BASE/FAIL/ACCURACY` → `RobotErrorMessage("BASE_CAPTURE_FAIL/ACCURACY")` → Dashboard toasts *"GPS not accurate enough yet - wait for RTK"*. **Otherwise** stores `(lat, lon)` as the datum, sets `_hasDatum`, **clears `_pathSaved`** (a new origin invalidates any old path) and replies OK |
| 6 | App | `BaseCapturedMessage` is published |
| 7 | VM | `ControlViewModel`: `HasDatum = true`; `_baseGps = CurrentPosition`; `MowerLocal = ProjectToLocal(...)`; logs `HasDatum=true baseGps=(43.8563142, 18.4131087)`; toasts "Base captured" |
| 8 | VM | `DashboardViewModel`: `HasDatum = true`, **`HasPath = false`** — mirroring the firmware's own reset |
| 9 | UI | `[NotifyPropertyChangedFor]` chains fire → `SetupStatusText` becomes "No boundary recorded yet", `CanStartRecording` becomes true, the **Record** button enables, and the Skia map's "Set base to enable map" warning disappears |

### Phase B — walk and mark

| # | Where | What |
|---|---|---|
| 10 | User | Presses **Record** → `BoundaryRecordStart` → `MOWER/CAPTURE/START<` |
| 11 | Robot | Clears all polygons, the exit point and the saved-path flag; state → `RecordingBoundary`; replies OK and emits `BoundaryCleared` |
| 12 | App | `BoundaryClearedMessage` → both VMs set `HasPath = false`. Meanwhile `StatusStream` reports `RecordingBoundary` → `ControlViewModel._statusSub` sets `IsRecordingBoundary = true` and resets both counters |
| 13 | UI | The **Mark** and **New outline** tiles appear; a red `REC` chip shows |
| 14 | User | Drives to a corner with the joystick (Flow 1), presses **Mark** |
| 15 | Robot | Computes the position in the local frame and replies — **SPP:** `MOWER/CAPTURE/POINT/OK/43.8563400,18.4131900` (GPS!) · **simulator/WiFi:** `MOWER/CAPTURE/POINT/OK/1234,-560` (centimetres!) |
| 16 | App | Correspondingly `BoundaryGpsPointCapturedMessage` **or** `BoundaryPointCapturedMessage` |
| 17 | VM | GPS variant: `_baseGps ??= m.Point`, `BoundaryPointCount++`, add to `ActiveGpsPolygon`, project into `ActivePolygon`. Local variant: `BoundaryPointCount++`, add to `ActivePolygon`. Either way a toast fires |
| 18 | View | `ActivePolygon.CollectionChanged` → `LocalMap.InvalidateSurface()` → a new dot and a dashed line appear |
| 19 | | Repeat 14–18 for each corner. At 3 points `CanCaptureOutline` becomes true and **New outline** enables |

### Phase C — close and save

| # | Where | What |
|---|---|---|
| 20 | User | Presses **New outline** → `CaptureOutline` → `MOWER/CAPTURE/OUTLINE<` |
| 21 | Robot | Moves `_activePoints` into `_closedPolygons`, clears the active list, replies OK |
| 22 | VM | `OutlineCapturedMessage` → `PolygonCount++`; `ActivePolygon` → `ClosedPolygons`; `ActiveGpsPolygon` → `ClosedGpsPolygons`; `BoundaryPointCount = 0` |
| 23 | View | The polygon now renders **filled** instead of dashed |
| 24 | User | Presses **Save boundary**. `SaveBoundaryAsync` sends **two** actions: `CaptureExit` → `MOWER/CAPTURE/EXIT<` (marks where the mower stands as the exit — the firmware refuses END without one), then `BoundaryRecordEnd` → `MOWER/CAPTURE/END<` |
| 25 | Robot | Validates: not recording → `NOT_RECORDING`; no closed polygon → `NO_POLYGON`; no exit point → `NO_EXIT`. Otherwise sets `_pathSaved = true` and replies OK |
| 26 | App | `CaptureEndMessage(true)` |
| 27 | VM | **`BuildWalkedGpsZone()`** — prefer the GPS polygons (largest = outer); otherwise convert the local ones with `LocalProjection(baseGps).ToGps(x/100, y/100)`; name it `"Walked HH:mm"` |
| 28 | VM | `HasPath = true`, `IsRecordingBoundary = false`, all four collections cleared, toast "Boundary saved to mower" |
| 29 | App | `PersistWalkedZoneAsync(zone)` → `_repo.SaveAsync(zone)` |
| 30 | Infra | `BoundaryRepository.SaveAsync`: `InitializeAsync()` → insert `BoundaryZoneEntity` (id assigned) → `DELETE FROM BoundaryPoints WHERE ZoneId=?` → insert one `BoundaryPointEntity` per vertex with its `Order` |
| 31 | App | **`await _geofence.ReloadAsync()`** — reloads all zones, picks the newest valid one, logs `active zone "Walked 14:35" (5 pts)` |
| 32 | App | Log: `walked boundary saved as GPS zone "Walked 14:35" (5 pts) - geofence armed` |
| 33 | VM | `DashboardViewModel` also received `CaptureEndMessage` → `HasPath = true` → `CanStartMowing` recomputes → **`ReadinessText` becomes "Ready to mow"** and the Mow button enables |

### Phase D — the fence does its job

| # | Where | What |
|---|---|---|
| 34 | Infra | `GeofenceMonitor` is subscribed to `SensorStream.Sample(500 ms)` |
| 35 | | `CheckGeofence`: skip if no zone; skip if the position is (0,0); `zone.Contains(gps)` runs the ray-casting test |
| 36 | | Robot leaves → `_outside` was false → set it true; `DistanceToBoundaryMeters` computes how far past |
| 37 | | Logs `robot left zone "Walked 14:35" at 43.8564901,18.4133772 - 0.84 m past the boundary, GPS ±0.01 m - stopping` |
| 38 | | Sends `GeofenceBreachMessage` **and** `_control.SendActionAsync(RobotAction.Stop)` |
| 39 | VM | `DashboardViewModel` toasts `⚠ Left zone "Walked 14:35" - stopping` |
| 40 | Robot | `MOWER/MANUAL/OFF` → motors stop, state → `Idle` |
| 41 | | Further outside samples are ignored (`if (_outside) return;`) — **one stop, not a flood** |

---

## FLOW 3 — Drawing a zone on the map and mowing it

| # | Where | What |
|---|---|---|
| 1 | User | Map page → **Draw Boundary** → `ToggleDrawingModeCommand` → `IsDrawingMode = true` (the button turns dark green via `ActiveColor`) |
| 2 | User | Taps the map |
| 3 | View | `MapPage.OnMapInfo`: returns immediately unless `IsDrawingMode`; converts `WorldPosition` from Spherical Mercator to lat/lon with `SphericalMercator.ToLonLat`; marshals to the UI thread; calls `_vm.OnMapTapped(new GpsPoint(lat, lon))` |
| 4 | VM | Adds to `BoundaryPoints`; `NotifyBoundaryChanged()` clears any existing route and refreshes four `Can…` properties plus two commands' `CanExecute` |
| 5 | View | `BoundaryPoints.CollectionChanged` → `UpdateBoundary()`: ≥ 3 points → a closed `Polygon` (green fill + bright outline); exactly 2 → a `LineString`; plus a dot per vertex. `MapView.RefreshGraphics()` |
| 6 | User | Types a zone name, chooses **Boustrophedon** or **Spiral**, presses **Calc Route** |
| 7 | VM | `CalculateRouteAsync`: builds a temporary `BoundaryZone`, then `await Task.Run(() => _planner.Plan(zone, index))` — **off the UI thread** |
| 8 | App | `MowingRoutePlanner.Plan` → `AdaptiveSpacing` (≥ 0.5 m, ≤ ~2500 waypoints) → the chosen strategy |
| 9 | Domain | `BoustrophedonStrategy`: project to ENU → scan-lines from `minNorth+spacing/2` upwards → `RowCrossings` per line → sort → **take crossings in pairs** (handles concave gardens) → alternate direction → densify each span → project every point back to GPS |
| 10 | VM | Sums `DistanceTo` over consecutive waypoints; fills `RoutePoints`; **`RouteVersion++`**; sets `RouteInfo = "412 waypoints, 187 m, Boustrophedon (parallel rows)"` |
| 11 | View | The `RouteVersion` property change triggers `UpdateRoute()` → an orange `LineString` layer |
| 12 | User | Presses **▶ Mow** |
| 13 | VM | `SendRouteToRobotAsync`: `IsSendingBoundary = true`, `Progress<int>` → `SendProgress` → the `ProgressBar`; `await _boundary.SendRouteAsync(RoutePoints)`; then `await _control.SendActionAsync(RobotAction.StartMowing)` |
| 14 | Infra | **BLE:** one 20-byte chunk per point, `pointType=1`, 50 ms apart, progress reported · **WiFi:** one `POST /commands` with `Kind:"Boundary"` and the whole array, progress jumps to 100 · **SPP:** `throw new NotSupportedException(...)` — the real firmware manages routes internally · **Simulator:** loads the waypoints and sets `_pathSaved` |
| 15 | Robot | `StartMowing` → jumps to waypoint 0, state → `Mowing`, blade on; each tick: if within 0.30 m of the target advance the index, else steer to the bearing and drive at 0.3 m/s |
| 16 | App | `StatusStream` reports `Mowing` → Dashboard turns green, `MowingHeadline` becomes "MOWING", the Control banner turns green |
| 17 | App | `RobotTrail` grows (> 0.3 m filter on the Map, > 0.5 m on the Control page) → a blue line traces where the robot has actually been, over the orange planned route |

*(Alternatively `SaveZoneLocallyCommand` persists the drawn polygon to SQLite so it appears in the zones panel, on the Schedule page's zone picker, and as a candidate for the geofence.)*

---

## FLOW 4 — A schedule fires automatically

| # | Where | What |
|---|---|---|
| 1 | User | Schedule page: taps *Mo*, *We*, *Fr*; picks a zone; sets 08:00; presses **Save** |
| 2 | VM | `SaveScheduleAsync`: validates ≥ 1 day; builds `MowingSchedule { ActiveDays, StartTime, DurationMinutes = 60, IsActive = true, ZoneName, ZoneId }`; `RunSafeAsync(() => _repo.SaveAsync(schedule))` |
| 3 | Infra | `IScheduleRepository` resolves to **`SyncingScheduleRepository`** → `ScheduleRepository.SaveAsync` writes to SQLite (`ActiveDays` → `"[1,3,5]"`, `StartTime` → ticks) |
| 4 | Infra | If sync is enabled: `_ = _sync.PushAsync(await _inner.GetAllAsync())` — fire-and-forget `POST /robots/{id}/schedules` with the **whole list** |
| 5 | Server | `SqliteScheduleStore.ReplaceAsync` in a transaction: UPSERT `Robots` (`Version = Version + 1`), delete all rows for the robot, insert each schedule as JSON, commit |
| 6 | Robot | Every 10th tick (~5 s) `GET /schedules`; the `Version` differs → logs `schedules synced from cloud: v1, 1 schedule(s)` and each schedule line |
| 7 | App | Meanwhile `MowingSchedulerService`'s timer ticks every 20 s |
| 8 | | `TickAsync`: `_ticking` guard → load all schedules → skip inactive → skip if today is not in `ActiveDays` |
| 9 | | `diff = now.TimeOfDay − StartTime`; continue only if `0 ≤ diff < 1 minute` |
| 10 | | Skip if `LastExecuted.Date == today && LastExecuted.TimeOfDay >= StartTime` (already ran) |
| 11 | | Skip with a warning if `!_connection.IsConnected` — **a scheduled mow is never queued for later** |
| 12 | App | `ZoneId.HasValue` → `SendZoneToRobotUseCase.ExecuteAsync(zoneId, startMowingAfter: true)`; otherwise `StartMowingOnlyAsync()` |
| 13 | App | The use case: load the zone → validate ≥ 3 points → `_planner.Plan(zone)` → send the **route** if non-empty, else the raw **boundary** → `SendActionAsync(StartMowing)` |
| 14 | App | `schedule.LastExecuted = now; await _repo.SaveAsync(schedule);` → also re-pushed to the cloud through the decorator |
| 15 | | The next tick 20 s later hits the same minute but is stopped by the `LastExecuted` check at step 10 |

---

## FLOW 5 — Connecting over WiFi and the first telemetry frame

| # | Where | What |
|---|---|---|
| 1 | User | Scan page → **WiFi** |
| 2 | VM | `SelectTransportAsync("Wifi")`: cancels any scan, `await _transport.SelectAsync(TransportKind.Wifi)`, clears the device list |
| 3 | Infra | `RobotTransportRouter.SelectAsync`: disconnects the old transport, `_active.OnNext(wifiService)`, `_kind.OnNext(Wifi)`. **Because the streams are `_active.Select(...).Switch()`, every existing ViewModel subscription now silently follows the new transport** |
| 4 | UI | `IsWifi` flips → button colours change, `ModeSubtitle` → "Reach a robot over WiFi through the cloud", `ScanButtonText` → "Find Robot Online" |
| 5 | User | Presses the scan button. No Bluetooth permission check runs (`if (IsBluetooth)`) |
| 6 | Infra | `WifiRobotService.StartScanAsync` walks `CandidateBaseUrls`; on Android `http://10.0.2.2:5080` first (the emulator alias for the host) |
| 7 | Wire | `GET /robots/active` + `Authorization: Bearer dev-token-…` |
| 8 | Server | `RobotTokenAuthHandler` → `FixedTimeEquals` → claim `robot_id=demo-robot-01` → `InMemoryTelemetryStore.GetActive()` returns robots seen in the last 5 s |
| 9 | Infra | For each: `_deviceSubject.OnNext(new MowerDevice { Id = MD5-Guid(robotId), Name = robotId })`. On any failure, a human-readable `RobotErrorMessage("WIFI/…")` |
| 10 | VM | `ScanViewModel` adds it to `DiscoveredDevices` (de-duplicated by `Id`) → a row appears |
| 11 | User | Presses **Connect** |
| 12 | Infra | `ConnectAsync`: `_robotId = device.Name`; state → `Connecting`; `GET /telemetry`; if `IsOnline != true` → error message + `Disconnected`; otherwise `_eventCursor = 0`, state → `Connected`, `RobotConnectedMessage`, and `PollLoop` starts |
| 13 | App | The `Connected` transition wakes three subscribers: `AppShell` sets `_wasConnected`; `GpsTraceService` opens a new CSV; `GeofenceMonitor` reloads zones and arms |
| 14 | VM | `ScanViewModel` navigates `//dashboard` |
| 15 | Infra | Poll loop every 500 ms: `GET /telemetry` → `PublishTelemetry` maps `TelemetryDto` → `SensorSnapshot` + `RobotStatus` → both subjects |
| 16 | Infra | `GET /events?since=N` → for each event, `_eventCursor = resp.Cursor` and `DispatchEvent` publishes the matching messenger message — **the same messages the Bluetooth transports publish**, which is why every screen works unchanged |
| 17 | App | Six failed polls in a row (~3 s) → `break` → `DisconnectAsync` → `AppShell` returns you to Scan with the alert |
