# 06 — THE INFRASTRUCTURE LAYER

`MowIT/Infrastructure/` — ring 3. **All the machinery.** Every class here implements an interface defined in `Domain/`. This is the only ring allowed to know about Bluetooth stacks, sockets, SQLite and HTTP.

---

## 1. The four factories — how a transport stack is assembled

[`Infrastructure/Factories/`](MowIT/Infrastructure/Factories/) — the **Abstract Factory** pattern. Each factory knows how to register one complete transport into the DI container. `MauiProgram` picks exactly one:

```csharp
private static readonly IRobotServiceFactory RobotFactory = new MultiTransportFactory();
```

### 1.1 `SimulatorServiceFactory` — [file](MowIT/Infrastructure/Factories/SimulatorServiceFactory.cs) — *simulator only*

```csharp
services.AddSingleton<SimulatedRobotService>();

services.AddSingleton<IRobotScanner>   (sp => sp.GetRequiredService<SimulatedRobotService>());
services.AddSingleton<IRobotConnection>(sp => sp.GetRequiredService<SimulatedRobotService>());
services.AddSingleton<IRobotSensors>   (sp => sp.GetRequiredService<SimulatedRobotService>());
services.AddSingleton<IRobotBoundary>  (sp => sp.GetRequiredService<SimulatedRobotService>());

services.AddSingleton<RobotStateMachine>();
services.AddSingleton<IRobotControl>(sp => {
    var sim = sp.GetRequiredService<SimulatedRobotService>();
    …
    stateMachine.Start(sensors);
    IRobotControl inner = new ConnectionGuardProxy(new RetryDecorator(sim), connection);
    return new CommandPipeline(inner, sensors, stateMachine, logger);
});
```

**The critical detail (explained in `03-ARCHITECTURE.md §8`):** the four interface registrations all resolve *the same* `SimulatedRobotService` instance via `sp => sp.GetRequiredService<…>()`. Writing `AddSingleton<IRobotScanner, SimulatedRobotService>()` four times would create four separate robots. This idiom is the correct multi-interface singleton registration.

### 1.2 `GreenTitanSppFactory` — identical shape, `GreenTitanSppService` instead. Also injects `EventLogService` so the SPP layer can log every TX/RX byte-level message.

### 1.3 `GreenTitanServiceFactory` (BLE) — identical shape, but constructs the service from the Plugin.BLE singletons:
```csharp
new GreenTitanBleService(CrossBluetoothLE.Current, CrossBluetoothLE.Current.Adapter, logger)
```

### 1.4 `MultiTransportFactory` — [file](MowIT/Infrastructure/Factories/MultiTransportFactory.cs) — **the active one**

```csharp
services.AddSingleton<SimulatedRobotService>();
services.AddSingleton<WifiRobotService>();

services.AddSingleton<RobotTransportRouter>(sp => new RobotTransportRouter(
    new Dictionary<TransportKind, IRobotTransport> {
        [TransportKind.Bluetooth] = sp.GetRequiredService<SimulatedRobotService>(),
        [TransportKind.Wifi]      = sp.GetRequiredService<WifiRobotService>(),
    },
    TransportKind.Bluetooth, evt));

services.AddSingleton<IRobotTransportSwitch>(sp => sp.GetRequiredService<RobotTransportRouter>());
services.AddSingleton<IRobotScanner>   (sp => sp.GetRequiredService<RobotTransportRouter>());
… (the router is registered under all five interfaces) …
```

Two things happen here that do not happen in the other factories:
* The **router**, not a transport, is registered under the five interfaces — so everything above talks to the router.
* `IRobotTransportSwitch` is re-registered, **overriding** the `NullRobotTransportSwitch` registered earlier in `MauiProgram`, which is what makes the Bluetooth/WiFi toggle on the Scan page functional.

Additionally, `MauiProgram` checks `if (RobotFactory is MultiTransportFactory)` and swaps `NullScheduleSyncService` for the real `HttpScheduleSyncService`.

---

## 2. Classic Bluetooth SPP — `GreenTitanSppService`

[`Infrastructure/ClassicBt/GreenTitanSppService.cs`](MowIT/Infrastructure/ClassicBt/GreenTitanSppService.cs) — 930 lines, the largest and most "real-world" class in the project. This is the one that talks to actual hardware.

### 2.1 What SPP is

*Serial Port Profile* makes a Bluetooth link behave exactly like an old RS-232 cable: an in-stream and an out-stream of bytes, nothing else. No packets, no framing, no addressing. Everything — message boundaries, acknowledgements, error handling — has to be built on top. The well-known UUID `00001101-0000-1000-8000-00805F9B34FB` identifies it.

### 2.2 Platform-specific connect

The class is compiled differently per platform with `#if ANDROID` / `#elif WINDOWS`.

**Android:**
```csharp
adapter.CancelDiscovery();                                   // discovery ruins throughput — always stop it first
var btDevice = adapter.GetRemoteDevice(GuidToMac(device.Id));
_socket = btDevice.CreateRfcommSocketToServiceRecord(UUID.FromString(SppUuid));
await Task.Run(() => _socket.Connect(), ct);                 // Connect() is blocking → push to a worker thread
_writer    = new StreamWriter(_socket.OutputStream!, Encoding.ASCII) { AutoFlush = true };
_rawStream = _socket.InputStream!;
```

