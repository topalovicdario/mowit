# 09 — PROTOCOLS AND DATA FORMATS

Everything that crosses a boundary: bytes on a Bluetooth link, JSON on the wire, rows in a database, lines in a log file.

---

## 1. The GreenTitan ASCII protocol (Classic Bluetooth SPP)

The protocol spoken by the **real firmware**. Implemented in [`GreenTitanSppService`](MowIT/Infrastructure/ClassicBt/GreenTitanSppService.cs) and imitated exactly by [`SimulatedRobotService`](MowIT/Infrastructure/Simulator/SimulatedRobotService.cs).

### 1.1 Format

* **Transport:** RFCOMM / SPP, UUID `00001101-0000-1000-8000-00805F9B34FB`, ASCII encoding.
* **Requests** are sent as `COMMAND<` — a literal `<` terminator, no newline.
  ```csharp
  _writer?.Write(cmd + "<");
  ```
* **Responses** are slash-separated paths, e.g. `MOWER/CAPTURE/POINT/OK/1234,-560`. Framing is **unreliable** — see §1.5.
* Fields are separated by `/`; coordinate pairs inside a field by `,`.
* All numbers use **invariant culture** (`.` as the decimal separator), enforced by `CultureInfo.InvariantCulture` on both parse and format.

### 1.2 Commands the app sends

| Command | Meaning | `RobotAction` it maps from |
|---|---|---|
| `GPS/GET/POS` | poll current position | *(2 Hz timer)* |
| `GPS/GET/ACCURACY` | poll current accuracy | *(0.5 Hz timer)* |
| `GPS/CAPTURE/BASE` | set the local origin here | `CaptureBase` |
| `MOWER/START` | begin mowing the stored path | `StartMowing`, `StartRoute` |
| `MOWER/MANUAL/ON` | enter manual driving mode | `ManualModeOn` |
| `MOWER/MANUAL/OFF` | leave manual mode (also used as **Stop**) | `Stop`, `ManualModeOff` |
| `MOWER/MOVE/<lin>,<ang>` | drive: m/s and rad/s, 3 decimals | *(motor command)* |
| `MOWER/CAPTURE/START` | begin a boundary-recording session | `BoundaryRecordStart`, `BoundaryClear` |
| `MOWER/CAPTURE/POINT` | mark the current position as a vertex | `BoundaryCapturePoint` |
| `MOWER/CAPTURE/OUTLINE` | close the current polygon, start the next | `CaptureOutline` |
| `MOWER/CAPTURE/EXIT` | mark the exit/dock point | `CaptureExit` *(simulator only)* |
| `MOWER/CAPTURE/END` | finish and persist the path | `BoundaryRecordEnd` |

Actions with **no firmware equivalent** (`Pause`, `Resume`, `ReturnToBase`, `BladeOn`, `BladeOff`) map to `null` and are logged as unsupported:
```csharp
if (cmd is null) { _logger.LogWarning("Action {Action} is not supported by GreenTitan firmware", action); return; }
```
Honest interface implementation rather than silent failure.

Note two protocol quirks the code accommodates:
* **`Stop` maps to `MOWER/MANUAL/OFF`** — leaving manual mode *is* how you stop the firmware.
* **`BoundaryClear` maps to `MOWER/CAPTURE/START`** — starting a new capture session is what clears the stored path.

Every command is followed by `await Task.Delay(150)` (`CmdDelayMs`) so the firmware's input buffer is not overrun.

### 1.3 Responses the robot sends

