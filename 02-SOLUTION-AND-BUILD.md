# 02 — SOLUTION, PROJECTS, DEPENDENCIES AND HOW TO RUN IT

---

## 1. The solution file

[`mowit.sln`](mowit.sln) declares four projects:

| GUID (short) | Project | Path |
|---|---|---|
| `A8B261AF…` | **MowIT** | [`MowIT/MowIT.csproj`](MowIT/MowIT.csproj) |
| `DC0F62ED…` | **MowIT.Shared** | [`MowIT.Shared/MowIT.Shared.csproj`](MowIT.Shared/MowIT.Shared.csproj) |
| `729D698B…` | **MowIT.ScheduleServer** | [`MowIT.ScheduleServer/MowIT.ScheduleServer.csproj`](MowIT.ScheduleServer/MowIT.ScheduleServer.csproj) |
| `867FC54D…` | **MowIT.RobotSimulator** | [`MowIT.RobotSimulator/MowIT.RobotSimulator.csproj`](MowIT.RobotSimulator/MowIT.RobotSimulator.csproj) |

Only `MowIT` has `Deploy.0` entries in the solution configuration — that is the MSBuild flag that says "this project can be deployed to a device/emulator". The others are plain build targets.

**Project reference graph (who depends on whom):**

```
MowIT            ──▶ MowIT.Shared
MowIT.ScheduleServer ──▶ MowIT.Shared
MowIT.RobotSimulator ──▶ MowIT.Shared
```

There are **no other project references**. In particular the app does not reference the server and the server does not reference the app — they only agree on the DTO shapes in `MowIT.Shared`. That is exactly what you want: the contract is a separate, dependency-free assembly.

### `global.json`

```json
{ "sdk": { "version": "8.0.405", "rollForward": "latestPatch" } }
```

*Plain:* "everyone who builds this repository must use .NET SDK 8.0.405 or a newer patch of 8.0". This prevents "works on my machine" problems caused by SDK drift.

---

## 2. `MowIT.csproj` — the app, line by line

```xml
<TargetFrameworks>net8.0-android;net8.0-ios;net8.0-maccatalyst</TargetFrameworks>
<TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('windows'))">$(TargetFrameworks);net8.0-windows10.0.19041.0</TargetFrameworks>
```
*Plain:* one project produces four different applications. The Windows head is only added when you are actually building on Windows (you cannot build a WinUI app on a Mac).

| Property | Value | Meaning |
|---|---|---|
| `OutputType` | `Exe` | Produces an executable app head, not a library |
| `UseMaui` | `true` | Turns on the whole MAUI build pipeline (XAML compilation, resource generation, app packaging) |
| `SingleProject` | `true` | One project holds all platform heads; `Platforms/` sub-folders are compiled conditionally |
| `ImplicitUsings` | `enable` | `using System;`, `using System.Linq;` etc. are added automatically — that is why you rarely see them |
| `Nullable` | `enable` | Compiler tracks null-ability; `string?` means "may be null", `string` means "must not be" |
| `ApplicationId` | `com.greentitan.mowit` | Android package name / iOS bundle id |
| `ApplicationTitle` | `MowIT` | The name under the icon |
| `ApplicationDisplayVersion` / `ApplicationVersion` | `1.0` / `1` | User-visible version and internal build number |
| `SupportedOSPlatformVersion` | iOS 11, MacCatalyst 13.1, **Android 21**, Windows 10.0.17763 | Minimum OS. Android 21 (Lollipop) is required for BLE APIs |
| `WindowsPackageType` | `None` | Build Windows as an unpackaged .exe — no MSIX signing needed for development |

**Resource item groups:**

```xml
<MauiIcon Include="Resources\AppIcon\appicon.svg" ForegroundFile="…\appiconfg.svg" Color="#1B5E20"/>
<MauiSplashScreen Include="Resources\Splash\splash.svg" Color="#1B5E20" BaseSize="128,128"/>
<MauiImage Include="Resources\Images\*"/>
<MauiFont  Include="Resources\Fonts\*"/>
<MauiAsset Include="Resources\Raw\**" LogicalName="%(RecursiveDir)%(Filename)%(Extension)"/>
```
*Plain:* MAUI takes **one SVG** and generates every icon size Android and iOS need at build time. `#1B5E20` is the dark-green background behind the icon and splash.

