# 03 — ARCHITECTURE

*This is the chapter a professor will grill you on. Read it twice.*

---

## 1. In plain words

Imagine the app as **four rings**, like an onion:

* The **innermost ring (Domain)** is the pure idea of the problem: what a *garden zone* is, what a *GPS point* is, what *"start mowing"* means. It could be printed on paper and would still make sense. It knows nothing about phones, Bluetooth or databases.
* The **second ring (Application)** contains the *stories*: "connect to a mower", "save a schedule", "send this zone to the robot and start mowing", "watch the robot and stop it if it leaves the garden". It uses the Domain and it asks for services *by name* (interfaces) without knowing who provides them.
* The **third ring (Infrastructure)** contains the *machinery*: the Bluetooth code, the SQLite code, the HTTP code, the simulator. This ring *implements* the interfaces the inner rings asked for.
* The **outermost ring (Presentation)** is the *screens*: XAML pages and ViewModels.

The rule that makes this valuable is the **Dependency Rule**: *source code dependencies may only point inwards*. Infrastructure knows about Domain; Domain must never know about Infrastructure.

**Why does that matter?** Because the mower can be reached three completely different ways. If `ControlViewModel` contained `BluetoothSocket`, then adding WiFi would mean rewriting the UI. Instead `ControlViewModel` depends on `IRobotControl` — an interface with two methods — and every transport implements it. Adding a fourth transport tomorrow touches **zero** lines of UI code.

---

## 2. The layers as folders

```
MowIT/
├── Domain/          ← ring 1  (no using-statements pointing outward)
│   ├── Entities/        GpsPoint, LocalPoint, BoundaryZone, MowingSchedule,
│   │                    MowingRoute, MowerDevice, RobotStatus, SensorSnapshot
│   ├── Enums/           RobotState, RobotAction, GpsFixType, RobotConnectionState
│   ├── Geometry/        LocalProjection  (WGS-84 → ENU maths)
│   ├── Strategies/      BoustrophedonStrategy, SpiralInwardStrategy
│   ├── Builders/        BoundaryZoneBuilder
│   └── Interfaces/      IRobotScanner, IRobotConnection, IRobotSensors,
│                        IRobotControl, IRobotBoundary, IBoundaryRepository,
│                        IScheduleRepository, IScheduleSyncService,
│                        IMowingStrategy, IBlePermissionService, IRobotServiceFactory
├── Application/     ← ring 2
│   ├── UseCases/        ConnectToMowerUseCase, SaveScheduleUseCase,
│   │                    SendBoundaryUseCase, SendZoneToRobotUseCase
│   ├── Services/        GeofenceMonitor, GpsTraceService, MowingRoutePlanner, LastMowSession
│   ├── StateMachine/    RobotStateMachine
│   ├── Pipeline/        CommandHandler + 4 handlers + CommandPipeline + RobotCommandContext
│   ├── Messages/        BleMessages.cs (13 messenger message records)
│   ├── Logging/         EventLogService, EventLogEntry, EventLogLevel
│   └── DTOs/            BoundaryDto, MotorCommandDto
├── Infrastructure/  ← ring 3
│   ├── Ble/             GreenTitanBleService, BleGattProfile, BlePacketSerializer,
│   │                    RetryDecorator, ConnectionGuardProxy
│   ├── ClassicBt/       GreenTitanSppService, BtDiscoveryReceiver
│   ├── Wifi/            WifiRobotService, WifiRobotOptions
│   ├── Simulator/       SimulatedRobotService
│   ├── Transport/       IRobotTransport, RobotTransportRouter, TransportKind, …
│   ├── Factories/       SimulatorServiceFactory, GreenTitanServiceFactory,
│   │                    GreenTitanSppFactory, MultiTransportFactory
│   ├── Persistence/     AppDatabase, DatabaseEntities, BoundaryRepository, ScheduleRepository
│   ├── ScheduleSync/    HttpScheduleSyncService, NullScheduleSyncService,
│   │                    SyncingScheduleRepository, ScheduleSyncOptions
│   ├── Services/        MowingSchedulerService
│   └── (root)           NullRobotService, NullBlePermissionService
└── Presentation/    ← ring 4
    ├── Pages/           Login, Scan, Dashboard, Control, Map, Schedule (XAML + code-behind)
    ├── ViewModels/      one per page + BaseViewModel
    ├── Controls/        JoystickView (SkiaSharp), GpsStatusBadge
    └── Converters/      19 IValueConverter classes
```