| Response | Meaning | Parsed into |
|---|---|---|
| `GPS/POS/<lat>,<lon>` | current position | `SensorSnapshot.Gps` + inferred fix type |
| `GPS/ACCURACY/<metres>` | accuracy in **metres** (converted to mm internally) | `GpsAccuracyMm`, `GpsFixType` |
| `GPS/CAPTURE/BASE/STARTED` | datum capture began | info log only |
| `GPS/CAPTURE/BASE/DONE` | datum set | `BaseCapturedMessage` |
| `GPS/CAPTURE/BASE/<verdict>/BUSY` | already running | info log, keep waiting |
| `GPS/CAPTURE/BASE/<verdict>/<reason>` | failed | `RobotErrorMessage("BASE_CAPTURE_FAIL/<reason>")` |
| `MOWER/START/OK` | mowing started | status → `Mowing` |
| `MOWER/START/FAIL` | refused | `RobotErrorMessage("MOWER_START_FAIL")` |
| `MOWER/MANUAL/ON/OK` | manual mode on | `_manualMode = true`, `IsManualMode` |
| `MOWER/MANUAL/OFF/…` | manual mode off | `_manualMode = false` |
| `MOWER/MOVE/OK` | move accepted | `IsMotorMoving` = (velocity ≠ 0) |
| `MOWER/MOVE/STOPPED` | motors stopped | `IsMotorMoving = false` |
| `MOWER/MOVE/FAIL/NOT_MANUAL` | rejected — not in manual mode | triggers `RecoverManualModeAsync()` |
| `MOWER/CAPTURE/START/OK` | session started | status → `RecordingBoundary`, `BoundaryClearedMessage` |
| `MOWER/CAPTURE/POINT/OK/<lat>,<lon>` | vertex recorded | `BoundaryGpsPointCapturedMessage` |
| `MOWER/CAPTURE/POINT/FAIL/<reason>` | vertex rejected | `RobotErrorMessage("CAPTURE_POINT_FAIL/<reason>")` |
| `MOWER/CAPTURE/OUTLINE/OK` | polygon closed | `OutlineCapturedMessage` |
| `MOWER/CAPTURE/OUTLINE/FAIL` | rejected | `RobotErrorMessage("CAPTURE_OUTLINE_FAIL")` |
| `MOWER/CAPTURE/END/OK` | path saved | `CaptureEndMessage(true)` |
| `MOWER/CAPTURE/END/FAIL/<reason>` | failed | `CaptureEndMessage(false, reason)` + error |

Known failure reasons: `NO_DATUM`, `NOT_READY`, `NOT_RECORDING`, `NO_POLYGON`, `NO_EXIT`, `TOO_FEW_POINTS`, `ACCURACY`, `BUSY`.

**Note the coordinate inconsistency, and know why it matters:** the real SPP firmware reports capture points as **latitude,longitude**, while the simulator and the cloud robot report **centimetres in the local frame**. That is exactly why there are two messenger message types (`BoundaryGpsPointCapturedMessage` vs `BoundaryPointCapturedMessage`) and why `ControlViewModel` maintains two parallel polygon collections.

### 1.4 A real session transcript

```
TX  GPS/GET/POS<
RX  GPS/POS/43.8563100,18.4131200                (+41 ms)
TX  GPS/GET/ACCURACY<
RX  GPS/ACCURACY/0.031                            (+38 ms)
TX  GPS/CAPTURE/BASE<
RX  GPS/CAPTURE/BASE/STARTED
RX  GPS/CAPTURE/BASE/DONE                         (+1180 ms)
TX  MOWER/CAPTURE/START<
RX  MOWER/CAPTURE/START/OK                        (+96 ms)
TX  MOWER/MANUAL/ON<
RX  MOWER/MANUAL/ON/OK                            (+88 ms)
TX  MOWER/MOVE/0.350,-0.200<
RX  MOWER/MOVE/OK                                 (+72 ms)
TX  MOWER/CAPTURE/POINT<
RX  MOWER/CAPTURE/POINT/OK/43.8563400,18.4131900  (+104 ms)
…
TX  MOWER/CAPTURE/OUTLINE<
RX  MOWER/CAPTURE/OUTLINE/OK                      (+91 ms)
TX  MOWER/CAPTURE/END<
RX  MOWER/CAPTURE/END/OK                          (+133 ms)
```
The `(+n ms)` suffixes are produced by the correlation/latency mechanism described in [`06-INFRASTRUCTURE-LAYER.md §2.7`](06-INFRASTRUCTURE-LAYER.md) — **the app measures its own link quality**, which gives you real numbers for the report.

### 1.5 Framing — the hard part