**Windows (WinRT):**
```csharp
var rfcommService = await ResolveRfcommServiceAsync(winId, ct);
var access = await rfcommService.RequestAccessAsync();       // Windows privacy gate
if (access != DeviceAccessStatus.Allowed) throw new Exception("… Settings > Privacy > Bluetooth.");
_winSocket = new StreamSocket();
await _winSocket.ConnectAsync(rfcommService.ConnectionHostName, rfcommService.ConnectionServiceName,
                              SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
```

`ResolveRfcommServiceAsync` is a **two-strategy fallback**: try the cached RFCOMM service id first, and if that throws or returns null, look the device up by id and enumerate its RFCOMM services with `BluetoothCacheMode.Cached` and then `Uncached`. That is exactly the sort of defensive code you only write after fighting a real Windows Bluetooth stack — good to mention.

**MAC ↔ Guid packing (Android):** `MowerDevice.Id` is a `Guid`, but Android identifies devices by MAC string. So:
```csharp
MacToGuid("AA:BB:CC:DD:EE:FF") → Guid "aabbccdd-eeff-0000-0000-000000000000"
GuidToMac(g) → returns null unless the last 12 hex digits are all zero (i.e. it really is a packed MAC)
```
A neat, reversible encoding that keeps the domain model platform-independent.

### 2.3 Scanning

* **Android:** first emits every **bonded (paired)** device immediately, then registers a `BtDiscoveryReceiver` (a `BroadcastReceiver` listening for `BluetoothDevice.ActionFound`), calls `StartDiscovery()`, waits 15 s, and stops.
* **Windows:** enumerates `RfcommDeviceService.GetDeviceSelector(SerialPort)` **and** paired `BluetoothDevice`s, merges the two lists **by device name** into a dictionary so a device that appears in both is one row with both ids cached in `_winDeviceIds`.

### 2.4 The framing problem — the cleverest 20 lines in the project

The GreenTitan firmware terminates commands *sent to it* with `<`, but its replies are **not reliably newline-terminated**; several replies can arrive glued together in one TCP-like read. So:

```csharp
private static readonly string[] _msgPrefixes = ["GPS/", "MOWER/"];

private static int FindMessageSplit(string text) {
    for (int i = 0; i < text.Length; i++)
        if (text[i] == '\n' || text[i] == '\r') return i + 1;   // 1) prefer a real line break

    foreach (var prefix in _msgPrefixes) {                       // 2) otherwise, split where the
        if (text.Length <= prefix.Length) continue;              //    NEXT message obviously begins
        int idx = text.IndexOf(prefix, 1, StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return idx;
    }
    return -1;                                                   // 3) incomplete — keep buffering
}
```

**Plain:** *"Cut at a newline if there is one. If not, cut just before the next `GPS/` or `MOWER/` — because that can only be the start of a new message. If neither, we have not received a whole message yet, so wait for more bytes."* The `IndexOf(prefix, 1, …)` starts at index **1** so it does not match the prefix of the current message.

`FlushPending` loops on this until nothing more can be split, then calls `ParseLine` on each complete message.

### 2.5 The read loop

```csharp
_readCts  = new CancellationTokenSource();
_readTask = Task.Run(() => ReadLoop(_readCts.Token));
```

`ReadLoop` runs on a background thread doing **blocking** `Stream.Read` into a 512-byte buffer. Cancellation cannot interrupt a blocking read, so:

```csharp
ct.Register(() => { try { _socket?.Close(); } catch { } });   // closing the socket forces Read to throw
…
catch when (ct.IsCancellationRequested) { }                   // expected → swallow
catch (Exception ex) {                                        // unexpected → the link died
    _evt.Error(Source, $"Read loop dropped unexpectedly: {ex.Message}");
    CurrentState = RobotConnectionState.Disconnected;
    _stateSubject.OnNext(RobotConnectionState.Disconnected);
    WeakReferenceMessenger.Default.Send(new RobotDisconnectedMessage(RobotDeviceName));
}
```

The `catch when (ct.IsCancellationRequested)` **exception filter** is the idiomatic way to distinguish "I closed it" from "it broke". Worth pointing out — it is a C# feature many students never use.

### 2.6 Polling timers

```csharp
_gpsTimer      = new Timer(_ => FireAndForget("GPS/GET/POS"),      null, 1_000,  500);   // 2 Hz
_accuracyTimer = new Timer(_ => FireAndForget("GPS/GET/ACCURACY"), null, 2_000, 2_000);  // 0.5 Hz
```

The firmware does not push telemetry; the app has to ask. Position is asked twice a second (it changes constantly); accuracy every two seconds (it changes slowly). Choosing two different rates instead of one is a deliberate bandwidth decision on a slow serial link.

### 2.7 Link-quality measurement — a genuinely nice feature

The service **measures its own latency**. Every outgoing command is stamped, and every reply is correlated back:

```csharp
private static string CorrelationKey(string message, bool outgoing) {
    var p = message.Split('/');
    if (outgoing && p[0]=="GPS" && p[1]=="GET") return $"GPS/{p[2]}";  // GPS/GET/POS  ↔ GPS/POS/…
    if (p[0]=="MOWER" && p[1]=="MANUAL")        return "MOWER/MANUAL"; // ON/OFF share one key
    if (p[1]=="CAPTURE")                        return $"{p[0]}/CAPTURE/{p[2]}";
    return $"{p[0]}/{p[1]}";
}
```

*Plain:* it reduces both the request and the response to the same short key so they can be matched in a dictionary. `GPS/GET/POS` → key `GPS/POS`; the reply `GPS/POS/43.85,18.41` → key `GPS/POS`. Match found → latency = now − sent-at.

Telemetry polls are excluded from the statistics (they would dominate the sample), and on disconnect:

```csharp
_evt.State("BT", $"link stats: sent={ControlCommandsSent} acked={ControlRepliesSeen} ({pct:F1}%), " +
                 $"latency avg={avg:F0} ms min={min} ms max={max} ms (n={n})");
```

**This is measurable, quotable evidence for your report.** "Over a 12-minute session, 143 control commands were sent, 141 acknowledged (98.6 %), mean round-trip 87 ms, max 412 ms." That is a result, not an opinion.

### 2.8 The manual-mode dance and automatic recovery

The firmware ignores `MOWER/MOVE` unless it is in manual mode.

```csharp
public async Task SendMotorCommandAsync(float lin, float ang) {
    if (!IsConnected) return;
    _lastLinVel = lin; _lastAngVel = ang;              // remember for a possible replay
    if (!_manualMode) { SendCommand("MOWER/MANUAL/ON"); await WaitForManualModeAsync(500); }
    SendMoveCommand(lin, ang);
}
```

If a `MOWER/MOVE/FAIL/NOT_MANUAL` still comes back (a race, or the firmware dropped out of manual mode by itself):

```csharp
private async Task RecoverManualModeAsync() {
    if (Interlocked.CompareExchange(ref _manualRecoveryBusy, 1, 0) != 0) return;   // re-entrancy guard
    try {
        if (|lin| < 0.001 && |ang| < 0.001) return;         // don't resurrect a stop
        _evt.Warn("BT", "MOVE rejected (NOT_MANUAL) - re-enabling manual mode and repeating the move");
        SendCommand("MOWER/MANUAL/ON");
        if (!await WaitForManualModeAsync(500)) { _evt.Error("BT", "manual mode was not confirmed - move not repeated"); return; }
        SendMoveCommand(lin, ang);
    } finally { Volatile.Write(ref _manualRecoveryBusy, 0); }
}
```

`Interlocked.CompareExchange(ref flag, 1, 0)` is a lock-free "only one recovery at a time" gate — it atomically sets the flag to 1 **only if** it was 0, and returns the old value. Because MOVE failures can arrive in bursts, without this you would fire a storm of recoveries. `Volatile.Write` on release ensures other threads see the reset.

### 2.9 Fix-type inference

The GreenTitan firmware reports an accuracy number but not a fix class, so the app derives it:

```csharp
if (accuracyMm <= 0) return NoFix;
if (lat == 0 && lon == 0) return NoFix;
if (accuracyMm <=  50) return RtkFixed;    // ≤ 5 cm
if (accuracyMm <= 500) return RtkFloat;    // ≤ 50 cm
return Standard;
```

### 2.10 What SPP deliberately does **not** support

```csharp
public Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? p = null)
    => throw new NotSupportedException(
        "GreenTitan does not support uploading GPS boundary coordinates. " +
        "Walk the perimeter using BoundaryRecordStart / BoundaryCapturePoint / BoundaryRecordEnd.");
```

The real firmware has no "here is a polygon" command — the boundary must be *walked*. Throwing `NotSupportedException` with an explanatory message is the honest implementation of the interface (as opposed to silently doing nothing). This is a legitimate Liskov discussion point: the interface promises more than one implementation can deliver, and the mitigation is that the UI paths that call it are only reachable in the transports that support it.

---

## 3. Bluetooth Low Energy — `GreenTitanBleService`

[`Infrastructure/Ble/GreenTitanBleService.cs`](MowIT/Infrastructure/Ble/GreenTitanBleService.cs)

### 3.1 The GATT profile — [`BleGattProfile.cs`](MowIT/Infrastructure/Ble/BleGattProfile.cs)

| UUID (short) | Name | Direction |
|---|---|---|
| `0x1234` | Service | — |
| `0x1235` | GPS data | robot → app (notify) |
| `0x1236` | IMU data | robot → app (notify) |
| `0x1237` | Odometry | robot → app (notify) |
| `0x1238` | Robot status | robot → app (notify) |
| `0x1240` | Motor command | app → robot (write **without response**) |
| `0x1241` | Action command | app → robot (write) |
| `0x1242` | Boundary chunk | app → robot (write) |
| `0x1243` | Schedule data | app → robot (declared, **never written**) |