**Proof that the dependency rule holds:** open any file in `Domain/` — the `using` statements only ever reference `MowIT.Domain.*` and the BCL (`System.*`). No `using SQLite;`, no `using Plugin.BLE;`, no `using Microsoft.Maui.*` for the model types.

*(Small honest caveat you should know: [`EventLogService`](MowIT/Application/Logging/EventLogService.cs) lives in `Application/` but uses MAUI types — `Color`, `MainThread`, `FileSystem`. It is a cross-cutting logging concern, and moving it to Infrastructure with an `IEventLog` interface in Domain would be the textbook-pure fix. Say this before the professor does; it shows you understand the rule rather than just following a folder template.)*

---

## 3. The five transport interfaces — the keystone of the design

Instead of one fat `IRobot` interface, the design uses the **Interface Segregation Principle**: five small interfaces, each with one responsibility.

| Interface | File | Members | Who consumes it |
|---|---|---|---|
| `IRobotScanner` | [`IRobotScanner.cs`](MowIT/Domain/Interfaces/IRobotScanner.cs) | `DiscoveredDevices` (IObservable), `StartScanAsync`, `StopScanAsync`, `IsScanning` | `ScanViewModel` |
| `IRobotConnection` | [`IRobotConnection.cs`](MowIT/Domain/Interfaces/IRobotConnection.cs) | `ConnectionState` (IObservable), `CurrentState`, `ConnectAsync`, `DisconnectAsync`, `IsConnected` | `ScanViewModel`, `DashboardViewModel`, `AppShell`, `GeofenceMonitor`, `GpsTraceService`, `MowingSchedulerService` |
| `IRobotSensors` | [`IRobotSensors.cs`](MowIT/Domain/Interfaces/IRobotSensors.cs) | `SensorStream`, `StatusStream` (IObservable), `LastSensor`, `LastStatus` | every ViewModel that shows telemetry, `GeofenceMonitor`, `RobotStateMachine` |
| `IRobotControl` | [`IRobotControl.cs`](MowIT/Domain/Interfaces/IRobotControl.cs) | `SendMotorCommandAsync(lin, ang)`, `SendActionAsync(action, param)` | `ControlViewModel`, `DashboardViewModel`, `MapViewModel`, use cases, `GeofenceMonitor` |
| `IRobotBoundary` | [`IRobotBoundary.cs`](MowIT/Domain/Interfaces/IRobotBoundary.cs) | `SendBoundaryAsync`, `SendRouteAsync`, `ClearBoundaryAsync` | `MapViewModel`, `ControlViewModel`, `SendBoundaryUseCase`, `SendZoneToRobotUseCase` |

Each concrete transport implements **all five** (and `IDisposable`):

```csharp
public sealed class GreenTitanSppService
    : IRobotScanner, IRobotConnection, IRobotSensors, IRobotControl, IRobotBoundary, IDisposable
```

`IRobotTransport` ([`Transport/IRobotTransport.cs`](MowIT/Infrastructure/Transport/IRobotTransport.cs)) is simply the union of the five, used only by the router:

```csharp
public interface IRobotTransport : IRobotScanner, IRobotConnection, IRobotSensors, IRobotControl, IRobotBoundary { }
```

> **Why not one big `IRobot`?** Because `ScanViewModel` has no business being able to call `SendMotorCommandAsync`. Segregated interfaces make the *capability* each class needs explicit in its constructor signature, and they make fakes trivial to write in tests.

---

## 4. The composition root — `MauiProgram.cs`

