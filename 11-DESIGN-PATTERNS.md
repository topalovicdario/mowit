# 11 — DESIGN PATTERNS USED

A catalogue with the exact file for each one. Professors love this table; make sure you can explain **why** each pattern was chosen, not just that it is present.

---

## Quick index

| # | Pattern | Category | Where |
|---|---|---|---|
| 1 | Abstract Factory | Creational | `Infrastructure/Factories/*` |
| 2 | Builder | Creational | `Domain/Builders/BoundaryZoneBuilder.cs` |
| 3 | Singleton (via DI) | Creational | `MauiProgram.cs` |
| 4 | Adapter | Structural | the four transport services |
| 5 | Decorator | Structural | `RetryDecorator`, `SyncingScheduleRepository` |
| 6 | Proxy | Structural | `ConnectionGuardProxy` |
| 7 | Facade | Structural | `MowingRoutePlanner`, use cases |
| 8 | Composite-ish routing | Structural | `RobotTransportRouter` |
| 9 | Strategy | Behavioural | `IMowingStrategy` + 2 implementations |
| 10 | Chain of Responsibility | Behavioural | `Application/Pipeline/*` |
| 11 | Observer | Behavioural | Rx `Subject`/`IObservable` everywhere |
| 12 | Publish/Subscribe (Mediator) | Behavioural | `WeakReferenceMessenger` |
| 13 | State Machine | Behavioural | `RobotStateMachine` |
| 14 | Command | Behavioural | `RobotCommandContext`, `RobotCommandDto`, `IRelayCommand` |
| 15 | Template Method | Behavioural | `CommandHandler.HandleAsync`, `BaseViewModel` |
| 16 | Null Object | Behavioural | four `Null*` classes |
| 17 | Repository | Architectural | `IBoundaryRepository`, `IScheduleRepository` |
| 18 | Unit of Work (lite) | Architectural | `AppDatabase` |
| 19 | MVVM | Architectural | the whole Presentation layer |
| 20 | Dependency Injection / IoC | Architectural | `MauiProgram`, both server and app |
| 21 | Clean / Onion Architecture | Architectural | the four folders |
| 22 | DTO | Architectural | `MowIT.Shared` |
| 23 | Options pattern | Architectural | `WifiRobotOptions`, `ScheduleSyncOptions`, `RobotTokenOptions` |
| 24 | Producer/Consumer queue | Concurrency | `InMemoryCommandQueue` |
| 25 | Cursor / offset delivery | Distributed | `RobotEventDto.Seq` + `?since=` |

---

## Creational

### 1. Abstract Factory — `IRobotServiceFactory`

```csharp
public interface IRobotServiceFactory {
    string RobotName { get; }
    void RegisterServices(IServiceCollection services);
}
```
Four concrete factories: [`SimulatorServiceFactory`](MowIT/Infrastructure/Factories/SimulatorServiceFactory.cs), [`GreenTitanServiceFactory`](MowIT/Infrastructure/Factories/GreenTitanServiceFactory.cs) (BLE), [`GreenTitanSppFactory`](MowIT/Infrastructure/Factories/GreenTitanSppFactory.cs) (Classic BT), [`MultiTransportFactory`](MowIT/Infrastructure/Factories/MultiTransportFactory.cs).

