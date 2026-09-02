# 00 — START HERE

**This is the reading guide for the MowIT documentation set.**
If you are preparing to present this project to a professor, read this file first, then follow the order below.

---

## 1. What is MowIT, in one paragraph?

MowIT is a **cross-platform mobile/desktop application (.NET MAUI, C#) that remote-controls a robotic lawn mower called "GreenTitan"**. The user connects to the mower (over Classic Bluetooth, BLE, or WiFi through a cloud server), drives it manually with an on-screen joystick, walks it around the garden to *record* the boundary of the lawn, saves that boundary as a "zone", lets the app compute an efficient mowing route inside that zone, sends the route to the mower, and schedules automatic mowing sessions. While the mower runs, the app receives live telemetry (GPS position, accuracy, heading, speed, battery, blade state) and shows it on a dashboard and on a real map.

The repository contains **four programs**, not one:

| Project | What it is | Why it exists |
|---|---|---|
| `MowIT` | The .NET MAUI app (Android / iOS / MacCatalyst / Windows) | The actual product — the client |
| `MowIT.ScheduleServer` | ASP.NET Core Minimal-API web server + SQLite | The cloud backend: stores schedules, relays telemetry & commands |
| `MowIT.RobotSimulator` | Console app that pretends to be a physical robot | Lets you demo/test the whole system with no hardware |
| `MowIT.Shared` | A class library with only DTOs (data contracts) | The shared "language" the app, server and robot all speak |

---

## 2. The 60-second pitch (say this at the start of the defence)

> "MowIT is a robot-lawnmower control application. It is written in C# with .NET MAUI so that one codebase runs on Android, iOS, macOS and Windows. The architecture is Clean Architecture with four layers — Domain, Application, Infrastructure, Presentation — and the dependency rule points inwards, so the business rules do not know anything about Bluetooth, SQLite or the UI framework.
>
> The interesting engineering problem is that the mower can be reached over **three completely different physical channels**: Classic Bluetooth serial (ASCII text protocol), Bluetooth Low Energy (binary GATT characteristics), and WiFi via a REST server. I solved that by defining five small domain interfaces — `IRobotScanner`, `IRobotConnection`, `IRobotSensors`, `IRobotControl`, `IRobotBoundary` — and writing one implementation per channel. Everything above those interfaces is written once and works with any transport, including a full software simulator I use for demos.
>
> The system also has a cloud side: an ASP.NET Core minimal-API server with token authentication and SQLite storage, which the app pushes mowing schedules to, and which relays commands and telemetry between the app and a robot that is not in Bluetooth range."

---

## 3. Reading order

| # | File | Read it to learn |
|---|---|---|
| 00 | **`00-START-HERE.md`** (this file) | Map of the docs, pitch, glossary |
| 01 | [`01-BIG-PICTURE.md`](01-BIG-PICTURE.md) | What the product does, the user's journey, how the 4 programs fit together |
| 02 | [`02-SOLUTION-AND-BUILD.md`](02-SOLUTION-AND-BUILD.md) | Every project file, every NuGet package, how to build and run everything |
| 03 | [`03-ARCHITECTURE.md`](03-ARCHITECTURE.md) | Clean Architecture, the dependency rule, dependency injection, the composition root |
| 04 | [`04-DOMAIN-LAYER.md`](04-DOMAIN-LAYER.md) | Entities, enums, the GPS→metres geometry maths, the route-planning algorithms |
| 05 | [`05-APPLICATION-LAYER.md`](05-APPLICATION-LAYER.md) | Use cases, geofence, GPS tracing, state machine, command pipeline, messaging |
| 06 | [`06-INFRASTRUCTURE-LAYER.md`](06-INFRASTRUCTURE-LAYER.md) | Bluetooth SPP, BLE, WiFi, the simulator, SQLite persistence, the scheduler |
| 07 | [`07-PRESENTATION-LAYER.md`](07-PRESENTATION-LAYER.md) | MVVM, every page, every ViewModel, the custom joystick and the Skia map |
| 08 | [`08-SERVER-SIDE.md`](08-SERVER-SIDE.md) | The web server, its endpoints, its auth, its storage, and the virtual robot |
| 09 | [`09-PROTOCOLS-AND-DATA.md`](09-PROTOCOLS-AND-DATA.md) | Byte-level and text-level protocol specs + all database schemas |
| 10 | [`10-TESTING-AND-QUALITY.md`](10-TESTING-AND-QUALITY.md) | **Honest state of testing**, what the simulator replaces, a concrete test plan |
| 11 | [`11-DESIGN-PATTERNS.md`](11-DESIGN-PATTERNS.md) | Every GoF/architectural pattern used, with the exact file and line |
| 12 | [`12-DATA-FLOWS.md`](12-DATA-FLOWS.md) | Step-by-step traces: "I press the joystick → what happens, line by line" |
| 13 | [`13-DEFENCE-SCRIPT-AND-QA.md`](13-DEFENCE-SCRIPT-AND-QA.md) | A presentation script + ~40 likely professor questions with answers |
| 14 | [`14-FILE-INDEX.md`](14-FILE-INDEX.md) | Every single file in the repo with a one-line purpose |
| 15 | [`15-WINDOWS-VS-ANDROID.md`](15-WINDOWS-VS-ANDROID.md) | How one codebase becomes four apps: what changes per platform, how to edit and verify each one |

**If you only have one evening:** read 01, 03, 12 and 13. That is enough to answer 80 % of questions.

---

## 4. Glossary — learn these 20 words and you can talk about the project

| Term | Plain meaning |
|---|---|
| **GreenTitan** | The name of the physical robot mower this app talks to |
| **Transport** | The physical channel used to reach the robot: Classic Bluetooth, BLE, or WiFi |
| **SPP** | *Serial Port Profile* — Classic Bluetooth mode that behaves like a serial cable; you send text |
| **BLE / GATT** | *Bluetooth Low Energy*; GATT is its data model of "services" and "characteristics" (little named data slots) |
| **Telemetry** | Measurements the robot sends *up* to the app (position, battery, speed…) |
| **Command** | An instruction the app sends *down* to the robot (start, stop, move, mark point…) |
| **Datum / Base** | A single GPS point captured once; all local coordinates are measured *from* it |
| **ENU / NEU frame** | A local flat coordinate system in metres: East, North, Up — used instead of raw latitude/longitude |
| **Boundary / Zone** | A closed polygon of GPS points describing the lawn the robot may cut |
| **Geofence** | Software that watches the robot's GPS and stops it if it leaves the zone |
| **Route / Waypoints** | The ordered list of GPS points the robot drives through to cut the whole zone |
| **Boustrophedon** | "As the ox ploughs" — the back-and-forth parallel-lines mowing pattern |
| **RTK** | *Real-Time Kinematic* GPS — centimetre-accurate GPS, needed for lawn-precision navigation |
| **Fix type** | Quality of the GPS solution: NoFix → Standard → RTK Float → RTK Fixed (best) |
| **MVVM** | *Model-View-ViewModel* — the UI pattern where the screen binds to a class instead of calling it |
| **DI** | *Dependency Injection* — objects get their collaborators handed to them instead of creating them |
| **Reactive / `IObservable`** | A stream of values over time; you *subscribe* instead of polling |
| **DTO** | *Data Transfer Object* — a dumb class whose only job is to be serialised to JSON |
| **Minimal API** | ASP.NET Core style where endpoints are lambdas (`app.MapGet(...)`) instead of controller classes |
| **Composition root** | The one place where all the object wiring happens — here, `MauiProgram.cs` |

---

## 5. Very important: the honesty section

You said you must be able to defend this as *your* work and understand it deeply. So these docs deliberately also record **what is incomplete or inconsistent in the code**. Knowing these makes you *more* credible, not less — a student who says "yes, `BatteryCheckHandler` exists but I have not wired it into the pipeline yet, here is why" is obviously the author.

The full list is in [`13-DEFENCE-SCRIPT-AND-QA.md` §4](13-DEFENCE-SCRIPT-AND-QA.md). The short version:

1. **There is no automated test project in the solution.** Testing today is manual + simulator-driven. Chapter 10 explains this honestly and gives a ready-to-implement test plan.
2. `BatteryCheckHandler` and `StateGuardHandler` are written but **not inserted into the command pipeline** — only logging → dispatch is active.
3. The transport is chosen by a hard-coded field in [`MauiProgram.cs`](MowIT/MauiProgram.cs), not by a setting. It is currently `MultiTransportFactory` (simulator + cloud, for demoing); switching to real hardware means changing that one line.
4. A few classes are written but unused: `BoundaryZoneBuilder`, `NullRobotService`, `GpsStatusBadge`, `MotorCommandDto`, `BoundaryDto`, `BlePacketSerializer.SerializeSchedule`.
5. `MowIT/Domain/Enums/mowit.code-workspace` is a stray editor file saved into the wrong folder.

---

## 6. How to demo it live (no hardware needed)

Full instructions are in [`02-SOLUTION-AND-BUILD.md`](02-SOLUTION-AND-BUILD.md). The 10-second version:

The app is configured with `MultiTransportFactory`, which gives you **both** demos in one build — the Scan page's two buttons pick between them:

* **Simplest demo (1 process):** run the MAUI app, press **Bluetooth** on the Scan page. That slot is the in-process `SimulatedRobotService`, so "Scan" finds two fake mowers and everything works with no server and no network.
* **Full-system demo (3 processes):** also run `MowIT.ScheduleServer` and `MowIT.RobotSimulator`, then press **WiFi** on the Scan page. Now commands travel app → HTTP → server → HTTP → robot and telemetry comes back the same way. This is the impressive demo — and you can switch between the two transports live, which is the clearest possible proof of the interface abstraction.