All are 16-bit UUIDs expanded into the Bluetooth SIG base UUID `0000xxxx-0000-1000-8000-00805f9b34fb`.

### 3.2 Connect sequence

```csharp
_device = await _adapter.ConnectToKnownDeviceAsync(id, ct);
await _device.RequestMtuAsync(512);                           // bigger packets = fewer round trips
_service = await _device.GetServiceAsync(ServiceUuid, ct) ?? throw …;
await DiscoverCharacteristicsAsync(ct);
await StartNotificationsAsync();                              // subscribe to the four telemetry chars
```

`_motorChar.WriteType = CharacteristicWriteType.WithoutResponse;` — **the single most important line for joystick feel.** A normal BLE write waits for an acknowledgement (~30 ms round trip); write-without-response is fire-and-forget and lets the app push motor updates at 10 Hz. Losing one motor packet is harmless because the next one arrives 100 ms later. Action commands, by contrast, use the acknowledged write because losing a `StartMowing` matters.

### 3.3 Binary serialisation — [`BlePacketSerializer.cs`](MowIT/Infrastructure/Ble/BlePacketSerializer.cs)

| Packet | Layout | Size |
|---|---|---|
| GPS | `double lat` @0, `double lon` @8, `float accMm` @16, `byte fix` @20 | 21 B |
| IMU | 6 × `float` (AccX/Y/Z, GyroX/Y/Z) | 24 B |
| Odometry | `float PosX, PosY, HeadingRad, LinearSpeed` | 16 B |
| Status | `byte state, byte battery, byte blade, byte rain, ushort uptime` | 6 B |
| Motor cmd | `float linear` @0, `float angular` @4 | 8 B |
| Action cmd | `byte action, byte param` | 2 B |
| Boundary chunk | `byte index, byte total, byte pointType, byte pad, double lat, double lon` | 20 B |

Every handler validates the length first (`if (data.Length < 21) return;`) — never trust the wire.

**The `record with` merge trick.** Three separate characteristics each carry a different *part* of one `SensorSnapshot`. Because the entity is a record:

```csharp
public static SensorSnapshot MergeImu(SensorSnapshot existing, byte[] data) => existing with {
    AccX = BitConverter.ToSingle(data,0), … };
```

Each notification produces a **new immutable snapshot** that keeps the other fields, and it is pushed onto the stream. Elegant, allocation-light, and thread-safe by construction.

**A subtlety in `OnGpsUpdated`:** the GPS packet creates a *fresh* snapshot, so the code explicitly copies the IMU and odometry fields forward:
```csharp
_latestSensor = snap with { AccX=_latestSensor.AccX, …, HeadingRad=_latestSensor.HeadingRad, … };
```

**`SerializeSchedule`** packs the days into a bit-mask: `daysMask |= (byte)(1 << ((int)d + 6) % 7);` — `DayOfWeek.Sunday` is 0 in .NET, so `+6 % 7` rotates the week to make **Monday = bit 0**, matching the firmware convention. This method is written but currently unused (the schedule characteristic is discovered but never written).

### 3.4 Boundary upload

```csharp
for (int i = 0; i < points.Count; i++) {
    var chunk = SerializeBoundaryChunk((byte)i, (byte)points.Count, pointType, points[i]);
    await _boundaryChar.WriteAsync(chunk);
    await Task.Delay(50);                                   // don't overrun the firmware's buffer
    progress?.Report((i + 1) * 100 / points.Count);
}
```
`pointType` is `0` for a boundary polygon, `1` for a route. **Limitation:** `index` and `total` are single bytes, so the protocol caps at 255 points per upload — combined with the 50 ms delay this is exactly why `AdaptiveSpacing` limits routes to ~2500 waypoints and why a real product would need a 16-bit index.

### 3.5 The two `IRobotControl` wrappers

```csharp
// ConnectionGuardProxy — Proxy pattern
public Task SendMotorCommandAsync(float lin, float ang) {
    if (!_connection.IsConnected) return Task.CompletedTask;   // silently drop
    return _inner.SendMotorCommandAsync(lin, ang);
}

// RetryDecorator — Decorator pattern
public async Task SendActionAsync(RobotAction a, byte p = 0) {
    for (int attempt = 0; attempt < 3; attempt++) {
        try { await _inner.SendActionAsync(a, p); return; }
        catch when (attempt < 2) { await Task.Delay(100); }
    }
}
public Task SendMotorCommandAsync(float lin, float ang) => _inner.SendMotorCommandAsync(lin, ang);  // NO retry
```

**Why motor commands are not retried:** a joystick command is only valid *right now*. Re-sending a 300 ms-old velocity would make the robot lurch. The next command is 100 ms away anyway. Actions (`StartMowing`, `Stop`) are idempotent, discrete and important — those get three attempts. That asymmetry is a deliberate design decision and a great thing to be able to justify.

---

## 4. WiFi / cloud — `WifiRobotService`

[`Infrastructure/Wifi/WifiRobotService.cs`](MowIT/Infrastructure/Wifi/WifiRobotService.cs)

### 4.1 Discovery with URL probing

