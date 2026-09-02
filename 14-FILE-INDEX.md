# 14 — COMPLETE FILE INDEX

Every file in the repository (excluding `bin/`, `obj/`, `.vs/`, `.git/`) with a one-line purpose. Use this to answer "what is this file for?" instantly.

---

## Root

| File | Purpose |
|---|---|
| `mowit.sln` | Visual Studio solution: the 4 projects and their build configurations |
| `global.json` | Pins the .NET SDK to 8.0.405 (roll-forward to the latest patch) |
| `.gitignore` | Excludes `bin/`, `obj/`, `.vs/`, `*.user`, `.vscode/`, `.claude/`, `*.log`, `*.db`, `*.db3` |
| `00-…` – `15-…` `.md` | This documentation set |
| `logs/` | Session logs + GPS traces pulled off the device (not source; `*.log` is gitignored, `*.csv` is not) |

---

## `MowIT/` — the MAUI client

### Root of the project

| File | Purpose |
|---|---|
| `MowIT.csproj` | 4 target frameworks, 12 NuGet packages, MAUI resource declarations |
| `MowIT.csproj.user` | Local Visual Studio state (active debug target = Pixel 7 emulator). Git-ignored |
| `MauiProgram.cs` | **The composition root.** All DI registration; picks the transport factory (line 25) |
| `App.xaml` | Merges Colors/Styles dictionaries; registers all 19 value converters as `StaticResource` |
| `App.xaml.cs` | Creates `AppShell`; installs global unhandled-exception handlers |
| `AppShell.xaml` | Navigation: `login` and `scan` routes plus a 4-tab `TabBar` |
| `AppShell.xaml.cs` | Global disconnect handling: navigate to `//scan` on any drop; alert only if it was not deliberate |
| `Properties/launchSettings.json` | Windows debug profile |

### `Domain/` — ring 1

| File | Purpose |
|---|---|
| `Entities/GpsPoint.cs` | Immutable lat/lon value type; haversine `DistanceTo`, great-circle `BearingTo` |
| `Entities/LocalPoint.cs` | Position in centimetres east/north of the datum |
| `Entities/BoundaryZone.cs` | Polygon entity: `Contains` (ray casting), `DistanceToBoundaryMeters`, `AreaSquareMeters` (shoelace) |
| `Entities/MowerDevice.cs` | A discovered device: Guid id, name, RSSI |
| `Entities/MowingRoute.cs` | A named list of waypoints with a generation timestamp |
| `Entities/MowingSchedule.cs` | Days + time + duration + optional zone; `ICloneable`; display labels |
| `Entities/RobotStatus.cs` | Immutable status snapshot: state, battery, blade, rain, uptime |
| `Entities/SensorSnapshot.cs` | Immutable telemetry snapshot: GPS, accuracy, fix, IMU, odometry, mode flags |
| `Enums/RobotState.cs` | `Idle`…`RecordingBoundary`, `Error=255`; byte-backed (wire format) |
| `Enums/RobotAction.cs` | 17 commands with explicit byte codes `0x01`–`0x11` |
| `Enums/GpsFixType.cs` | `NoFix`/`Standard`/`RtkFloat`/`RtkFixed`, ordered by quality |
| `Enums/BleConnectionState.cs` | `RobotConnectionState`: Disconnected/Scanning/Connecting/Connected |
| `Enums/mowit.code-workspace` | **Stray VS Code workspace file in the wrong folder** — should be deleted |
| `Geometry/LocalProjection.cs` | WGS-84 geodetic ↔ ECEF ↔ local ENU metres; Bowring's inverse |
| `Strategies/BoustrophedonStrategy.cs` | Scan-line parallel-row mowing pattern; handles concave polygons via crossing pairs |
| `Strategies/SpiralInwardStrategy.cs` | Concentric rings by radial shrink toward the centroid |
| `Builders/BoundaryZoneBuilder.cs` | Fluent builder with a ≥ 3-point invariant. *Currently unused* |
| `Interfaces/IRobotScanner.cs` | Discover mowers |
| `Interfaces/IRobotConnection.cs` | Connect/disconnect + connection-state stream |
| `Interfaces/IRobotSensors.cs` | Sensor and status streams + last values |
| `Interfaces/IRobotControl.cs` | Send motor and action commands |
| `Interfaces/IRobotBoundary.cs` | Upload boundaries/routes, clear |
| `Interfaces/IBoundaryRepository.cs` | CRUD for zones |
| `Interfaces/IScheduleRepository.cs` | CRUD for schedules |
| `Interfaces/IScheduleSyncService.cs` | Push/delete schedules to the cloud; `IsEnabled` |
| `Interfaces/IMowingStrategy.cs` | Generate a route inside a zone |
| `Interfaces/IBlePermissionService.cs` | Request Bluetooth permission / check the radio |
| `Interfaces/IRobotServiceFactory.cs` | Register a whole transport stack into DI |

