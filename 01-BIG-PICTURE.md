# 01 — THE BIG PICTURE

*Read this before any code. It explains **what** the system does and **why** it is split the way it is.*

---

## 1. The problem the project solves

A robotic lawn mower is not like a vacuum robot. Three things make it hard:

1. **It is outdoors.** There are no walls to bump into and no ceiling to map. The only reliable way to know "where am I" is GNSS/GPS — and normal phone-grade GPS (±3–5 m) is far too coarse for a lawn. The GreenTitan robot therefore uses **RTK GPS**, which reaches centimetre accuracy, and the app must show the user whether the fix is good enough *before* letting them do anything precise.
2. **The lawn has no fixed definition.** Traditional mowers need a physical buried perimeter wire. This project replaces the wire with a **software boundary**: the user drives the robot around their garden and presses a button at each corner; the robot reports its coordinates; the app builds a polygon.
3. **The link is unreliable and comes in several flavours.** In the garden you may be on Bluetooth. From inside the house you may need WiFi/internet. The robot firmware speaks a text protocol over Classic Bluetooth, but a BLE variant exists too. **The app must not care.**

MowIT is the answer to all three.

---

## 2. What the user actually does (the user journey)

```
  ┌──────────┐   ┌────────┐   ┌───────────┐   ┌─────────┐   ┌──────────┐
  │  Login   │──▶│  Scan  │──▶│ Dashboard │◀─▶│ Control │◀─▶│   Map    │
  │  (name)  │   │(pick   │   │  (live    │   │ (drive, │   │(draw zone│
  │          │   │ mower) │   │telemetry) │   │ record) │   │on OSM)   │
  └──────────┘   └────────┘   └───────────┘   └─────────┘   └──────────┘
                                     ▲                            │
                                     │        ┌──────────┐        │
                                     └────────│ Schedule │◀───────┘
                                              │ (when to │
                                              │   mow)   │
                                              └──────────┘
```

**Step by step, in plain words:**

1. **Login page** — no real authentication. You type a profile name; it is stored in device preferences so the dashboard can greet you. It exists to make the app feel like a product and to give the Shell a start route.
2. **Scan page** — you choose the connection type (Bluetooth or WiFi) and press *Scan*. A list of discovered mowers appears. You press *Connect*. On success the app navigates to the Dashboard.
3. **Control page** — this is the heart of the app. Here you:
   * press **Set Base**, which tells the robot "the spot you are standing on right now is the origin (0,0) of the local coordinate system". This only succeeds if the GPS accuracy is good enough (< 50 mm in the simulator).
   * drive the robot with the **joystick** to the first corner of your lawn,
   * press **Mark** at each corner (the robot answers with its local X/Y in centimetres),
   * press **New outline** to close one polygon and start another (so you can have several zones or cut-outs),
   * press **Save boundary**, which ends the recording session on the robot *and* saves the walked polygon into the app's own SQLite database as a GPS zone so that the geofence can use it.
   * There is a second sub-mode, **Capture and plan**, where instead of the robot recording, the *app* captures the corners, computes a mowing route with a chosen strategy, previews it on the local map, and can save it or start mowing immediately.
4. **Map page** — a real slippy map (OpenStreetMap tiles via Mapsui). Here you can draw a boundary by *tapping the map* instead of walking the robot, load/delete saved zones, calculate a route (Boustrophedon or Spiral), see how many waypoints and how many metres it is, and send it to the robot.
5. **Dashboard page** — the read-only "cockpit": current state (IDLE/MOWING/…), battery, speed, GPS fix quality and accuracy, heading with compass letters, blade on/off, manual vs auto, uptime, last-mowed time, plus **Mow** and **Stop** buttons.
6. **Schedule page** — pick days of the week, a start time, and (optionally) one of the saved zones; save it. A background service checks every 20 seconds whether it is time to run one; there is also a ▶ button to run a schedule immediately.

Throughout, an **event log** records every command sent and every reply received, both to memory (for on-screen display) and to a file, and a **GPS trace** is written to a CSV file for the whole connected session. Those two files are what turn a demo into evidence.