### NuGet packages and why each one is there

| Package | Version | Why it is in the project |
|---|---|---|
| `Microsoft.Maui.Controls` | `$(MauiVersion)` | The UI framework itself |
| `Microsoft.Maui.Controls.Compatibility` | `$(MauiVersion)` | Legacy Xamarin.Forms layout compatibility — pulled in by Mapsui |
| **`Mapsui.Maui`** | 4.1.7 | The slippy-map control used on the Map page (OpenStreetMap tiles, layers, pan/zoom) |
| **`Mapsui.Nts`** | 4.1.7 | NetTopologySuite integration — lets us build `Polygon`/`LineString` geometry features |
| `Microsoft.Extensions.Logging.Debug` | 8.0.1 | `ILogger` output into the debugger window (only added in `#if DEBUG`) |
| **`CommunityToolkit.Mvvm`** | 8.3.2 | Source generators: `[ObservableProperty]`, `[RelayCommand]`, and `WeakReferenceMessenger` |
| **`CommunityToolkit.Maui`** | 9.1.1 | `Toast.Make(...)` pop-ups; initialised by `.UseMauiCommunityToolkit()` |
| **`Plugin.BLE`** | 3.1.0 | Cross-platform Bluetooth Low Energy client (`IBluetoothLE`, `IAdapter`, `ICharacteristic`) |
| **`sqlite-net-pcl`** | 1.9.172 | Tiny async ORM: `[Table]`/`[PrimaryKey]` attributes, `CreateTableAsync<T>()`, LINQ queries |
| `SQLitePCLRaw.bundle_green` | 2.1.10 | The native SQLite binaries for every platform that `sqlite-net-pcl` calls into |
| **`System.Reactive`** | 6.0.1 | Rx.NET: `IObservable<T>`, `Subject<T>`, `Sample()`, `Switch()`, `SelectMany()` |
| **`SkiaSharp.Views.Maui.Controls`** | 2.88.9 | 2-D drawing canvas used for the joystick and the local ENU map |

> **Defence tip:** if asked "why Rx and not events?", the answer is in this list — `Sample`, `Switch` and `DistinctUntilChanged` give you throttling, transport-hot-swapping and de-duplication as one-liners; with plain C# events you would hand-write timers and unsubscribe logic in every consumer.

---

## 3. `MowIT.Shared.csproj` — the contract library

```xml
<TargetFramework>net8.0</TargetFramework>
```
Plain `net8.0` — **no** platform suffix, **no** package references at all. It contains only records. That is deliberate: the server (which cannot reference MAUI) and the app (which cannot reference ASP.NET Core) can both use it.

---

## 4. `MowIT.ScheduleServer.csproj` — the backend

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
```
The **Web** SDK — this is what brings in ASP.NET Core, `WebApplication.CreateBuilder`, static-file handling, `launchSettings.json` support, etc.

| Package | Version | Why |
|---|---|---|
| `Microsoft.AspNetCore.Authentication` | 2.2.0 | Base classes for the custom `AuthenticationHandler` |
| `Microsoft.Data.Sqlite` | 8.0.10 | ADO.NET provider for SQLite (`SqliteConnection`, `SqliteConnectionStringBuilder`) |
| `Dapper` | 2.1.35 | Micro-ORM: `connection.Execute(sql, params)` / `QueryAsync<T>` — raw SQL, no change-tracking |
| `Swashbuckle.AspNetCore` | 6.6.2 | Generates OpenAPI/Swagger UI so you can try the endpoints in a browser during the demo |

`<UserSecretsId>mowit-schedule-server</UserSecretsId>` allows real tokens to be stored outside the repository in production.

**Why Dapper and not Entity Framework?** Two tables, four queries, no migrations needed. EF Core would add ~10 MB and a migration workflow for nothing. Good answer to give.

---

## 5. `MowIT.RobotSimulator.csproj` — the fake robot

Plain console app (`OutputType=Exe`, `net8.0`) with `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Http` and a `CopyToOutputDirectory` rule for `appsettings.json`.

---

## 6. Configuration files

### Server — [`appsettings.json`](MowIT.ScheduleServer/appsettings.json)
```json
{ "Database": { "Path": "schedules.db" }, "RobotTokens": { "Tokens": {} } }
```
Production defaults: a database file and **no tokens** (so nothing can authenticate until you add one).

### Server — [`appsettings.Development.json`](MowIT.ScheduleServer/appsettings.Development.json)
```json
{ "Database": { "Path": "schedules-dev.db" },
  "RobotTokens": { "Tokens": { "demo-robot-01": "dev-token-please-replace-in-prod" } } }