The firmware does not consistently terminate replies. Two messages can arrive glued:
```
"MOWER/MOVE/OKGPS/POS/43.8563,18.4131"
```
`FindMessageSplit` handles it: prefer a `\r`/`\n`; otherwise split immediately before the next occurrence of `GPS/` or `MOWER/` (searching from index 1 so the *current* message's own prefix does not match); otherwise return −1 meaning "incomplete, keep buffering". `FlushPending` loops until nothing more can be extracted.

---

## 2. The BLE GATT profile

Implemented in [`GreenTitanBleService`](MowIT/Infrastructure/Ble/GreenTitanBleService.cs) + [`BlePacketSerializer`](MowIT/Infrastructure/Ble/BlePacketSerializer.cs).

### 2.1 Service and characteristics

Base UUID pattern `0000XXXX-0000-1000-8000-00805f9b34fb`:

| XXXX | Name | Direction | Size | Property |
|---|---|---|---|---|
| `1234` | GreenTitan service | — | — | — |
| `1235` | GPS data | ← robot | 21 B | Notify |
| `1236` | IMU data | ← robot | 24 B | Notify |
| `1237` | Odometry | ← robot | 16 B | Notify |
| `1238` | Robot status | ← robot | 6 B | Notify |
| `1240` | Motor command | → robot | 8 B | Write **without response** |
| `1241` | Action command | → robot | 2 B | Write |
| `1242` | Boundary chunk | → robot | 20 B | Write |
| `1243` | Schedule data | → robot | 10 B | Write *(declared, unused)* |

MTU is negotiated to 512 bytes on connect (`RequestMtuAsync(512)`).

### 2.2 Byte layouts (all **little-endian**, via `BitConverter`)

```
GPS  (0x1235, 21 bytes)
  offset 0  : double  latitude        (8 B, IEEE-754)
  offset 8  : double  longitude       (8 B)
  offset 16 : float   accuracy_mm     (4 B)
  offset 20 : byte    fix_type        (0=NoFix 1=Standard 2=RtkFloat 3=RtkFixed)

IMU  (0x1236, 24 bytes)
  0  : float AccX   4  : float AccY   8  : float AccZ
  12 : float GyroX  16 : float GyroY  20 : float GyroZ

ODOMETRY (0x1237, 16 bytes)
  0 : float PosX_m   4 : float PosY_m   8 : float Heading_rad   12 : float LinearSpeed_mps

STATUS (0x1238, 6 bytes)
  0 : byte   state (RobotState)     1 : byte  battery_pct
  2 : byte   blade_on (0/1)         3 : byte  rain_detected (0/1)
  4 : ushort uptime_minutes (2 B)

MOTOR CMD (0x1240, 8 bytes)
  0 : float linear_mps    4 : float angular_radps

ACTION CMD (0x1241, 2 bytes)
  0 : byte action (RobotAction)   1 : byte param

BOUNDARY CHUNK (0x1242, 20 bytes)
  0 : byte index      1 : byte total      2 : byte point_type (0=boundary, 1=route)
  3 : byte reserved
  4 : double latitude      12 : double longitude

SCHEDULE (0x1243, 10 bytes)  — serialiser exists, never written
  0 : byte  days_mask  (bit 0 = Monday … bit 6 = Sunday)
  1 : byte  hour       2 : byte minute
  3 : byte  duration_high   4 : byte duration_low     (big-endian 16-bit minutes)
  5 : byte  is_active       6..9 : reserved
```

**Two details worth explaining:**
* `BitConverter` is little-endian on all supported platforms (x86/x64/ARM), which matches typical microcontroller output. A truly portable implementation would use `BinaryPrimitives.WriteInt64LittleEndian` — a legitimate "would improve" answer.
* The day mask rotation: `daysMask |= (byte)(1 << ((int)d + 6) % 7);` — .NET's `DayOfWeek.Sunday == 0`, but the firmware wants Monday = bit 0. `(0 + 6) % 7 = 6` → Sunday becomes bit 6. Correct.

### 2.3 Validation

Every notification handler checks the length before parsing:
```csharp
private void OnGpsUpdated(byte[] data) { if (data.Length < 21) return; … }
private void OnImuUpdated(byte[] data) { if (data.Length < 24) return; … }
private void OnOdometryUpdated(byte[] data) { if (data.Length < 16) return; … }
private void OnStatusUpdated(byte[] data) { if (data.Length < 6) return; … }
```
Never index into wire data without checking its length first — an out-of-range read on a truncated packet would crash the app.

### 2.4 Protocol limits (be ready to name these)

* `index` and `total` are **one byte** → max 255 points per boundary/route upload.
* 50 ms inter-packet delay → 255 points ≈ 13 s upload.
* `uptime_minutes` is `ushort` → wraps after 45.5 days.
* `battery_pct` is a byte → 0–100 fits fine.

---

## 3. The HTTP/JSON API

Base URL `http://<host>:5080`. All routes except `/healthz` require `Authorization: Bearer <token>`. Content type `application/json` both ways. Serialised by `System.Text.Json` defaults (PascalCase property names, ISO-8601 `DateTime`).

### 3.1 Endpoint reference

#### `GET /robots/active`
```json
{ "robots": [ { "robotId": "demo-robot-01", "isOnline": true,
                "lastSeenUtc": "2026-09-01T10:15:42.1234567Z",
                "latest": { "lat": 43.8563, "lon": 18.4131, "gpsAccuracyMm": 12.0, … } } ] }
```
Only robots seen within the last 5 seconds.

#### `GET /robots/{robotId}/telemetry`
```json
{ "robotId": "demo-robot-01", "isOnline": true,
  "lastSeenUtc": "2026-09-01T10:15:42.12Z",
  "latest": { "lat": 43.85631, "lon": 18.41312, "gpsAccuracyMm": 12.0, "gpsFixType": 3,
              "headingRad": 1.5708, "posX": 4.21, "posY": -1.03, "linearSpeed": 0.3,
              "accX": 0.05, "accY": 0.01, "accZ": 9.81, "gyroX": 0, "gyroY": 0, "gyroZ": 0,
              "isManualMode": false, "isMotorMoving": true,
              "batteryPct": 85, "state": 1, "bladeOn": true, "rainDetected": false,
              "uptimeMinutes": 7, "timestampUtc": "2026-09-01T10:15:42.10Z" } }
```

#### `POST /robots/{robotId}/telemetry` → `204 No Content`  *(body = `TelemetryDto`)*

#### `POST /robots/{robotId}/commands` → `202 Accepted`
```json
{ "id":"3f2b…", "kind":"Motor", "linearVel":0.35, "angularVel":-0.2, "createdUtc":"…" }
{ "id":"7c1a…", "kind":"Action", "actionName":"StartMowing", "actionCode":1, "param":0 }
{ "id":"9d4e…", "kind":"Boundary",
  "boundary": { "name":"Front lawn", "points":[ {"lat":43.8563,"lon":18.4131}, … ] } }
```

#### `GET /robots/{robotId}/commands` → `200` **and clears the queue**
```json
{ "commands": [ { … }, { … } ] }
```

#### `GET /robots/{robotId}/events?since=12`
```json
{ "events": [ { "seq":13, "type":"BoundaryPointCaptured", "xCm":1234, "yCm":-560,
                "success":false, "reason":null, "createdUtc":"…" },
              { "seq":14, "type":"CaptureEnd", "success":true } ],
  "cursor": 14 }
```

#### `POST /robots/{robotId}/events` → `200` with the stored event (including its assigned `seq`)

#### `POST /robots/{robotId}/schedules` → `200 ScheduleListResponse`
```json
{ "schedules": [ { "id":1, "activeDays":[1,3,5], "startTimeTicks":288000000000,
                   "durationMinutes":60, "isActive":true,
                   "zoneName":"Front lawn", "zoneId":2,
                   "lastExecutedUtc":"0001-01-01T00:00:00" } ] }
```
`288000000000` ticks = 8 hours (1 tick = 100 ns) → 08:00. `activeDays` uses .NET `DayOfWeek` numbering (Sunday = 0), so `[1,3,5]` is Mon/Wed/Fri.

#### `GET /robots/{robotId}/schedules` → the same shape plus `robotId`, `version`, `updatedUtc`
#### `GET /robots/{robotId}/schedules/version` → `{ "robotId":…, "version":3, "updatedUtc":… }`
#### `DELETE /robots/{robotId}/schedules/{scheduleId}` → `204` or `404`
#### `GET /healthz` → `{ "status":"ok", "utc":"…" }` *(no auth)*

### 3.2 Status codes used

| Code | When |
|---|---|
| `200 OK` | read succeeded, body returned |
| `202 Accepted` | command enqueued, not yet executed |
| `204 No Content` | write/delete succeeded, nothing to return |
| `401 Unauthorized` | missing or invalid bearer token |
| `404 Not Found` | delete of a non-existent schedule; bad route constraint |

### 3.3 Try it by hand (great for a live demo)

```bash
TOKEN="dev-token-please-replace-in-prod"
BASE="http://localhost:5080"

curl $BASE/healthz
curl -H "Authorization: Bearer $TOKEN" $BASE/robots/active
curl -H "Authorization: Bearer $TOKEN" $BASE/robots/demo-robot-01/telemetry

curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"kind":"Action","actionName":"StartMowing","actionCode":1}' \
     $BASE/robots/demo-robot-01/commands

curl -H "Authorization: Bearer $TOKEN" "$BASE/robots/demo-robot-01/events?since=0"
```
Or open `http://localhost:5080/swagger` — Swagger UI is enabled in Development and gives a clickable version of the whole API.

---

## 4. Database schemas

### 4.1 App-side SQLite — `mowit.db3` (device)

```sql
CREATE TABLE BoundaryZones (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Name      TEXT,
    CreatedAt DATETIME);

CREATE TABLE BoundaryPoints (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ZoneId    INTEGER,       -- logical FK to BoundaryZones.Id
    "Order"   INTEGER,       -- vertex sequence, 0-based  ← ESSENTIAL
    Latitude  REAL,
    Longitude REAL);

CREATE TABLE MowingSchedules (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ActiveDaysJson  TEXT,     -- "[1,3,5]"
    StartTimeTicks  INTEGER,  -- TimeSpan.Ticks
    DurationMinutes INTEGER,
    IsActive        INTEGER,  -- SQLite has no BOOL
    ZoneName        TEXT,
    LastExecuted    DATETIME,
    ZoneId          INTEGER); -- 0 means "none" (mapped to null in the domain)
```

Created by `sqlite-net-pcl` from the `[Table]`/`[PrimaryKey, AutoIncrement]` attributes in [`DatabaseEntities.cs`](MowIT/Infrastructure/Persistence/DatabaseEntities.cs). `ZoneId` in `MowingSchedules` was added later via the `ALTER TABLE … ADD COLUMN ZoneId INTEGER DEFAULT 0` migration in `AppDatabase.CreateTablesAsync`.

**Design notes to defend:**
* `BoundaryZones`/`BoundaryPoints` is a proper **1-to-many normalisation**, because the number of vertices is unbounded. The `Order` column is mandatory: SQL result order is not guaranteed, and a polygon whose vertices are shuffled is a different (and probably self-intersecting) polygon.
* `MowingSchedules.ActiveDaysJson` is **denormalised into JSON** — a small, fixed-size set that is never queried by SQL. Different data, different trade-off, and being able to explain *why the two are stored differently* is the real answer.
* There is **no declared FOREIGN KEY** on `BoundaryPoints.ZoneId`; referential integrity is enforced by the repository (`DELETE FROM BoundaryPoints WHERE ZoneId = ?` before deleting the zone). A real improvement would be to declare the FK and enable `PRAGMA foreign_keys = ON`.

### 4.2 Server-side SQLite — `schedules-dev.db`

```sql
CREATE TABLE IF NOT EXISTS Robots (
    RobotId    TEXT PRIMARY KEY,
    Version    INTEGER NOT NULL DEFAULT 0,
    UpdatedUtc TEXT    NOT NULL DEFAULT (datetime('now')));

CREATE TABLE IF NOT EXISTS Schedules (
    RobotId     TEXT    NOT NULL,
    ScheduleId  INTEGER NOT NULL,
    PayloadJson TEXT    NOT NULL,
    PRIMARY KEY (RobotId, ScheduleId),
    FOREIGN KEY (RobotId) REFERENCES Robots(RobotId) ON DELETE CASCADE);
```

Different design from the app side, on purpose: the server does not interpret schedules, it only stores and versions them per robot, so a JSON blob keyed by `(RobotId, ScheduleId)` is exactly right. The **composite primary key** means one robot's schedule 3 and another's schedule 3 coexist. `ON DELETE CASCADE` removes a robot's schedules with the robot.

### 4.3 In-memory server structures

| Store | Structure | Bound |
|---|---|---|
| `InMemoryTelemetryStore` | `ConcurrentDictionary<string, Entry(TelemetryDto, LastSeenUtc)>` | 1 entry per robot |
| `InMemoryCommandQueue` | `ConcurrentDictionary<string, List<RobotCommandDto>>` | drained every poll; motor commands coalesced |
| `InMemoryRobotEventLog` | `ConcurrentDictionary<string, Channel{long Seq; LinkedList<RobotEventDto>}>` | 200 events per robot |
| `InMemoryRobotTokenStore` | `Dictionary<string,string>` token→robotId, read-only after startup | tiny |

---

## 5. File formats written by the app

### 5.1 Session event log — `logs/session_yyyyMMdd_HHmmss.log`

```
# MowIT session started 2026-09-01T14:32:10.1234567+02:00
# time	level	source	message
14:32:11.045	=	SIM	Connecting to GreenTitan-SIM...
14:32:11.850	=	SIM	Connected to GreenTitan-SIM - GPS accuracy ramp starting at 800 mm
14:32:11.851	=	GPSTRACE	recording to gpstrace_20260901_143211.csv
14:32:11.902	i	GEOFENCE	no saved boundary - geofence idle
14:32:19.120	i	VM	user pressed Set Base  (HasGpsFix=True acc=48mm)
14:32:19.121	TX	SIM	GPS/CAPTURE/BASE
14:32:19.243	RX	SIM	GPS/CAPTURE/BASE/OK  lat=43.8563142 lon=18.4131087
14:32:19.244	=	SIM	Base datum set - local NEU frame anchored at this GPS
14:32:19.250	=	VM	HasDatum=true  baseGps=(43.8563142, 18.4131087)
…
14:35:02.771	=	VM	walked boundary saved as GPS zone "Walked 14:35" (5 pts) - geofence armed
14:41:18.004	!	GEOFENCE	robot left zone "Walked 14:35" at 43.8564901,18.4133772 - 0.84 m past the boundary, GPS +-0.01 m - stopping
```

Tab-separated, `#`-commented header, millisecond timestamps. Level tags: `TX` `RX` `i` `=` `!` `x`. Loads directly into Excel or `pandas.read_csv(sep='\t', comment='#')`.

### 5.2 GPS trace — `logs/gpstrace_yyyyMMdd_HHmmss.csv`

```csv
utc_iso,elapsed_ms,latitude,longitude,accuracy_m,fix,pos_changed,manual,moving
2026-09-01T12:32:11.8501234Z,0,43.8563100,18.4131200,0.800,Standard,1,0,0
2026-09-01T12:32:12.0503456Z,200,43.8563100,18.4131200,0.780,Standard,0,0,0
2026-09-01T12:32:12.2505678Z,400,43.8563112,18.4131234,0.760,Standard,1,1,1
```

| Column | Meaning |
|---|---|
| `utc_iso` | ISO-8601 round-trip UTC timestamp (`"O"` format) |
| `elapsed_ms` | milliseconds since the session started (`Stopwatch`) |
| `latitude` / `longitude` | 7 decimal places ≈ 1.1 cm resolution |
| `accuracy_m` | accuracy converted from mm to metres, 3 decimals |
| `fix` | `NoFix` / `Standard` / `RtkFloat` / `RtkFixed` |
| `pos_changed` | 1 if the coordinates differ from the previous written row |
| `manual` | 1 if in manual-driving mode |
| `moving` | 1 if the motors are turning |

**Analysis ideas for your report** (this file is your experimental evidence):
* Plot `latitude` vs `longitude` → the actual path driven; overlay the boundary polygon.
* Plot `accuracy_m` vs `elapsed_ms` → the RTK convergence curve.
* `pos_changed` duty cycle → the effective GPS update rate versus the polling rate.
* Compute inter-sample distances → speed profile; compare against the commanded velocities in the session log.

---

## 6. Preferences (key/value on the device)

| Key | Type | Written by | Read by |
|---|---|---|---|
| `profile_name` | string | `LoginViewModel.ContinueAsync` (if *Remember me*) | `LoginViewModel` ctor, `DashboardViewModel.Greeting` |
| `mowit.last_mow_at_ticks` | long (UTC ticks) | `LastMowSession.MarkMowedNow` | `LastMowSession.LastMowAtLocal` |

Stored via MAUI `Preferences.Default`, which maps to `SharedPreferences` on Android, `NSUserDefaults` on iOS/Mac and the registry on Windows.