---

## 3. The four programs and how they talk

```
                                       ┌──────────────────────────────┐
                                       │        MowIT.Shared          │
                                       │  (DTOs = the shared language)│
                                       │ TelemetryDto, RobotCommandDto│
                                       │ RobotEventDto, ScheduleDto   │
                                       └───────▲───────────────▲──────┘
                                               │ referenced by │
        ┌──────────────────────┐               │               │        ┌────────────────────────┐
        │      MowIT (app)     │               │               │        │  MowIT.RobotSimulator  │
        │  .NET MAUI client    │───────────────┘               └────────│  console "fake robot"  │
        └──────────┬───────────┘                                        └───────────┬────────────┘
                   │                                                                │
                   │  HTTP+JSON (Bearer token)          HTTP+JSON (Bearer token)    │
                   │  POST /robots/{id}/commands        GET  /robots/{id}/commands  │
                   │  GET  /robots/{id}/telemetry       POST /robots/{id}/telemetry │
                   │  GET  /robots/{id}/events          POST /robots/{id}/events    │
                   │  POST /robots/{id}/schedules       GET  /robots/{id}/schedules │
                   ▼                                                                ▼
              ┌──────────────────────────────────────────────────────────────────────┐
              │                       MowIT.ScheduleServer                           │
              │   ASP.NET Core Minimal API  ·  custom Bearer auth  ·  SQLite+Dapper  │
              │   schedules → SQLite   |   telemetry, commands, events → in-memory   │
              └──────────────────────────────────────────────────────────────────────┘
```

**Key insight to state in the defence:** the server is a **relay/mailbox**, not a controller. It never decides anything. The app *posts* a command into a queue; the robot *polls* that queue twice a second and drains it. The robot *posts* telemetry; the app *polls* it twice a second. Events (like "base captured") go the same way but with a **monotonic sequence cursor** so the app never misses or re-processes one. This design was chosen because a robot on a home WiFi has no public address — it cannot be called, it can only call out.

When Bluetooth is used instead, the server is not involved at all: the app talks to the robot directly and the schedule sync is replaced by a no-op (`NullScheduleSyncService`).

---

## 4. The three (four) ways to reach a mower

| Transport | Class | Protocol | Where it runs |
|---|---|---|---|
| **Classic Bluetooth SPP** | [`GreenTitanSppService`](MowIT/Infrastructure/ClassicBt/GreenTitanSppService.cs) | ASCII commands terminated by `<`, e.g. `MOWER/MOVE/0.300,-0.150<` | Android + Windows (real hardware) |
| **Bluetooth Low Energy** | [`GreenTitanBleService`](MowIT/Infrastructure/Ble/GreenTitanBleService.cs) | Binary GATT characteristics, little-endian structs | All platforms (real hardware) |
| **WiFi / cloud** | [`WifiRobotService`](MowIT/Infrastructure/Wifi/WifiRobotService.cs) | HTTP + JSON against `MowIT.ScheduleServer` | All platforms |
| **Simulator** | [`SimulatedRobotService`](MowIT/Infrastructure/Simulator/SimulatedRobotService.cs) | none — it *is* the robot, in-process | All platforms (demo/test) |

All four implement exactly the same five interfaces, so the rest of the app is written once. Chapter 3 explains how, chapter 6 explains each one.

---

## 5. The three coordinate systems (this always gets asked)

This is the single most conceptually interesting part of the project, so understand it well.

1. **Geodetic (WGS-84): latitude, longitude in degrees.** What GPS gives you. Good for storing and for drawing on a world map. Bad for maths: one degree of longitude is 111 km at the equator and 0 km at the pole, so you cannot just subtract two longitudes and call it distance.

2. **Local ENU (East–North–Up) in metres.** A flat "tabletop" coordinate system anchored at a chosen origin point (the *datum*). Within a garden (tens of metres) the Earth is flat enough that this is accurate to millimetres. All geometry — is the point inside the polygon, how far is it from the edge, where are the mowing rows — is done here. Implemented in [`LocalProjection`](MowIT/Domain/Geometry/LocalProjection.cs) via a proper geodetic→ECEF→ENU transform on the WGS-84 ellipsoid (see chapter 4 §3 for the maths).