```
The development override defines one robot id and its token. Note the token literally contains the words *please-replace-in-prod* — that is intentional self-documentation.

### Server — [`Properties/launchSettings.json`](MowIT.ScheduleServer/Properties/launchSettings.json)
```json
"applicationUrl": "http://0.0.0.0:5080",
"ASPNETCORE_ENVIRONMENT": "Development"
```
`0.0.0.0` (not `localhost`) so that an Android emulator or a phone on the same WiFi can reach it. Port **5080** matches `WifiRobotOptions.ServerPort`.

### Robot simulator — [`appsettings.json`](MowIT.RobotSimulator/appsettings.json)
```json
{ "Robot": { "BaseUrl": "http://localhost:5080", "RobotId": "demo-robot-01",
             "Token": "dev-token-please-replace-in-prod", "TickHz": 2 } }
```
Configuration is layered in [`Program.cs:8`](MowIT.RobotSimulator/Program.cs#L8): JSON file → environment variables → command line, so you can override anything with e.g. `dotnet run -- --Robot:TickHz=5`.

### App — [`WifiRobotOptions`](MowIT/Infrastructure/Wifi/WifiRobotOptions.cs)
The app-side counterpart. It has **candidate URLs** rather than one URL:
```csharp
#if ANDROID
  "http://10.0.2.2:5080",      // the Android emulator's alias for the host machine's localhost
  "http://192.168.56.1:5080",  // a typical host-only adapter address
  "http://172.20.10.6:5080",   // a typical iPhone-hotspot address
#else
  "http://localhost:5080"
#endif
```
`StartScanAsync` tries them in order and remembers the first that responds. This is a small but genuinely useful piece of engineering — it removes the #1 cause of "it doesn't work on the emulator".

### App — [`Platforms/Android/AndroidManifest.xml`](MowIT/Platforms/Android/AndroidManifest.xml)
Declares `INTERNET`, `ACCESS_NETWORK_STATE`, the legacy `BLUETOOTH`/`BLUETOOTH_ADMIN` (capped at `maxSdkVersion=30`), the modern `BLUETOOTH_SCAN` (with `neverForLocation`), `BLUETOOTH_CONNECT`, `BLUETOOTH_ADVERTISE`, and fine/coarse location. `android:usesCleartextTraffic="true"` is required because the WiFi transport uses plain `http://` in development.

### `.gitignore`
Ignores `bin/`, `obj/`, `.vs/`, `*.user`, `.vscode/`, `.claude/`, `*.log`, `*.db`, `*.db3`. So `schedules-dev.db` on disk is a local artefact, not committed source.

---

## 7. How to build

```powershell
# everything (the app will build all four heads — slow the first time)
dotnet build c:\mowit\mowit.sln

# just the server / simulator (fast)
dotnet build c:\mowit\MowIT.ScheduleServer\MowIT.ScheduleServer.csproj
dotnet build c:\mowit\MowIT.RobotSimulator\MowIT.RobotSimulator.csproj

# only the Windows head of the app
dotnet build c:\mowit\MowIT\MowIT.csproj -f net8.0-windows10.0.19041.0
```