[`MauiProgram.CreateMauiApp()`](MowIT/MauiProgram.cs#L28) is the **only** place in the whole client where concrete types are chosen. Everything else asks for interfaces.

```csharp
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();

    builder.UseMauiApp<App>()
           .UseMauiCommunityToolkit()   // enables Toast, etc.
           .UseSkiaSharp()              // enables SKCanvasView
           .ConfigureFonts(f => { f.AddFont("OpenSans-Regular.ttf","OpenSansRegular");
                                  f.AddFont("OpenSans-Semibold.ttf","OpenSansSemibold");
                                  f.AddFont("Pixie.ttf","Pixie"); });
#if DEBUG
    builder.Logging.AddDebug();
#endif

    RegisterInfrastructure(builder.Services);
    RegisterApplication  (builder.Services);
    RegisterPresentation (builder.Services);

    var app = builder.Build();

    // force-create the three "invisible" background singletons
    app.Services.GetRequiredService<MowingSchedulerService>();
    app.Services.GetRequiredService<GeofenceMonitor>();
    app.Services.GetRequiredService<GpsTraceService>();

    return app;
}
```

### 4.1 The three `GetRequiredService` calls at the end — why?

Microsoft's DI container is **lazy**: a singleton is not constructed until somebody asks for it. `MowingSchedulerService`, `GeofenceMonitor` and `GpsTraceService` are never injected into any page — they do their work purely from their constructors (they start a timer / subscribe to `ConnectionState`). So the app has to *resolve them once* to bring them to life. Those three lines are the "start the background services" step.

### 4.2 `RegisterInfrastructure`

```csharp
RegisterPlatformPermissions(services);          // partial method — see §5

if (RobotFactory is SimulatorServiceFactory or MultiTransportFactory)
    services.AddSingleton<IBlePermissionService, NullBlePermissionService>();

var dbPath = Path.Combine(FileSystem.AppDataDirectory, "mowit.db3");
services.AddSingleton(new AppDatabase(dbPath));
services.AddSingleton<IBoundaryRepository, BoundaryRepository>();
services.AddSingleton<ScheduleRepository>();     // registered as the CONCRETE type…

if (RobotFactory is MultiTransportFactory) { …HttpScheduleSyncService… }
else services.AddSingleton<IScheduleSyncService, NullScheduleSyncService>();

services.AddSingleton<IScheduleRepository>(sp =>  // …so the decorator can wrap it
    new SyncingScheduleRepository(sp.GetRequiredService<ScheduleRepository>(),
                                  sp.GetRequiredService<IScheduleSyncService>()));

services.AddSingleton(new WifiRobotOptions());
services.AddSingleton<IRobotTransportSwitch, NullRobotTransportSwitch>();
services.AddSingleton(RobotFactory);
RobotFactory.RegisterServices(services);         // ← the factory does the transport wiring
```

**Three things to point out in a defence:**

1. **The decorator trick.** `ScheduleRepository` is registered *by its concrete type* and `IScheduleRepository` is registered as a lambda that wraps it in `SyncingScheduleRepository`. Anyone who asks for `IScheduleRepository` therefore transparently gets "save locally **and** push to the cloud". Neither the ViewModel nor the repository knows this happened.
2. **Last registration wins.** `IBlePermissionService` is registered by `RegisterPlatformPermissions` (per-platform) and *then* possibly overridden with `NullBlePermissionService` when running in simulator/multi-transport mode — because in those modes there is no real Bluetooth to ask permission for. `IRobotTransportSwitch` is registered as `NullRobotTransportSwitch` here and then *overwritten* by `MultiTransportFactory` with the real `RobotTransportRouter`. Microsoft DI resolves the **last** registration for a service type, which is what makes this override pattern work.
3. **Configuration flows from one place.** When `MultiTransportFactory` is active, `ScheduleSyncOptions` is built *from* `WifiRobotOptions`, so the cloud URL, robot id and token are defined once.

### 4.3 `RegisterApplication`

```csharp
services.AddTransient<SendBoundaryUseCase>();       // Transient: cheap, stateless
services.AddTransient<SaveScheduleUseCase>();
services.AddTransient<ConnectToMowerUseCase>();
services.AddTransient<SendZoneToRobotUseCase>();

services.AddSingleton<GeofenceMonitor>();           // Singleton: holds subscriptions & state
services.AddSingleton<GpsTraceService>();
services.AddSingleton<MowingSchedulerService>();
services.AddSingleton<IMessenger>(_ => WeakReferenceMessenger.Default);
services.AddSingleton<EventLogService>();
services.AddSingleton<LastMowSession>();

services.AddTransient<IMowingStrategy, BoustrophedonStrategy>();   // ← two registrations
services.AddTransient<IMowingStrategy, SpiralInwardStrategy>();    //    of the SAME interface
services.AddSingleton<MowingRoutePlanner>();
```

**The two `IMowingStrategy` registrations are deliberate.** In Microsoft DI, registering the same interface twice means that injecting `IEnumerable<IMowingStrategy>` gives you **both**. `MowingRoutePlanner`'s constructor is exactly `MowingRoutePlanner(IEnumerable<IMowingStrategy> strategies)`, so it receives the list and exposes it as `Strategies`. Adding a third mowing pattern = write one class + add one line here. Nothing else changes; the UI already selects by index.

### 4.4 `RegisterPresentation`

```csharp
services.AddTransient<LoginPage>();     services.AddTransient<LoginViewModel>();
services.AddTransient<ScanPage>();      services.AddTransient<ScanViewModel>();
services.AddSingleton<DashboardPage>(); services.AddSingleton<DashboardViewModel>();
services.AddSingleton<ControlPage>();   services.AddSingleton<ControlViewModel>();
services.AddSingleton<MapPage>();       services.AddSingleton<MapViewModel>();
services.AddSingleton<SchedulePage>();  services.AddSingleton<ScheduleViewModel>();
```

**Why the split?** Login and Scan are visited once and thrown away → `Transient`. The four tab pages are long-lived and must **keep their state** (the recorded polygon, the robot trail, the drawn boundary) while you switch tabs → `Singleton`. Also, the tab ViewModels subscribe to sensor streams in their constructors; making them transient would create a new subscription on every navigation and leak.

---

## 5. Per-platform code without `#if` everywhere: the partial method trick

At the bottom of `MauiProgram.cs`:

```csharp
static partial void RegisterPlatformPermissions(IServiceCollection services);
```

and in each platform folder, a file that MSBuild only compiles for that platform:

| File | Body |
|---|---|
| [`Platforms/Android/PlatformRegistration.cs`](MowIT/Platforms/Android/PlatformRegistration.cs) | `services.AddSingleton<IBlePermissionService, AndroidBlePermissionService>();` |
| [`Platforms/iOS/PlatformRegistration.cs`](MowIT/Platforms/iOS/PlatformRegistration.cs) | `…IosBlePermissionService` |
| [`Platforms/MacCatalyst/PlatformRegistration.cs`](MowIT/Platforms/MacCatalyst/PlatformRegistration.cs) | `…NullBlePermissionService` |
| [`Platforms/Windows/PlatformRegistration.cs`](MowIT/Platforms/Windows/PlatformRegistration.cs) | `…NullBlePermissionService` |

Because it is a **partial method with no implementation on some platform**, the compiler simply erases the call. This is the idiomatic MAUI way to do platform-specific DI, and it is much cleaner than a wall of `#if ANDROID … #elif IOS …`.

---

## 6. Reactive data flow (Rx) instead of polling

Every transport publishes through Rx `Subject<T>`s:

```csharp
private readonly Subject<MowerDevice>          _deviceSubject = new();
private readonly Subject<RobotConnectionState> _stateSubject  = new();
private readonly Subject<SensorSnapshot>       _sensorSubject = new();
private readonly Subject<RobotStatus>          _statusSubject = new();
```

Consumers *subscribe* and each applies its own throttling appropriate to its purpose:

| Consumer | Operator | Rate | Why |
|---|---|---|---|
| `ControlViewModel` sensors | `Sample(100 ms)` | 10 Hz | Smooth position for the joystick/map |
| `DashboardViewModel` sensors | `Sample(500 ms)` | 2 Hz | Numbers on a dashboard do not need 10 Hz |
| `DashboardViewModel` status | `Sample(1 s)` | 1 Hz | State/battery change slowly |
| `MapViewModel` sensors | `Sample(1 s)` | 1 Hz | Map pins + trail |
| `GeofenceMonitor` | `Sample(500 ms)` | 2 Hz | Safety check; polygon test is O(n) so do not run it at 10 Hz |
| `GpsTraceService` | *no throttle* | full rate | It is a recorder — you want every sample |
| `RobotStateMachine` | *no throttle* | full rate | Must never miss a state change |

This is a strong point to raise: **one producer, many consumers, each with independently chosen back-pressure** — trivial with Rx, painful with events.

### The `MainThread` discipline

Rx callbacks fire on whatever thread the Bluetooth/HTTP/timer code is on. UI properties must be set on the UI thread. Every subscription that touches the UI therefore wraps its body:

```csharp
.Subscribe(s => MainThread.BeginInvokeOnMainThread(() => { … }));
```

`EventLogService.Add` even checks `MainThread.IsMainThread` first to avoid an unnecessary marshal when already on the UI thread.

---

## 7. Decoupled notifications: `WeakReferenceMessenger`

Rx streams carry *continuous* data. One-off *events* ("base captured", "boundary point captured", "geofence breach", "robot error") use the CommunityToolkit **Messenger** — an in-process publish/subscribe bus.

```csharp
// publisher, inside a transport:
WeakReferenceMessenger.Default.Send(new BaseCapturedMessage());

// subscriber, inside a ViewModel constructor:
WeakReferenceMessenger.Default.Register<BaseCapturedMessage>(this, (_, _) =>
    MainThread.BeginInvokeOnMainThread(() => { HasDatum = true; … }));
```

**Why "weak"?** The messenger holds *weak references* to subscribers, so a page that is garbage-collected does not keep receiving messages and does not leak. This lets `DashboardViewModel` **and** `ControlViewModel` both react to `BaseCapturedMessage` without either knowing the other exists.

The 13 message types are all declared in one file, [`Application/Messages/BleMessages.cs`](MowIT/Application/Messages/BleMessages.cs).

---

## 8. The full runtime object graph (simulator configuration)

```
                       ┌──────────────────────────────┐
                       │    SimulatedRobotService     │  (singleton, implements all 5 + IRobotTransport)
                       └──┬────┬────┬──────────┬──────┘
        IRobotScanner ────┘    │    │          │
        IRobotConnection ──────┘    │          │
        IRobotSensors ──────────────┘          │
        IRobotBoundary ────────────────────────┘
                                    │
        IRobotControl resolves to:  │
        ┌───────────────────────────▼───────────────────────────┐
        │ CommandPipeline (Chain of Responsibility)              │
        │   LoggingCommandHandler → DispatchHandler              │
        │        DispatchHandler wraps ↓                          │
        │   ConnectionGuardProxy  (drop command if disconnected) │
        │        wraps ↓                                          │
        │   RetryDecorator        (3 attempts, 100 ms apart)      │
        │        wraps ↓                                          │
        │   SimulatedRobotService                                 │
        └─────────────────────────────────────────────────────────┘

  Consumers:  ControlViewModel · DashboardViewModel · MapViewModel · ScanViewModel ·
              GeofenceMonitor · GpsTraceService · MowingSchedulerService · AppShell
  Storage:    AppDatabase → BoundaryRepository / (SyncingScheduleRepository → ScheduleRepository)
```

Note that the **same physical object** (`SimulatedRobotService`) is registered four times under four different interfaces via `sp => sp.GetRequiredService<SimulatedRobotService>()`. That is the correct way to expose one singleton under several service types in Microsoft DI — writing `AddSingleton<IRobotScanner, SimulatedRobotService>()` four times would create **four separate instances** with four separate GPS positions. This is a subtle bug the code correctly avoids, and it is a great detail to mention.

---

## 9. SOLID scorecard (useful for the write-up)

| Principle | Where it shows up |
|---|---|
| **S**ingle Responsibility | `BlePacketSerializer` only serialises; `GeofenceMonitor` only watches the fence; `MowingRoutePlanner` only plans |
| **O**pen/Closed | Add a mowing pattern by adding a class + one DI line; add a transport by adding a class + one factory |
| **L**iskov Substitution | `NullRobotService` / `NullScheduleSyncService` / `NullRobotTransportSwitch` can replace the real thing anywhere without breaking callers |
| **I**nterface Segregation | The five small robot interfaces instead of one `IRobot` |
| **D**ependency Inversion | Every ViewModel and use case depends on Domain interfaces; concrete types appear only in `MauiProgram` and the factories |