### `Application/` — ring 2

| File | Purpose |
|---|---|
| `UseCases/ConnectToMowerUseCase.cs` | Connect (a seam for future connection policy). *Not currently injected* |
| `UseCases/SaveScheduleUseCase.cs` | Validate (≥ 1 day, duration > 0) then persist |
| `UseCases/SendBoundaryUseCase.cs` | Load zone → validate → upload with progress |
| `UseCases/SendZoneToRobotUseCase.cs` | Plan a route; send route or fall back to outline; optionally start mowing |
| `Services/GeofenceMonitor.cs` | **Safety:** watches position, stops the robot on exit, logs distance past + accuracy |
| `Services/GpsTraceService.cs` | Records every GPS sample of a session to a CSV |
| `Services/MowingRoutePlanner.cs` | Strategy selection + `AdaptiveSpacing` (≥ 0.5 m, ≤ 2500 waypoints) |
| `Services/LastMowSession.cs` | Stores/reads the last mow time in preferences (UTC ticks → local) |
| `StateMachine/RobotStateMachine.cs` | Valid state→action transition table; mirrors the robot's reported state |
| `Pipeline/CommandHandler.cs` | Abstract chain link (Template Method + Chain of Responsibility) |
| `Pipeline/CommandPipeline.cs` | Builds and runs the chain; implements `IRobotControl` |
| `Pipeline/RobotCommandContext.cs` | The travelling command object with abort state |
| `Pipeline/Handlers/LoggingCommandHandler.cs` | Logs every command. **Wired in** |
| `Pipeline/Handlers/DispatchHandler.cs` | Terminal link: calls the inner control. **Wired in** |
| `Pipeline/Handlers/BatteryCheckHandler.cs` | Blocks actions below 5 % battery. **Not wired in** |
| `Pipeline/Handlers/StateGuardHandler.cs` | Blocks actions invalid for the current state. **Not wired in** |
| `Messages/BleMessages.cs` | 13 messenger message records (connected, base captured, breach, …) |
| `Logging/EventLogService.cs` | The black box: in-memory list + session file + `ILogger`, 6 levels, bounded at 2000 |
| `DTOs/BoundaryDto.cs` | Zone summary record. *Unused* |
| `DTOs/MotorCommandDto.cs` | Velocity pair with `Clamped()` limits (±0.5 m/s, ±1.0 rad/s). *Unused* |

### `Infrastructure/` — ring 3