**Why:** each factory produces a whole *family* of related objects (scanner + connection + sensors + control + boundary, plus the decorator stack around control). Choosing a different transport is one line in [`MauiProgram.cs:25`](MowIT/MauiProgram.cs#L25). Not one object — a coherent set. That is precisely Abstract Factory rather than plain Factory Method.

### 2. Builder — `BoundaryZoneBuilder`

Fluent, and `Build()` enforces the ≥ 3-point invariant. [file](MowIT/Domain/Builders/BoundaryZoneBuilder.cs). *(Honest note: implemented but not currently called — see [`04-DOMAIN-LAYER.md §5`](04-DOMAIN-LAYER.md).)*

### 3. Singleton — through the container, not through a static

The app never writes `private static readonly X Instance = new X()`. Instead `services.AddSingleton<T>()` gives one instance with a container-managed lifetime — **testable and replaceable**, unlike a classic static singleton. The one subtlety, worth knowing: registering the same object under several interfaces uses
```csharp
services.AddSingleton<IRobotScanner>(sp => sp.GetRequiredService<SimulatedRobotService>());
```
so all five interfaces resolve to *one* object rather than five.

---

## Structural

### 4. Adapter

Each transport adapts a foreign API to the project's own five interfaces:

| Adapter | Adapts |
|---|---|
| `GreenTitanSppService` | Android `BluetoothSocket` / WinRT `StreamSocket` + an ASCII protocol |
| `GreenTitanBleService` | `Plugin.BLE`'s `IAdapter`/`ICharacteristic` + binary packets |
| `WifiRobotService` | `HttpClient` + JSON |
| `SimulatedRobotService` | an in-process physics model |

**The value:** four utterly different technologies, one interface, zero conditional code above the adapter.

### 5. Decorator (two instances)

```csharp
// behaviour added: retry
public class RetryDecorator : IRobotControl {
    public async Task SendActionAsync(RobotAction a, byte p = 0) {
        for (int i = 0; i < 3; i++) { try { await _inner.SendActionAsync(a,p); return; }
                                      catch when (i < 2) { await Task.Delay(100); } } }
    public Task SendMotorCommandAsync(float l, float a) => _inner.SendMotorCommandAsync(l,a);  // no retry
}

// behaviour added: cloud sync
public sealed class SyncingScheduleRepository : IScheduleRepository {
    public async Task SaveAsync(MowingSchedule s) {
        await _inner.SaveAsync(s);
        if (_sync.IsEnabled) _ = _sync.PushAsync(await _inner.GetAllAsync());
    }
}
```

`SyncingScheduleRepository` is the better example to present: **the entire cloud-sync feature was added without modifying `ScheduleRepository` or any ViewModel** — the Open/Closed Principle in action, visible in a single class.

### 6. Proxy — `ConnectionGuardProxy`

```csharp
public Task SendActionAsync(RobotAction a, byte p = 0) {
    if (!_connection.IsConnected) return Task.CompletedTask;   // access control
    return _inner.SendActionAsync(a, p);
}
```
Same interface, **controls access** rather than adding behaviour — that is the distinction from Decorator, and it is a classic exam question. [file](MowIT/Infrastructure/Ble/ConnectionGuardProxy.cs)

### 7. Facade

* `MowingRoutePlanner` hides strategy selection, index wrapping and adaptive-spacing computation behind `Plan(zone)`.
* Each use case hides load → validate → plan → send → start behind one `ExecuteAsync`.

### 8. Routing composite — `RobotTransportRouter`

Implements all five interfaces and forwards to whichever transport is active, including the stream-of-streams flattening:
```csharp
public IObservable<SensorSnapshot> SensorStream => _active.Select(t => t.SensorStream).Switch();
```
[file](MowIT/Infrastructure/Transport/RobotTransportRouter.cs) — explained in detail in [`06-INFRASTRUCTURE-LAYER.md §6`](06-INFRASTRUCTURE-LAYER.md).

---

## Behavioural

### 9. Strategy — the textbook example

```csharp
public interface IMowingStrategy { string Name { get; } List<GpsPoint> GenerateRoute(BoundaryZone z, float spacing); }
```
`BoustrophedonStrategy` (parallel rows) and `SpiralInwardStrategy` (concentric rings). Selected at **runtime** by the user pressing a button; registered so that DI hands the planner `IEnumerable<IMowingStrategy>`. **Adding a third pattern = one new class + one DI line + nothing else.** Say that sentence in the defence; it is the clearest demonstration of Open/Closed in the project.

### 10. Chain of Responsibility — the command pipeline

`CommandHandler` (abstract) → `LoggingCommandHandler` → `DispatchHandler`, with `BatteryCheckHandler` and `StateGuardHandler` implemented and available. Any link can call `ctx.Abort(reason)` and the chain stops. See [`05-APPLICATION-LAYER.md §4`](05-APPLICATION-LAYER.md) — **including the honest note that the two guard handlers are not currently wired in**.

### 11. Observer — via Rx

`Subject<T>` (the subject/observable) and `.Subscribe(...)` (the observers) everywhere: connection state, sensor stream, status stream, discovered devices. The improvement over classic `event` is that Rx gives you composable operators — `Sample`, `Switch`, `DistinctUntilChanged`, `Concat`, `SelectMany` — so each observer picks its own rate and lifetime without the producer knowing.

`ObservableCollection<T>` in the ViewModels is the same pattern applied to collections (`INotifyCollectionChanged`), and `[ObservableProperty]`/`INotifyPropertyChanged` is Observer applied to single values.

### 12. Publish/Subscribe (Mediator) — `WeakReferenceMessenger`

13 message records; publishers are transports and services, subscribers are ViewModels. Neither side knows the other. Weak references mean a discarded page cannot leak or keep handling messages. [file](MowIT/Application/Messages/BleMessages.cs)

### 13. State Machine — `RobotStateMachine`

A `HashSet<(RobotState, RobotAction)>` transition table plus an always-allowed set, mirroring the robot's reported state from `StatusStream`. [file](MowIT/Application/StateMachine/RobotStateMachine.cs)

### 14. Command — three variants in one project

* **`RobotCommandContext`** — a command object travelling through the chain, carrying its own abort state.
* **`RobotCommandDto`** — a *serialisable* command sent over HTTP and queued on the server. Command-as-message.
* **`[RelayCommand]` / `IRelayCommand`** — UI commands with `CanExecute`, bound to buttons.

Being able to say "the Command pattern appears here at three different levels — in-process, over the wire, and in the UI" is a strong observation.

### 15. Template Method

```csharp
public async Task HandleAsync(RobotCommandContext ctx) {   // ← the invariant algorithm
    if (ctx.IsAborted) return;
    await ProcessAsync(ctx);                                // ← the varying step, supplied by subclasses
    if (!ctx.IsAborted && _next is not null) await _next.HandleAsync(ctx);
}
protected abstract Task ProcessAsync(RobotCommandContext ctx);
```
Also in `BaseViewModel.RunSafeAsync(Func<Task> action, …)` — the busy/error/thread-marshalling skeleton is fixed; the body varies.

### 16. Null Object

`NullRobotService`, `NullBlePermissionService`, `NullScheduleSyncService`, `NullRobotTransportSwitch`. **The payoff:** no consumer ever writes `if (_sync != null)`. `SyncingScheduleRepository` always has a sync service; whether it does anything is a DI decision.

---

## Architectural

### 17. Repository

`IBoundaryRepository` and `IScheduleRepository` live in **Domain**; implementations live in Infrastructure. The domain talks about "get all zones", never about SQL. Swapping SQLite for a REST API would not touch a single ViewModel.

### 18. Unit of Work (lightweight) — `AppDatabase`

Owns the single `SQLiteAsyncConnection` shared by both repositories, plus thread-safe one-time schema initialisation. Not a full UoW (no change tracking, no atomic multi-repository commit), so describe it accurately as "a shared connection/initialisation owner" rather than claiming a full UoW.

### 19. MVVM

Views (XAML) ↔ ViewModels (`ObservableObject`) ↔ Model (Domain/Application). See [`07-PRESENTATION-LAYER.md`](07-PRESENTATION-LAYER.md).

### 20. Dependency Injection / IoC

Constructor injection everywhere; one composition root per program (`MauiProgram.CreateMauiApp`, `WebApplication.CreateBuilder`). Lifetimes chosen deliberately: `Transient` for stateless use cases and short-lived pages, `Singleton` for stateful services and tab pages.

*(One honest exception: `AppShell` uses `IPlatformApplication.Current?.Services.GetService<…>()` — a Service Locator, because Shell is not constructed by the container.)*

### 21. Clean / Onion Architecture

Four rings, dependencies pointing inwards only. See [`03-ARCHITECTURE.md`](03-ARCHITECTURE.md).

### 22. DTO

`MowIT.Shared` — records with no behaviour, no dependencies, shared by three programs so the wire contract is enforced by the compiler.

### 23. Options pattern

`WifiRobotOptions`, `ScheduleSyncOptions`, `RobotTokenOptions` — strongly-typed configuration objects with `init`-only properties. The server binds `RobotTokenOptions` from configuration with `builder.Services.Configure<RobotTokenOptions>(config.GetSection("RobotTokens"))` and consumes it via `IOptions<T>`; `ScheduleSyncOptions` is derived from `WifiRobotOptions` so URL/id/token are defined once.

### 24. Producer/Consumer with coalescing — `InMemoryCommandQueue`

The app produces at 10 Hz, the robot consumes at 2 Hz, and **stale motor commands are dropped on enqueue** because only the newest velocity is meaningful. Actions are never dropped. A small but genuinely clever piece of design — see [`08-SERVER-SIDE.md §5.4`](08-SERVER-SIDE.md).

### 25. Cursor-based delivery — `Seq` + `?since=`

The server assigns a monotonic sequence number to every event; the client tracks the highest it has seen. Gives no-loss, no-duplicate delivery over a stateless polling transport — the same mechanism as a Kafka offset or a DB change feed. See [`08-SERVER-SIDE.md §5.3`](08-SERVER-SIDE.md).

---

## Patterns deliberately **not** used (good answers to "why didn't you…")

| Not used | Why |
|---|---|
| **Entity Framework Core** | Two tables and four queries on the server; `sqlite-net` + Dapper are ~10 MB lighter and need no migration tooling |
| **SignalR / WebSockets** | A home robot has no public address, so it must poll outward anyway; polling + a coalescing queue + an event cursor solves it with no persistent-connection infrastructure |
| **MediatR** | The four use cases are injected directly; adding a mediator library for four handlers would be ceremony without benefit |
| **AutoMapper** | Mappings are explicit static methods (`ToModel`/`ToEntity`/`ToDto`) — compile-time-checked and debuggable, unlike reflection-based mapping |
| **A full DDD model (aggregates, value objects, domain events)** | Overkill for this domain size; the useful parts (rich entities with behaviour, repositories, an anti-corruption boundary at the transports) are present |
| **A visitor over the polygon types** | There is one geometry type; a visitor would add indirection with no second implementation to justify it |