> Building the Android head requires the Android SDK + JDK installed; the iOS/MacCatalyst heads require macOS. On Windows you normally build `net8.0-android` and `net8.0-windows…`.

---

## 8. How to run — three scenarios

The app ships configured with `MultiTransportFactory`:

```csharp
private static readonly IRobotServiceFactory RobotFactory = new MultiTransportFactory();
```

which registers **both** robots and lets the Scan page's two buttons choose between them —
**Bluetooth** → the in-process `SimulatedRobotService`, **WiFi** → `WifiRobotService` talking to the server.
So scenarios A and B below are the *same build*; only which button you press differs.

### Scenario A — offline demo (simplest, no server)

1. Start the app (F5 in Visual Studio on the Windows or Android target).
2. Login → type any name → *Let's go*.
3. Scan → press **Bluetooth** → *Scan for Mowers* → two devices appear (`GreenTitan-SIM`, `GreenTitan-SIM2`) → *Connect*.
4. Control → wait ~8 s for the simulated GPS accuracy to ramp from 800 mm down under 50 mm → *Set Base* → drive with the joystick → *Record* → *Mark* ×4 → *New outline* → *Save boundary*.
5. Dashboard now shows *Ready to mow*; press **Mow**.

### Scenario B — full cloud demo (the impressive one)

1. **No code change needed** — `MultiTransportFactory` is already active. It registers both the simulator (as the "Bluetooth" slot) and `WifiRobotService`, plus the `RobotTransportRouter` that lets the Scan page switch between them, **and** it switches schedule sync from `NullScheduleSyncService` to the real `HttpScheduleSyncService` (see [`MauiProgram.cs`](MowIT/MauiProgram.cs)).
2. **Terminal 1:** `dotnet run --project c:\mowit\MowIT.ScheduleServer`
   → listens on `http://0.0.0.0:5080`; Swagger UI at `http://localhost:5080/swagger`.
3. **Terminal 2:** `dotnet run --project c:\mowit\MowIT.RobotSimulator`
   → prints `virtual robot 'demo-robot-01' -> http://localhost:5080 (tick 2 Hz)` and then a line for every command it receives.
4. **Run the app**, go to Scan, press **WiFi**, press *Find Robot Online* → `demo-robot-01` appears → *Connect*.
5. Now drive the joystick and watch Terminal 2 print `RX cmd Motor …`. Save a schedule on the Schedule page and watch Terminal 2 print `schedules synced from cloud: v1, 1 schedule(s)`.

### Scenario C — real GreenTitan hardware

* Classic Bluetooth: set `RobotFactory = new GreenTitanSppFactory();` — pair the mower in the OS Bluetooth settings first, then Scan lists paired devices.
* BLE: set `RobotFactory = new GreenTitanServiceFactory();` — requires a device advertising GATT service `00001234-0000-1000-8000-00805f9b34fb`.

> **This one-line switch is a design feature, not a shortcut.** It is the Abstract Factory pattern: `IRobotServiceFactory.RegisterServices()` is the only thing that knows how to build a transport stack. See [`11-DESIGN-PATTERNS.md`](11-DESIGN-PATTERNS.md).

---

## 9. Where the app writes files at runtime

Everything goes under `FileSystem.AppDataDirectory` (on Windows: `%LOCALAPPDATA%\Packages\…\LocalState` or the app folder; on Android: the app's private data dir):

| File | Written by | Content |
|---|---|---|
| `mowit.db3` | [`AppDatabase`](MowIT/Infrastructure/Persistence/AppDatabase.cs) | SQLite: boundary zones, boundary points, schedules |
| `logs/session_yyyyMMdd_HHmmss.log` | [`EventLogService`](MowIT/Application/Logging/EventLogService.cs) | Tab-separated log of every TX/RX/state event |
| `logs/gpstrace_yyyyMMdd_HHmmss.csv` | [`GpsTraceService`](MowIT/Application/Services/GpsTraceService.cs) | CSV of every GPS sample while connected |

Device preferences (`Preferences.Default`) store two values: `profile_name` (login) and `mowit.last_mow_at_ticks` (last mow time).