| File | Purpose |
|---|---|
| `NullRobotService.cs` | Null Object implementing all 5 robot interfaces. *Unused; ideal test-fake base* |
| `NullBlePermissionService.cs` | Always grants permission (Windows/Mac/simulator) |
| **Ble/** | |
| `Ble/BleGattProfile.cs` | The 9 GATT UUIDs (service + 4 notify + 4 write) |
| `Ble/BlePacketSerializer.cs` | Binary (de)serialisation of every BLE packet; day-mask packing |
| `Ble/GreenTitanBleService.cs` | BLE transport: scan, connect, MTU 512, notifications, chunked uploads |
| `Ble/ConnectionGuardProxy.cs` | Proxy: drops commands when disconnected |
| `Ble/RetryDecorator.cs` | Decorator: 3 retries for actions, none for motor commands |
| **ClassicBt/** | |
| `ClassicBt/GreenTitanSppService.cs` | Classic BT SPP transport (Android + Windows): ASCII protocol, stream framing, latency stats, manual-mode recovery |
| `ClassicBt/BtDiscoveryReceiver.cs` | Android `BroadcastReceiver` for `ACTION_FOUND` during discovery |
| **Wifi/** | |
| `Wifi/WifiRobotService.cs` | HTTP transport: URL probing, telemetry polling, event cursor, command POSTs |
| `Wifi/WifiRobotOptions.cs` | Robot id, token, poll interval, failure limit, platform-specific candidate URLs |
| **Simulator/** | |
| `Simulator/SimulatedRobotService.cs` | Full in-process robot: physics, accuracy ramp, watchdog, capture state machine, protocol echo |
| **Transport/** | |
| `Transport/IRobotTransport.cs` | Union of the 5 robot interfaces |
| `Transport/TransportKind.cs` | `Bluetooth` / `Wifi` |
| `Transport/IRobotTransportSwitch.cs` | Current kind + change stream + `SelectAsync` |
| `Transport/RobotTransportRouter.cs` | Forwards all 5 interfaces to the active transport; `Switch()`-based stream hot-swap |
| `Transport/NullRobotTransportSwitch.cs` | Null Object for single-transport configurations |
| **Factories/** | |
| `Factories/SimulatorServiceFactory.cs` | Registers the simulator stack only |
| `Factories/GreenTitanServiceFactory.cs` | Registers the BLE stack |
| `Factories/GreenTitanSppFactory.cs` | Registers the Classic BT stack |
| `Factories/MultiTransportFactory.cs` | Registers simulator + WiFi + router; enables cloud schedule sync. **Currently active** |
| **Persistence/** | |
| `Persistence/AppDatabase.cs` | Owns the SQLite connection; header validation, thread-safe init, migration |
| `Persistence/DatabaseEntities.cs` | `sqlite-net` table classes for zones, points and schedules |
| `Persistence/BoundaryRepository.cs` | Zone CRUD with ordered-point mapping (delete-then-insert) |
| `Persistence/ScheduleRepository.cs` | Schedule CRUD with JSON day arrays and tick-based times |
| **ScheduleSync/** | |
| `ScheduleSync/HttpScheduleSyncService.cs` | POST/DELETE schedules to the server; logs to the event log |
| `ScheduleSync/NullScheduleSyncService.cs` | Null Object when the cloud is not configured |
| `ScheduleSync/ScheduleSyncOptions.cs` | BaseUrl / RobotId / Token / Timeout + `IsConfigured` |
| `ScheduleSync/SyncingScheduleRepository.cs` | Decorator: save locally then push the whole list to the cloud |
| **Services/** | |
| `Services/MowingSchedulerService.cs` | 20 s timer that fires due schedules; 1-minute window, once-per-day guard |

### `Presentation/` — ring 4

| File | Purpose |
|---|---|
| `ViewModels/Base/BaseViewModel.cs` | `IsBusy`/`Title`/`ErrorMessage`, `RunSafeAsync`, lifecycle hooks |
| `ViewModels/LoginViewModel.cs` | Profile name + remember-me; navigates to `//scan` |
| `ViewModels/ScanViewModel.cs` | Transport toggle, permissions, scanning, device list, connect; rebuilds its subscriptions on every appearance so reconnecting works |
| `ViewModels/DashboardViewModel.cs` | Telemetry → display strings/colours; readiness text; Mow/Stop/Disconnect; 6 message handlers |
| `ViewModels/ControlViewModel.cs` | Joystick Rx pipeline, base capture, boundary recording, plan mode, walked-zone persistence |
| `ViewModels/MapViewModel.cs` | Draw/load/delete zones, route calculation, uploads, robot trail; re-arms the geofence on every zone save/delete |
| `ViewModels/ScheduleViewModel.cs` | Day toggles, zone picker, save/delete/toggle, "mow now" with progress |
| `Pages/LoginPage.xaml(.cs)` | Gradient hero screen with name entry |
| `Pages/ScanPage.xaml(.cs)` | Transport buttons, scan control, device `CollectionView`, status footer |
| `Pages/DashboardPage.xaml(.cs)` | State card + 8 stat tiles; `OnIdiom` responsive layout |
| `Pages/ControlPage.xaml` | GPS/datum card, capture cards, Skia map, joystick dock |
| `Pages/ControlPage.xaml.cs` | The SkiaSharp local-ENU renderer: bounds, grid, polygons, mower, scale bar, 30 fps smoothing |
| `Pages/MapPage.xaml` | Mapsui map + floating control/zones/boundary cards |
| `Pages/MapPage.xaml.cs` | 5 Mapsui layers, Mercator conversion, tap-to-draw, layer refresh |
| `Pages/SchedulePage.xaml(.cs)` | Creation card + schedule list with switches and actions |
| `Controls/JoystickView.cs` | Custom `SKCanvasView` joystick: bindable colours, radial clamp, normalised output |
| `Controls/GpsStatusBadge.xaml(.cs)` | Reusable fix-quality badge. *Currently unused* |
| `Converters/Converters.cs` | 19 `IValueConverter`s (percent→fraction, bool→colour/label/opacity, …) |

### `Platforms/`

| File | Purpose |
|---|---|
| `Android/MainActivity.cs` | Android entry activity; forwards permission results |
| `Android/MainApplication.cs` | Android `Application` that builds the MAUI app |
| `Android/AndroidBlePermissionService.cs` | Runtime permission requests (API 31+ vs legacy) and radio check |
| `Android/PlatformRegistration.cs` | Partial method: registers the Android permission service |
| `Android/AndroidManifest.xml` | Permissions, features, cleartext traffic |
| `Android/Resources/values/colors.xml` | Android theme colours |
| `iOS/AppDelegate.cs`, `iOS/Program.cs` | iOS entry points |
| `iOS/IosBlePermissionService.cs` | Stub (iOS prompts automatically on first BLE use) |
| `iOS/PlatformRegistration.cs` | Registers the iOS permission service |
| `iOS/Info.plist` | Bundle config + Bluetooth usage descriptions |
| `iOS/Resources/PrivacyInfo.xcprivacy` | Apple privacy manifest |
| `MacCatalyst/AppDelegate.cs`, `Program.cs`, `Info.plist`, `Entitlements.plist` | MacCatalyst entry + entitlements |
| `MacCatalyst/PlatformRegistration.cs` | Registers the null permission service |
| `Windows/App.xaml(.cs)`, `Package.appxmanifest`, `app.manifest` | WinUI entry and packaging |
| `Windows/PlatformRegistration.cs` | Registers the null permission service |
| `Tizen/Main.cs`, `tizen-manifest.xml` | Tizen scaffolding (not an active target) |

### `Resources/`

| File | Purpose |
|---|---|
| `Styles/Colors.xaml` | Earth palette + MAUI palette + light/dark surface colours |
| `Styles/Styles.xaml` | Named styles: cards, stat tiles, buttons, inputs, banners, labels |
| `Fonts/OpenSans-Regular.ttf`, `OpenSans-Semibold.ttf`, `Pixie.ttf` | Registered fonts |
| `Images/tab_dashboard.svg`, `tab_control.svg`, `tab_map.svg`, `tab_schedule.svg` | Tab icons |
| `Images/lawn.png`, `dotnet_bot.png` | Artwork |
| `AppIcon/appicon.svg`, `appiconfg.svg` | App icon (background + foreground) |
| `Splash/splash.svg` | Splash screen |
| `Raw/AboutAssets.txt` | Template note about raw assets |

---

## `MowIT.Shared/` — the contract library

| File | Purpose |
|---|---|
| `MowIT.Shared.csproj` | Plain `net8.0`, zero packages |
| `Telemetry/TelemetryDto.cs` | Full robot state for the wire (enums as `int`) |
| `Telemetry/TelemetryEnvelope.cs` | Telemetry + `IsOnline` + `LastSeenUtc`; `ActiveRobotsResponse` |
| `Telemetry/RobotCommandDto.cs` | Motor/Action/Boundary command; `CommandKinds`; `BoundaryUploadDto`; `GpsPointDto` |
| `Telemetry/RobotEventDto.cs` | Robot event with server-assigned `Seq`; `RobotEventTypes`; `EventListResponse` |
| `Schedules/ScheduleDto.cs` | Serialisable schedule (days as `int[]`, time as ticks) |
| `Schedules/ScheduleListResponse.cs` | List + version + updated; also `ScheduleVersionResponse`, `ScheduleUploadRequest` |

---

## `MowIT.ScheduleServer/` — the backend

| File | Purpose |
|---|---|
| `MowIT.ScheduleServer.csproj` | Web SDK; Dapper, Microsoft.Data.Sqlite, Swashbuckle |
| `Program.cs` | DI, auth scheme, Swagger, and all 12 minimal-API endpoints |
| `Auth/RobotTokenStore.cs` | Token→robotId map from configuration; timing-safe comparison |
| `Auth/RobotTokenAuthHandler.cs` | Custom Bearer authentication scheme producing a `robot_id` claim |
| `Storage/ScheduleStore.cs` | SQLite + Dapper: `Robots`/`Schedules` tables, transactional versioned replace |
| `Storage/TelemetryStore.cs` | Latest telemetry per robot; 5-second online window |
| `Storage/CommandQueue.cs` | Per-robot queue with motor-command coalescing and destructive drain |
| `Storage/RobotEventLog.cs` | Per-robot 200-entry ring with monotonic `Seq` and cursor queries |
| `appsettings.json` | Production defaults: `schedules.db`, no tokens |
| `appsettings.Development.json` | Dev: `schedules-dev.db`, `demo-robot-01` + its token |
| `Properties/launchSettings.json` | Listens on `http://0.0.0.0:5080` in Development |
| `schedules-dev.db` | Local dev database (git-ignored) |

---

## `MowIT.RobotSimulator/` — the virtual robot

| File | Purpose |
|---|---|
| `MowIT.RobotSimulator.csproj` | Console app, `net8.0`, copies `appsettings.json` to output |
| `Program.cs` | Layered configuration + the 2 Hz loop: tick → post telemetry → drain commands → post events → sync schedules |
| `VirtualRobot.cs` | Pure simulation: physics, accuracy ramp, watchdog, capture state machine, route following |
| `appsettings.json` | BaseUrl, RobotId, Token, TickHz |

---

## Files created at runtime (not in source control)

| Path | Created by |
|---|---|
| `<AppData>/mowit.db3` | `AppDatabase` |
| `<AppData>/logs/session_*.log` | `EventLogService` |
| `<AppData>/logs/gpstrace_*.csv` | `GpsTraceService` |
| `MowIT.ScheduleServer/schedules-dev.db` | `SqliteScheduleStore` |