```csharp
foreach (var candidate in _opts.CandidateBaseUrls) {
    try { resp = await _http.GetAsync($"{candidate}/robots/active", ct); }
    catch (Exception ex) { _evt.Warn("WIFI", $"{candidate} unreachable: {ex.Message}");
                           reason = "Can't reach the server. Tried: …"; continue; }
    _base = candidate;                    // remember the one that answered
    if (resp.IsSuccessStatusCode)      { …emit a MowerDevice per online robot… }
    else if (resp.StatusCode == 401)   { reason = "Server rejected the token …"; }
    else                               { reason = $"Server returned {(int)resp.StatusCode} …"; }
    break;
}
```

Note the quality of the **failure messages** — they name the URLs tried and tell the user what to start. That is user-facing engineering, not just error handling. Failures are surfaced as `RobotErrorMessage("WIFI/…")`, and `ScanViewModel` strips the `WIFI/` prefix and shows the rest in the red banner.

`DeviceIdFor(robotId)` = `new Guid(MD5(robotId))` — a **deterministic** id so repeated scans do not create duplicate list rows. (MD5 here is used purely as a hash-to-Guid function, not for security.)

### 4.2 The polling loop

```csharp
while (!ct.IsCancellationRequested) {
    var envelope = await _http.GetFromJsonAsync<TelemetryEnvelope>(TelemetryUrl, ct);
    if (envelope is null || !envelope.IsOnline || envelope.Latest is null) {
        if (++failures >= 6) { _evt.Warn("WIFI","robot offline - dropping connection"); break; }
    } else {
        failures = 0;
        PublishTelemetry(envelope.Latest);    // → _sensorSubject + _statusSubject
        await PollEvents(ct);                 // → messenger
    }
    await Task.Delay(_opts.PollInterval, ct); // 500 ms
}
if (!ct.IsCancellationRequested) await DisconnectAsync();
```

**`MaxConsecutiveFailures = 6` × 500 ms ≈ 3 seconds of silence before giving up** — long enough to survive a WiFi hiccup, short enough that the user notices quickly. The counter resets on any success, so intermittent failures never accumulate into a false disconnect.

### 4.3 The event cursor — exactly-once delivery

```csharp
var resp = await _http.GetFromJsonAsync<EventListResponse>($"{EventsUrl}?since={_eventCursor}", ct);
_eventCursor = resp.Cursor;
foreach (var evt in resp.Events) DispatchEvent(evt);
```

The server assigns each event a monotonically increasing `Seq`. The client remembers the highest one it has seen and asks only for newer ones. `_eventCursor = 0` is reset on every connect. **This is how a polling client gets reliable, no-duplicate, no-loss event delivery** — the same idea as a Kafka offset or a database change-feed cursor. Explaining this well is a strong moment in a defence.

`DispatchEvent` maps each `RobotEventDto.Type` string onto exactly the same messenger messages the Bluetooth transports send — which is why the entire UI works unchanged over WiFi.

### 4.4 Sending

```csharp
await _http.PostAsJsonAsync(CommandsUrl, new RobotCommandDto { Kind = CommandKinds.Motor,
                                                               LinearVel = lin, AngularVel = ang });
```
Motor, Action and Boundary all become one `RobotCommandDto` with a `Kind` discriminator. `SendRouteAsync` simply calls `SendBoundaryAsync` with a temporary zone. Failures are logged, never thrown — a dropped joystick command must not crash the UI.

`HttpClient` is created with `Timeout = 4 s` and a permanent `Authorization: Bearer <token>` header.

---

## 5. The simulator — `SimulatedRobotService`

[`Infrastructure/Simulator/SimulatedRobotService.cs`](MowIT/Infrastructure/Simulator/SimulatedRobotService.cs) — 616 lines. **This is not a stub; it is a physics-and-protocol emulator**, and it is what makes the whole project demonstrable and testable without hardware.

### 5.1 The tick loop

```csharp
_sensorTimer = Observable.Interval(TimeSpan.FromMilliseconds(200)).Subscribe(_ => EmitSensorData());
```
5 Hz, `TickSeconds = 0.2`.

### 5.2 What it models

**(a) Motor inertia.** Velocity does not jump to the commanded value:
```csharp
private static float ApproachLinear(float current, float target, float step) {
    float diff = target - current;
    return Math.Abs(diff) <= step ? target : current + Math.Sign(diff) * step;
}
_actualLinear  = ApproachLinear(_actualLinear,  targetLin, 0.8f * 0.2f);   // 0.8 m/s²
_actualAngular = ApproachLinear(_actualAngular, targetAng, 3.0f * 0.2f);   // 3.0 rad/s²
```
A first-order rate limiter → the robot accelerates and decelerates instead of teleporting, which makes UI latency and smoothing behave realistically.

**(b) Dead reckoning into GPS.**
```csharp
_heading += _actualAngular * dt;
double dist = linearMs * dt;
_lat += dist * cos(heading) / 111319.444;
_lon += dist * sin(heading) / 111319.444 / cos(lat·π/180);
```
The `/cos(lat)` term is the longitude-shrink correction. Start position `43.8563, 18.4131` — Sarajevo.