3. **Robot-local NEU in centimetres.** What the GreenTitan firmware itself reports after you capture a base: `MOWER/CAPTURE/POINT/OK/1234,-560` means "1234 cm east, −560 cm north of the base". Represented by [`LocalPoint`](MowIT/Domain/Entities/LocalPoint.cs). The Control page's Skia canvas draws in this system.

There is a **fourth** system used only for drawing: **Spherical Mercator (EPSG:3857)**, the projection every web map uses. [`MapPage`](MowIT/Presentation/Pages/MapPage.xaml.cs) converts GPS→Mercator with `SphericalMercator.FromLonLat` before handing points to Mapsui.

**The pipeline in one line:** robot reports lat/lon → app projects to ENU metres → algorithms run in metres → results are projected back to lat/lon → stored in SQLite and drawn on the map.

---

## 6. What makes this project non-trivial (say this if asked "what was hard?")

1. **Protocol reverse-fitting.** The GreenTitan firmware does not frame its replies with newlines reliably, so the read loop must split an incoming byte stream on either a newline *or* the start of the next known message prefix (`GPS/`, `MOWER/`). See `FindMessageSplit` in [`GreenTitanSppService.cs:585`](MowIT/Infrastructure/ClassicBt/GreenTitanSppService.cs#L585).
2. **The firmware silently drops MOVE commands if it is not in manual mode.** The app therefore auto-enables manual mode, waits for the acknowledgement, and — if a `MOWER/MOVE/FAIL/NOT_MANUAL` still arrives — re-enables manual mode and *replays the last move* exactly once (guarded by an `Interlocked` flag so two recoveries never run at the same time). See `RecoverManualModeAsync`.
3. **A joystick generates hundreds of events per second.** Sending them all would flood a 9600-baud-ish serial link. The app throttles with Rx `Sample(100 ms)` and adds a 250 ms *keep-alive* repeat while the stick is held, because the robot has a 600 ms watchdog that stops the motors if it hears nothing. See [`ControlViewModel.cs:214`](MowIT/Presentation/ViewModels/ControlViewModel.cs#L214).
4. **Safety.** A geofence subscribes to the position stream, checks polygon containment with a ray-casting test, and issues `Stop` the moment the robot leaves the saved zone — logging exactly how many metres past the boundary it got and what the GPS accuracy was at that moment.
5. **Three transports, one UI.** Achieved through interface segregation + a `RobotTransportRouter` that can switch the live transport at runtime while keeping the observable streams unbroken (using `BehaviorSubject` + `Switch()`).

---

## 7. Repository map (top level)

```
c:\mowit\
├── mowit.sln                  ← Visual Studio solution: 4 projects
├── global.json                ← pins .NET SDK 8.0.405
├── .gitignore
├── MowIT/                     ← THE APP  (client)
│   ├── MauiProgram.cs         ← composition root: all DI wiring
│   ├── App.xaml(.cs)          ← app resources + converter registry
│   ├── AppShell.xaml(.cs)     ← navigation (routes + tab bar)
│   ├── Domain/                ← layer 1: pure business model, zero dependencies
│   ├── Application/           ← layer 2: use cases & orchestration
│   ├── Infrastructure/        ← layer 3: BLE, BT, WiFi, SQLite, simulator
│   ├── Presentation/          ← layer 4: pages, viewmodels, controls, converters
│   ├── Platforms/             ← per-OS entry points & permissions
│   └── Resources/             ← fonts, images, colours, styles, icons
├── MowIT.Shared/              ← DTOs shared by app + server + robot
├── MowIT.ScheduleServer/      ← THE SERVER
└── MowIT.RobotSimulator/      ← THE FAKE ROBOT
```

Every one of those files is catalogued with a one-line purpose in [`14-FILE-INDEX.md`](14-FILE-INDEX.md).