**(c) A GPS accuracy ramp.**
```csharp
_accMm = max(12, 800 − 100 · secondsSinceConnect);
```
Starts at 800 mm and improves at 100 mm/s to a 12 mm floor — mimicking an RTK receiver converging. **This is why `Set Base` fails for the first ~8 seconds** (the base needs < 50 mm), which forces you to demonstrate and explain the accuracy gate rather than hiding it.

**(d) A manual-mode watchdog.**
```csharp
if (_manualMode && (now - _lastMoveCmdAt).TotalMilliseconds > 600) { _targetLinear = 0; _targetAngular = 0; }
```
Exactly like real robot firmware: if commands stop arriving, stop the motors. **This is why `ControlViewModel` sends a keep-alive every 250 ms while the joystick is held.**

**(e) The full capture state machine** — datum, active polygon, closed polygons, exit point, path-saved flag — with realistic failure codes:

| Situation | Reply |
|---|---|
| `CaptureBase` with accuracy > 50 mm | `GPS/CAPTURE/BASE/FAIL/ACCURACY (have 612 mm, need < 50 mm)` |
| `CapturePoint` before a base is set | `MOWER/CAPTURE/POINT/FAIL/NO_DATUM` |
| `CaptureOutline` with < 3 points | `MOWER/CAPTURE/OUTLINE/FAIL/TOO_FEW_POINTS (have 2, need ≥ 3)` |
| `RecordEnd` with no closed polygon | `MOWER/CAPTURE/END/FAIL/NO_POLYGON` |
| `RecordEnd` with no exit point | `MOWER/CAPTURE/END/FAIL/NO_EXIT` |
| `StartMowing` with no datum | `MOWER/START/FAIL/NO_DATUM` |
| `StartMowing` with no saved path | `MOWER/START/FAIL/NO_PATH` |

**(f) Route following.**
```csharp
if (current.DistanceTo(target) < 0.30) { _routeIdx++; return; }
_heading = current.BearingTo(target) · π/180;
_actualLinear = 0.3f;  StepGps(...);
```
Point-and-shoot navigation at 0.3 m/s with a 30 cm waypoint-reached radius. When the last waypoint is passed it returns to `Idle` and logs "Route complete".

**(g) Protocol echo.** Every action is logged as the exact ASCII string the real firmware would use (`LogTx("MOWER/CAPTURE/POINT")` / `LogRx("MOWER/CAPTURE/POINT/OK/1234,-560")`) and each carries an artificial `await Task.Delay(120)` to imitate link latency. Watching the event log during a simulator demo is indistinguishable from watching a real session — which is exactly the point.

---

## 6. The transport router — `RobotTransportRouter`

[`Infrastructure/Transport/RobotTransportRouter.cs`](MowIT/Infrastructure/Transport/RobotTransportRouter.cs)

**Plain:** a switchboard that implements all five robot interfaces and forwards every call to whichever transport is currently selected.

```csharp
private readonly BehaviorSubject<IRobotTransport> _active;
private readonly BehaviorSubject<TransportKind>   _kind;

public async Task SelectAsync(TransportKind kind) {
    if (kind == _kind.Value) return;
    if (!_transports.TryGetValue(kind, out var next)) return;
    try { await Active.DisconnectAsync(); } catch { }     // always leave the old link cleanly
    _active.OnNext(next);  _kind.OnNext(kind);
    _evt.State("ROUTE", $"transport to {kind}");
}
```

The elegant part is how the **streams** are forwarded:

```csharp
public IObservable<SensorSnapshot> SensorStream => _active.Select(t => t.SensorStream).Switch();
```

**Read that carefully — it is the best single line in the codebase to explain in a defence.**
`_active` is a stream *of transports*. `Select(t => t.SensorStream)` turns it into a stream *of streams*. `Switch()` flattens it by always subscribing to the **most recent inner stream** and automatically unsubscribing from the previous one.

Consequence: a ViewModel subscribes **once, at construction**, and keeps receiving data across any number of transport switches — with no manual unsubscribe/resubscribe code anywhere. Doing this with plain events would require every consumer to know that transports can change.

`KindChanges => _kind.DistinctUntilChanged()` similarly gives a de-duplicated stream of the current transport.

`NullRobotTransportSwitch` ([file](MowIT/Infrastructure/Transport/NullRobotTransportSwitch.cs)) is the **Null Object** used when only one transport exists: `CurrentKind => Bluetooth`, `SelectAsync => Task.CompletedTask`. The Scan page can therefore always inject `IRobotTransportSwitch` without null checks.

---

## 7. Persistence — SQLite on the device

### 7.1 `AppDatabase` — [file](MowIT/Infrastructure/Persistence/AppDatabase.cs)

```csharp
public AppDatabase(string dbPath) {
    if (File.Exists(dbPath) && !HasValidSQLiteHeader(dbPath)) File.Delete(dbPath);   // corruption guard
    _db = new SQLiteAsyncConnection(dbPath);
}
private static bool HasValidSQLiteHeader(string path) {
    Span<byte> header = stackalloc byte[16];
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    return fs.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
}
```

**Plain:** every SQLite file starts with the 16 ASCII bytes `SQLite format 3\0`. If the file on disk does not, it is not a database (an interrupted install, a partially-written file) — delete it and start fresh rather than crashing on every query.

`stackalloc` + `"…"u8` (UTF-8 literal, C# 11) means this check performs **zero heap allocations**.

**Thread-safe lazy initialisation** — the classic double-checked locking pattern with an async lock:

```csharp
private volatile bool _initialized;
private readonly SemaphoreSlim _initLock = new(1, 1);

public async Task InitializeAsync() {
    if (_initialized) return;                 // fast path, no lock
    await _initLock.WaitAsync();
    try {
        if (_initialized) return;             // re-check inside the lock
        try { await CreateTablesAsync(); }
        catch (SQLiteException) {             // schema mismatch → nuke and rebuild
            await _db.CloseAsync();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            _db = new SQLiteAsyncConnection(_dbPath);
            await CreateTablesAsync();
        }
        _initialized = true;
    } finally { _initLock.Release(); }
}
```

`SemaphoreSlim` rather than `lock` because you cannot `await` inside a `lock`. `volatile` guarantees the fast path sees the flag across threads. Every repository method starts with `await _db.InitializeAsync();`, so initialisation is guaranteed exactly once no matter which screen loads first.

**Migration:**
```csharp
try { await _db.ExecuteAsync("ALTER TABLE MowingSchedules ADD COLUMN ZoneId INTEGER DEFAULT 0"); } catch { }
```
A pragmatic one-step migration: try to add the column; if it already exists SQLite throws and the empty catch swallows it. Fine for a student project; a production app would use a `PRAGMA user_version` migration ladder — say so if asked.

### 7.2 Schema — [`DatabaseEntities.cs`](MowIT/Infrastructure/Persistence/DatabaseEntities.cs)

```
BoundaryZones (Id PK AUTOINCREMENT, Name TEXT, CreatedAt DATETIME)
BoundaryPoints(Id PK AUTOINCREMENT, ZoneId INT, Order INT, Latitude REAL, Longitude REAL)
MowingSchedules(Id PK AUTOINCREMENT, ActiveDaysJson TEXT, StartTimeTicks INT,
                DurationMinutes INT, IsActive BOOL, ZoneName TEXT, LastExecuted DATETIME, ZoneId INT)
```

Note the **separation of `BoundaryZoneEntity` from `BoundaryZone`**. The database entity is flat (SQLite has no arrays); the domain entity has a `List<GpsPoint>`. The repository is the mapper. That separation is what keeps `System.Data`/SQLite attributes out of the Domain layer.

### 7.3 `BoundaryRepository` — [file](MowIT/Infrastructure/Persistence/BoundaryRepository.cs)

`GetAllAsync` loads both tables and joins in memory:
```csharp
Points = points.Where(p => p.ZoneId == z.Id).OrderBy(p => p.Order).Select(p => new GpsPoint(p.Latitude, p.Longitude)).ToList()
```
The `Order` column is essential — a polygon's vertices are meaningless without their sequence, and SQL rows have no inherent order.

`SaveAsync` uses **delete-then-insert** for the points:
```csharp
if (zone.Id == 0) { await InsertAsync(entity); zone.Id = entity.Id; }   // new: capture the generated id
else              { await UpdateAsync(entity); }
await ExecuteAsync("DELETE FROM BoundaryPoints WHERE ZoneId = ?", zone.Id);
for (int i = 0; i < zone.Points.Count; i++) await InsertAsync(new BoundaryPointEntity { ZoneId=…, Order=i, … });
```
Simpler and safer than diffing (which vertex moved? was one inserted in the middle?), at the cost of extra writes. Correct choice for tens of points.

*Improvement to mention:* the inserts are not wrapped in a transaction, so a crash mid-save could leave a zone with partial points. `RunInTransactionAsync` would fix it.

### 7.4 `ScheduleRepository` — [file](MowIT/Infrastructure/Persistence/ScheduleRepository.cs)

`DayOfWeek[]` cannot be stored in a SQLite column, so it is JSON-serialised:
```csharp
ActiveDaysJson = JsonSerializer.Serialize(m.ActiveDays.Select(d => (int)d).ToArray());   // "[1,3,5]"
ActiveDays     = JsonSerializer.Deserialize<int[]>(e.ActiveDaysJson)!.Select(d => (DayOfWeek)d).ToArray();
```
`TimeSpan` is stored as `Ticks` (an `INTEGER`), and `ZoneId` uses `0 ⇄ null` mapping because the column is non-nullable.

---

## 8. Schedule sync to the cloud

### 8.1 `SyncingScheduleRepository` — the Decorator

[file](MowIT/Infrastructure/ScheduleSync/SyncingScheduleRepository.cs)

```csharp
public async Task SaveAsync(MowingSchedule schedule) {
    await _inner.SaveAsync(schedule);              // 1. local SQLite first — this must succeed
    if (!_sync.IsEnabled) return;
    var all = await _inner.GetAllAsync();
    _ = _sync.PushAsync(all);                      // 2. cloud push, fire-and-forget
}
```

**Local-first, cloud-best-effort.** The user's data is safe on the device even with no network, and the UI never blocks on an HTTP call. `GetAllAsync`/`GetByIdAsync` pass straight through. The whole cloud feature is added **without changing `ScheduleRepository` or any ViewModel** — the textbook payoff of the Decorator pattern.

It pushes **the whole list**, not the delta, matching the server's `ReplaceAsync` (full replace + version bump) — a simple last-writer-wins sync that avoids merge conflicts entirely.

### 8.2 `HttpScheduleSyncService` — [file](MowIT/Infrastructure/ScheduleSync/HttpScheduleSyncService.cs)

```csharp
public bool IsEnabled => _opts.IsConfigured;   // BaseUrl && RobotId && BearerToken all non-blank
await _http.PostAsJsonAsync($"/robots/{Uri.EscapeDataString(_opts.RobotId)}/schedules", payload, ct);
await _http.DeleteAsync($"/robots/{…}/schedules/{scheduleId}", ct);
```
Note `Uri.EscapeDataString` on the robot id — correct URL encoding, not string concatenation. A `404` on delete is treated as success (the schedule is gone either way — idempotent delete). Every failure is logged to the `EventLogService` under the `SYNC` tag and never thrown.

### 8.3 `NullScheduleSyncService` — the Null Object. `IsEnabled => false`, both methods `Task.CompletedTask`. Used whenever the app is not in `MultiTransportFactory` mode, so the decorator can be installed unconditionally.

---

## 9. `MowingSchedulerService` — the automatic mowing timer

[`Infrastructure/Services/MowingSchedulerService.cs`](MowIT/Infrastructure/Services/MowingSchedulerService.cs)

```csharp
_timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
```
First tick 5 s after startup (let the app settle), then every 20 s.

```csharp
private async Task TickAsync() {
    if (_ticking) return;                                   // re-entrancy guard
    _ticking = true;
    try {
        var now = DateTime.Now;
        foreach (var s in await _repo.GetAllAsync()) {
            if (!s.IsActive) continue;
            if (!s.ActiveDays.Contains(now.DayOfWeek)) continue;

            var diff = now.TimeOfDay - s.StartTime;
            if (diff.TotalMinutes is < 0 or >= 1) continue;              // ① a 1-minute firing window

            if (s.LastExecuted.Date == now.Date
                && s.LastExecuted.TimeOfDay >= s.StartTime) continue;    // ② already ran today

            if (!_connection.IsConnected) { _logger.LogWarning("Scheduled mow skipped - robot not connected …"); continue; }

            if (s.ZoneId.HasValue) await _sendZone.ExecuteAsync(s.ZoneId.Value, startMowingAfter: true);
            else                   await _sendZone.StartMowingOnlyAsync();

            s.LastExecuted = now;  await _repo.SaveAsync(s);             // ③ persist so ② works
        }
    } finally { _ticking = false; }
}
```

**Why a 1-minute window (①)?** The timer fires every 20 s, so it will land inside the minute after `StartTime` between one and three times. The window guarantees at least one hit while `TotalMinutes < 1` keeps it from firing an hour later.

**Why the `LastExecuted` check (②)?** Because the window is hit multiple times. Persisting `LastExecuted` (③) makes the schedule idempotent for the day — and because it is written through `SyncingScheduleRepository`, the cloud also learns that the schedule ran.

**Why the re-entrancy guard?** `TickAsync` is `async` and the timer does not wait for it. If a mow upload takes longer than 20 s, a second tick would start while the first is running. (Strictly, `bool _ticking` is not atomic; `Interlocked.CompareExchange` would be the fully correct version — an honest, easy improvement to name.)

**Missed schedules are skipped, not queued** — if the robot is not connected at 08:00, that day's mow simply does not happen and a warning is logged. That is the safe behaviour for a machine with spinning blades: you do not want it to start unexpectedly three hours later.

---

## 10. The Null Object implementations

| Class | Replaces | Behaviour |
|---|---|---|
| [`NullRobotService`](MowIT/Infrastructure/NullRobotService.cs) | any transport | `Observable.Empty<>()` streams, `IsConnected=false`, every method a completed task. **Currently unused** — a ready-made "no robot" stand-in and an ideal base for unit-test fakes |
| [`NullBlePermissionService`](MowIT/Infrastructure/NullBlePermissionService.cs) | Android/iOS permission service | always `true` — used on Windows/Mac and in simulator mode |
| [`NullScheduleSyncService`](MowIT/Infrastructure/ScheduleSync/NullScheduleSyncService.cs) | `HttpScheduleSyncService` | `IsEnabled=false`, no-op |
| [`NullRobotTransportSwitch`](MowIT/Infrastructure/Transport/NullRobotTransportSwitch.cs) | `RobotTransportRouter` | fixed `Bluetooth`, `SelectAsync` no-op |

**The value of the pattern:** consumers never write `if (service != null)`. `ScanViewModel` always injects `IRobotTransportSwitch`; whether the toggle actually does anything depends purely on DI configuration.
