using System.Diagnostics;
using System.Globalization;
using System.Reactive.Subjects;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MowIT.Application.Logging;
using MowIT.Application.Messages;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
#if ANDROID
using Android.Bluetooth;
using Android.Content;
#endif
#if WINDOWS
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
#endif

namespace MowIT.Infrastructure.ClassicBt;

public sealed class GreenTitanSppService
    : IRobotScanner, IRobotConnection, IRobotSensors, IRobotControl, IRobotBoundary, IDisposable
{
    private const string RobotDeviceName     = "GreenTitan";
    private const string SppUuid             = "00001101-0000-1000-8000-00805F9B34FB";
    private const int    CmdDelayMs          = 150;
    private const int    ManualAckTimeoutMs  = 500;
    private const float  RtkFixedMaxMm       = 50f;
    private const float  RtkFloatMaxMm       = 500f;

    private readonly ILogger<GreenTitanSppService> _logger;
    private readonly EventLogService _evt;
    private const string Source = "BT";

    private readonly Subject<MowerDevice>          _deviceSubject = new();
    private readonly Subject<RobotConnectionState> _stateSubject  = new();
    private readonly Subject<SensorSnapshot>       _sensorSubject = new();
    private readonly Subject<RobotStatus>          _statusSubject = new();

    private SensorSnapshot _latestSensor = new() { Timestamp = DateTime.UtcNow };
    private RobotStatus?   _latestStatus;
    private bool  _manualMode;
    private float _lastLinVel;
    private float _lastAngVel;

    private CancellationTokenSource? _readCts;
    private Task?   _readTask;
    private Timer?  _gpsTimer;
    private Timer?  _accuracyTimer;

    private StreamWriter? _writer;
    private Stream?       _rawStream;
    private readonly object _writeLock = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _pendingTx = new();
    private readonly object _statsLock = new();
    private int    _manualRecoveryBusy;

    public int    ControlCommandsSent { get; private set; }
    public int    ControlRepliesSeen  { get; private set; }
    public int    LatencySamples      { get; private set; }
    public long   LatencyTotalMs      { get; private set; }
    public long   LatencyMinMs        { get; private set; } = long.MaxValue;
    public long   LatencyMaxMs        { get; private set; }
    public bool   LogPositionsToEventLog { get; set; } = true;

#if ANDROID
    private BluetoothSocket?     _socket;
    private BtDiscoveryReceiver? _discoveryReceiver;
#endif

#if WINDOWS
    private StreamSocket?                     _winSocket;
    private readonly Dictionary<Guid, WinDeviceIds> _winDeviceIds = new();

    private readonly record struct WinDeviceIds(string? ServiceId, string? DeviceId);
#endif

    public GreenTitanSppService(ILogger<GreenTitanSppService> logger, EventLogService evt)
    {
        _logger = logger;
        _evt    = evt;
    }

public IObservable<MowerDevice> DiscoveredDevices => _deviceSubject;
    public bool IsScanning { get; private set; }

    public async Task StartScanAsync(CancellationToken ct = default)
    {
        IsScanning = true;
#if ANDROID
        var adapter = BluetoothAdapter.DefaultAdapter;

        var bonded = adapter?.BondedDevices;
        if (bonded != null)
        {
            foreach (var btDev in bonded)
            {
                if (btDev != null)
                    _deviceSubject.OnNext(ToMowerDevice(btDev));
            }
        }

        if (adapter?.IsEnabled == true)
        {
            _discoveryReceiver = new BtDiscoveryReceiver(btDev =>
            {
                _deviceSubject.OnNext(ToMowerDevice(btDev));
            });

            var filter = new IntentFilter();
            filter.AddAction(BluetoothDevice.ActionFound);
            filter.AddAction(BluetoothAdapter.ActionDiscoveryFinished);
            Android.App.Application.Context.RegisterReceiver(_discoveryReceiver, filter);
            adapter.StartDiscovery();

            try { await Task.Delay(15_000, ct); }
            catch (OperationCanceledException) { }

            await StopScanAsync();
        }
#elif WINDOWS
        try
        {
            _winDeviceIds.Clear();
            var byName = new Dictionary<string, (Guid Id, string? Svc, string? Dev)>(StringComparer.OrdinalIgnoreCase);

            var rfcommSel   = RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort);
            var rfcommInfos = await DeviceInformation.FindAllAsync(rfcommSel).AsTask(ct);
            foreach (var d in rfcommInfos)
            {
                byName[d.Name] = (Guid.NewGuid(), d.Id, null);
            }

            var btSel   = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var btInfos = await DeviceInformation.FindAllAsync(btSel).AsTask(ct);
            foreach (var d in btInfos)
            {
                if (byName.TryGetValue(d.Name, out var existing))
                    byName[d.Name] = (existing.Id, existing.Svc, d.Id);
                else
                    byName[d.Name] = (Guid.NewGuid(), null, d.Id);
            }

            foreach (var (name, entry) in byName)
            {
                _winDeviceIds[entry.Id] = new WinDeviceIds(entry.Svc, entry.Dev);
                _deviceSubject.OnNext(new MowerDevice { Id = entry.Id, Name = name });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Windows BT scan failed"); }
#else
        await Task.CompletedTask;
#endif
        IsScanning = false;
    }

    public async Task StopScanAsync()
    {
#if ANDROID
        BluetoothAdapter.DefaultAdapter?.CancelDiscovery();
        if (_discoveryReceiver != null)
        {
            try { Android.App.Application.Context.UnregisterReceiver(_discoveryReceiver); }
            catch {  }
            _discoveryReceiver = null;
        }
#endif
        IsScanning = false;
        await Task.CompletedTask;
    }

public IObservable<RobotConnectionState> ConnectionState => _stateSubject;
    public RobotConnectionState CurrentState { get; private set; } = RobotConnectionState.Disconnected;
    public bool IsConnected => CurrentState == RobotConnectionState.Connected;

    public async Task<bool> ConnectAsync(MowerDevice mowerDevice, CancellationToken ct = default)
    {
        CurrentState = RobotConnectionState.Connecting;
        _stateSubject.OnNext(RobotConnectionState.Connecting);
        _evt.State(Source, $"Connecting to {mowerDevice.Name} ({mowerDevice.Id})...");

        try
        {
#if ANDROID
            var adapter = BluetoothAdapter.DefaultAdapter
                ?? throw new InvalidOperationException("Bluetooth adapter not available");

            adapter.CancelDiscovery();

            var mac      = GuidToMac(mowerDevice.Id);
            var btDevice = mac != null
                ? adapter.GetRemoteDevice(mac)
                : adapter.BondedDevices?.FirstOrDefault(
                    d => d.Name?.Contains(RobotDeviceName, StringComparison.OrdinalIgnoreCase) == true);

            if (btDevice == null)
                throw new Exception($"'{mowerDevice.Name}' not found in paired Bluetooth devices");

            var uuid = Java.Util.UUID.FromString(SppUuid)!;
            _socket  = btDevice.CreateRfcommSocketToServiceRecord(uuid)
                ?? throw new Exception("Failed to create RFCOMM socket");

            await Task.Run(() => _socket.Connect(), ct);

            _writer    = new StreamWriter(_socket.OutputStream!, Encoding.ASCII) { AutoFlush = true };
            _rawStream = _socket.InputStream!;

#elif WINDOWS
            if (!_winDeviceIds.TryGetValue(mowerDevice.Id, out var winId))
                throw new Exception($"'{mowerDevice.Name}' not found - run scan first");

            RfcommDeviceService? rfcommService = await ResolveRfcommServiceAsync(winId, ct);

            if (rfcommService is null)
                throw new Exception("SPP service not available - ensure GreenTitan is powered on and paired");

            var access = await rfcommService.RequestAccessAsync().AsTask(ct);
            if (access != DeviceAccessStatus.Allowed)
                throw new Exception($"Bluetooth access not granted: {access}. Allow Bluetooth for this app in Windows Settings > Privacy > Bluetooth.");

            _winSocket = new StreamSocket();
            await _winSocket.ConnectAsync(
                rfcommService.ConnectionHostName,
                rfcommService.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(ct);

            _writer    = new StreamWriter(_winSocket.OutputStream.AsStreamForWrite(), Encoding.ASCII) { AutoFlush = true };
            _rawStream = _winSocket.InputStream.AsStreamForRead();

#else
            throw new PlatformNotSupportedException("Classic BT SPP is only supported on Android and Windows");
#endif

            _readCts  = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoop(_readCts.Token));

            _gpsTimer      = new Timer(_ => FireAndForget("GPS/GET/POS"),      null, 1_000,   500);
            _accuracyTimer = new Timer(_ => FireAndForget("GPS/GET/ACCURACY"), null, 2_000, 2_000);

            lock (_statsLock)
            {
                _pendingTx.Clear();
                ControlCommandsSent = 0;
                ControlRepliesSeen  = 0;
                LatencySamples      = 0;
                LatencyTotalMs      = 0;
                LatencyMinMs        = long.MaxValue;
                LatencyMaxMs        = 0;
            }

            _manualMode  = false;
            CurrentState = RobotConnectionState.Connected;
            _stateSubject.OnNext(RobotConnectionState.Connected);
            WeakReferenceMessenger.Default.Send(new RobotConnectedMessage(mowerDevice.Name));
            _evt.State(Source, $"Connected to {mowerDevice.Name} via Classic BT SPP");
            return true;
        }
        catch (Exception ex)
        {
            _evt.Error(Source, $"Connection to {mowerDevice.Name} failed: {ex.Message}");
            _logger.LogError(ex, "Classic BT connection to {Name} failed", mowerDevice.Name);
            CurrentState = RobotConnectionState.Disconnected;
            _stateSubject.OnNext(RobotConnectionState.Disconnected);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        LogLinkStats();

        _gpsTimer?.Dispose();      _gpsTimer      = null;
        _accuracyTimer?.Dispose(); _accuracyTimer = null;
        _readCts?.Cancel();

        if (_readTask != null)
            await Task.WhenAny(_readTask, Task.Delay(1_000));

        try { _writer?.Dispose(); } catch {  }
        _writer    = null;
        _rawStream = null;

#if ANDROID
        try { _socket?.Close();   } catch {  }
        try { _socket?.Dispose(); } catch {  }
        _socket = null;
#endif
#if WINDOWS
        try { _winSocket?.Dispose(); } catch {  }
        _winSocket = null;
#endif

        _manualMode  = false;
        CurrentState = RobotConnectionState.Disconnected;
        _stateSubject.OnNext(RobotConnectionState.Disconnected);
    }

public IObservable<SensorSnapshot> SensorStream => _sensorSubject;
    public IObservable<RobotStatus>    StatusStream  => _statusSubject;
    public SensorSnapshot? LastSensor => _latestSensor;
    public RobotStatus?    LastStatus  => _latestStatus;

public async Task SendMotorCommandAsync(float linearVel, float angularVel)
    {
        if (!IsConnected) return;

        _lastLinVel = linearVel;
        _lastAngVel = angularVel;

        if (!_manualMode)
        {
            SendCommand("MOWER/MANUAL/ON");
            await WaitForManualModeAsync(ManualAckTimeoutMs);
        }

        SendMoveCommand(linearVel, angularVel);
    }

    private void SendMoveCommand(float linearVel, float angularVel)
        => SendCommand(string.Format(CultureInfo.InvariantCulture,
            "MOWER/MOVE/{0:F3},{1:F3}", linearVel, angularVel));

    private async Task<bool> WaitForManualModeAsync(int timeoutMs)
    {
        long deadline = _clock.ElapsedMilliseconds + timeoutMs;

        while (!_manualMode && _clock.ElapsedMilliseconds < deadline)
            await Task.Delay(20);

        return _manualMode;
    }

    private async Task RecoverManualModeAsync()
    {
        if (Interlocked.CompareExchange(ref _manualRecoveryBusy, 1, 0) != 0) return;

        try
        {
            float lin = _lastLinVel, ang = _lastAngVel;
            if (MathF.Abs(lin) < 0.001f && MathF.Abs(ang) < 0.001f) return;

            _evt.Warn(Source, "MOVE rejected (NOT_MANUAL) - re-enabling manual mode and repeating the move");

            SendCommand("MOWER/MANUAL/ON");
            if (!await WaitForManualModeAsync(ManualAckTimeoutMs))
            {
                _evt.Error(Source, "manual mode was not confirmed - move not repeated");
                return;
            }

            SendMoveCommand(lin, ang);
        }
        finally
        {
            Volatile.Write(ref _manualRecoveryBusy, 0);
        }
    }

    private static GpsFixType InferFixType(float accuracyMm, GpsPoint pos)
    {
        if (accuracyMm <= 0) return GpsFixType.NoFix;
        if (pos.Latitude == 0 && pos.Longitude == 0) return GpsFixType.NoFix;
        if (accuracyMm <= RtkFixedMaxMm) return GpsFixType.RtkFixed;
        if (accuracyMm <= RtkFloatMaxMm) return GpsFixType.RtkFloat;
        return GpsFixType.Standard;
    }

    public async Task SendActionAsync(RobotAction action, byte param = 0)
    {
        if (!IsConnected) return;

        var cmd = action switch
        {
            RobotAction.StartMowing          => "MOWER/START",
            RobotAction.Stop                 => "MOWER/MANUAL/OFF",
            RobotAction.BoundaryRecordStart  => "MOWER/CAPTURE/START",
            RobotAction.BoundaryCapturePoint => "MOWER/CAPTURE/POINT",
            RobotAction.BoundaryRecordEnd    => "MOWER/CAPTURE/END",
            RobotAction.BoundaryClear        => "MOWER/CAPTURE/START",
            RobotAction.StartRoute           => "MOWER/START",
            RobotAction.ManualModeOn         => "MOWER/MANUAL/ON",
            RobotAction.ManualModeOff        => "MOWER/MANUAL/OFF",
            RobotAction.CaptureBase          => "GPS/CAPTURE/BASE",
            RobotAction.CaptureOutline       => "MOWER/CAPTURE/OUTLINE",
            _                                => null
        };

        if (cmd is null)
        {
            _logger.LogWarning("Action {Action} is not supported by GreenTitan firmware", action);
            return;
        }

        SendCommand(cmd);
        await Task.Delay(CmdDelayMs);
        _logger.LogInformation("Action {Action} -> {Cmd}", action, cmd);
    }

public Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null)
    {

throw new NotSupportedException(
            "GreenTitan does not support uploading GPS boundary coordinates. " +
            "Walk the perimeter using BoundaryRecordStart / BoundaryCapturePoint / BoundaryRecordEnd.");
    }

    public Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)
    {
        
        throw new NotSupportedException(
            "GreenTitan manages routes internally. Start mowing with RobotAction.StartMowing.");
    }

    public async Task ClearBoundaryAsync()
    {
        
        SendCommand("MOWER/CAPTURE/START");
        await Task.Delay(CmdDelayMs);
    }

private void SendCommand(string cmd)
    {
        bool isPoll = cmd.StartsWith("GPS/GET/", StringComparison.Ordinal);

        try
        {
            lock (_writeLock)
            {
                _writer?.Write(cmd + "<");
            }

            lock (_statsLock)
            {
                _pendingTx[CorrelationKey(cmd, true)] = _clock.ElapsedMilliseconds;
                if (!isPoll) ControlCommandsSent++;
            }

            if (!isPoll) _evt.Tx(Source, cmd);
        }
        catch (Exception ex)
        {
            _evt.Error(Source, $"Failed to send '{cmd}': {ex.Message}");
            _logger.LogWarning(ex, "Failed to send: {Cmd}", cmd);
        }
    }

    private static string CorrelationKey(string message, bool outgoing)
    {
        var p = message.Split('/');
        if (p.Length < 2) return message;

        string head = p[0].ToUpperInvariant();
        string mid  = p[1].ToUpperInvariant();

        if (outgoing && head == "GPS" && mid == "GET" && p.Length >= 3)
            return $"GPS/{p[2].ToUpperInvariant()}";

        if (head == "MOWER" && mid == "MANUAL")
            return "MOWER/MANUAL";

        if (mid == "CAPTURE" && p.Length >= 3)
            return $"{head}/CAPTURE/{p[2].ToUpperInvariant()}";

        return $"{head}/{mid}";
    }

    private long? MatchReply(string line)
    {
        string key = CorrelationKey(line, false);

        lock (_statsLock)
        {
            if (!_pendingTx.Remove(key, out long sentAt)) return null;

            long elapsed = _clock.ElapsedMilliseconds - sentAt;

            if (!key.StartsWith("GPS/POS", StringComparison.Ordinal)
             && !key.StartsWith("GPS/ACCURACY", StringComparison.Ordinal))
            {
                ControlRepliesSeen++;
                LatencySamples++;
                LatencyTotalMs += elapsed;
                if (elapsed < LatencyMinMs) LatencyMinMs = elapsed;
                if (elapsed > LatencyMaxMs) LatencyMaxMs = elapsed;
            }

            return elapsed;
        }
    }

    private void LogLinkStats()
    {
        lock (_statsLock)
        {
            if (ControlCommandsSent == 0) return;

            double pct = 100.0 * ControlRepliesSeen / ControlCommandsSent;
            double avg = LatencySamples > 0 ? (double)LatencyTotalMs / LatencySamples : 0;
            long   min = LatencyMinMs == long.MaxValue ? 0 : LatencyMinMs;

            _evt.State(Source,
                $"link stats: sent={ControlCommandsSent} acked={ControlRepliesSeen} ({pct:F1}%), " +
                $"latency avg={avg:F0} ms min={min} ms max={LatencyMaxMs} ms (n={LatencySamples})");
        }
    }


    private void FireAndForget(string cmd)
    {
        if (IsConnected) SendCommand(cmd);
    }

private static readonly string[] _msgPrefixes = ["GPS/", "MOWER/"];

    private void ReadLoop(CancellationToken ct)
    {
        ct.Register(() =>
        {
#if ANDROID
            try { _socket?.Close(); } catch {  }
#endif
#if WINDOWS
            try { _winSocket?.Dispose(); } catch {  }
#endif
        });

        var buf     = new byte[512];
        var pending = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = _rawStream!.Read(buf, 0, buf.Length);
                if (n == 0) break;

                pending.Append(Encoding.ASCII.GetString(buf, 0, n));
                FlushPending(pending);
            }
        }
        catch when (ct.IsCancellationRequested)
        {
            
        }
        catch (Exception ex)
        {
            _evt.Error(Source, $"Read loop dropped unexpectedly: {ex.Message}");
            _logger.LogError(ex, "Bluetooth read loop dropped unexpectedly");
            CurrentState = RobotConnectionState.Disconnected;
            _stateSubject.OnNext(RobotConnectionState.Disconnected);
            WeakReferenceMessenger.Default.Send(new RobotDisconnectedMessage(RobotDeviceName));
        }
    }

    private void FlushPending(StringBuilder pending)
    {
        while (pending.Length > 0)
        {
            string text  = pending.ToString();
            int    split = FindMessageSplit(text);

            if (split < 0)
            {
                
                pending.Clear();
                var msg = text.Trim();
                if (msg.Length > 0) ParseLine(msg);
                break;
            }

            var line = text[..split].Trim('\r', '\n', ' ');
            if (line.Length > 0) ParseLine(line);
            pending.Clear();
            pending.Append(text[split..]);
        }
    }

    private static int FindMessageSplit(string text)
    {
        
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || text[i] == '\r') return i + 1;
        }

foreach (var prefix in _msgPrefixes)
        {
            if (text.Length <= prefix.Length) continue;
            int idx = text.IndexOf(prefix, 1, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return idx;
        }

        return -1; 
    }

private void ParseLine(string line)
    {
        long? latencyMs = MatchReply(line);
        string suffix   = latencyMs is { } ms ? $"  (+{ms} ms)" : string.Empty;

        bool telemetry = line.StartsWith("GPS/POS/", StringComparison.OrdinalIgnoreCase)
                      || line.StartsWith("GPS/ACCURACY/", StringComparison.OrdinalIgnoreCase);

        if (!telemetry)                    _evt.Rx(Source, line + suffix);
        else if (LogPositionsToEventLog)   _evt.Rx(Source, line + suffix);

        var parts = line.Split('/');
        if (parts.Length < 2) return;

        switch (parts[0].ToUpperInvariant())
        {
            case "GPS":   ParseGpsResponse(parts);   break;
            case "MOWER": ParseMowerResponse(parts); break;
            default:
                _logger.LogDebug("Unknown response: {Line}", line);
                break;
        }
    }

    private void ParseGpsResponse(string[] parts)
    {
        if (parts.Length < 3) return;

        switch (parts[1].ToUpperInvariant())
        {
            case "ACCURACY":
                if (float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float accMeters))
                {
                    float accMm = accMeters * 1000f;
                    _latestSensor = _latestSensor with
                    {
                        GpsAccuracyMm = accMm,
                        GpsFixType    = InferFixType(accMm, _latestSensor.Gps),
                        Timestamp     = DateTime.UtcNow
                    };
                    _sensorSubject.OnNext(_latestSensor);
                }
                break;

            case "POS":
            {
                
                var coords = parts[2].Split(',');
                if (coords.Length == 2
                    && double.TryParse(coords[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                    && double.TryParse(coords[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    UpdateGps(new GpsPoint(lat, lon));
                }
                break;
            }

            case "CAPTURE":

                if (parts.Length >= 4 && parts[2].Equals("BASE", StringComparison.OrdinalIgnoreCase))
                {
                    string verdict = parts[3].ToUpperInvariant();
                    string reason  = parts.Length >= 5 ? parts[4] : string.Empty;

                    switch (verdict)
                    {
                        case "STARTED":
                            _evt.Info(Source, "datum capture started - waiting for final verdict");
                            break;

                        case "DONE":
                            WeakReferenceMessenger.Default.Send(new BaseCapturedMessage());
                            break;

                        default:
                            if (reason.Equals("BUSY", StringComparison.OrdinalIgnoreCase))
                                _evt.Info(Source, "datum capture already running - waiting for final verdict");
                            else
                                WeakReferenceMessenger.Default.Send(new RobotErrorMessage($"BASE_CAPTURE_FAIL/{reason}"));
                            break;
                    }

                    _logger.LogInformation("GPS/CAPTURE/BASE {Verdict} {Reason}", verdict, reason);
                }
                break;
        }
    }

    private void ParseMowerResponse(string[] parts)
    {
        if (parts.Length < 3) return;

        switch (parts[1].ToUpperInvariant())
        {
            case "START":
            {
                bool ok = parts[2].Equals("OK", StringComparison.OrdinalIgnoreCase);
                if (ok)
                {
                    _latestStatus = (_latestStatus ?? new RobotStatus()) with
                    {
                        State     = RobotState.Mowing,
                        Timestamp = DateTime.UtcNow
                    };
                    _statusSubject.OnNext(_latestStatus);
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new RobotErrorMessage("MOWER_START_FAIL"));
                }
                break;
            }

            case "MANUAL":
                if (parts.Length >= 4)
                {
                    bool on = parts[2].Equals("ON", StringComparison.OrdinalIgnoreCase);
                    bool ok = on && parts[3].Equals("OK", StringComparison.OrdinalIgnoreCase);
                    _manualMode   = ok;
                    _latestSensor = _latestSensor with
                    {
                        IsManualMode  = _manualMode,
                        IsMotorMoving = _manualMode && _latestSensor.IsMotorMoving,
                        Timestamp     = DateTime.UtcNow
                    };
                    _sensorSubject.OnNext(_latestSensor);
                    _logger.LogDebug("Manual mode: {Mode}", _manualMode);
                }
                break;

            case "MOVE":
                switch (parts[2].ToUpperInvariant())
                {
                    case "OK":
                        bool moving = MathF.Abs(_lastLinVel) > 0.001f || MathF.Abs(_lastAngVel) > 0.001f;
                        _latestSensor = _latestSensor with { IsMotorMoving = moving, Timestamp = DateTime.UtcNow };
                        _sensorSubject.OnNext(_latestSensor);
                        break;

                    case "STOPPED":
                        _latestSensor = _latestSensor with { IsMotorMoving = false, Timestamp = DateTime.UtcNow };
                        _sensorSubject.OnNext(_latestSensor);
                        break;

                    case "FAIL":
                        string reason = parts.Length >= 4 ? parts[3] : string.Empty;
                        _logger.LogWarning("MOWER/MOVE failed: {Reason}", reason);
                        if (string.Equals(reason, "NOT_MANUAL", StringComparison.OrdinalIgnoreCase))
                        {
                            _manualMode   = false;
                            _latestSensor = _latestSensor with
                            {
                                IsManualMode  = false,
                                IsMotorMoving = false,
                                Timestamp     = DateTime.UtcNow
                            };
                            _sensorSubject.OnNext(_latestSensor);
                            _ = RecoverManualModeAsync();
                        }
                        break;
                }
                break;

            case "CAPTURE":
                ParseCaptureResponse(parts);
                break;
        }
    }

    private void ParseCaptureResponse(string[] parts)
    {
        
        if (parts.Length < 4) return;

        string subCmd = parts[2].ToUpperInvariant();
        bool   ok     = parts[3].Equals("OK", StringComparison.OrdinalIgnoreCase);

        switch (subCmd)
        {
            

            case "START":
                if (ok)
                {
                    _latestStatus = (_latestStatus ?? new RobotStatus()) with
                    {
                        State     = RobotState.RecordingBoundary,
                        Timestamp = DateTime.UtcNow
                    };
                    _statusSubject.OnNext(_latestStatus);
                   
                    WeakReferenceMessenger.Default.Send(new BoundaryClearedMessage());
                }
                break;

            case "POINT":

                if (ok && parts.Length >= 5)
                {
                    var coords = parts[4].Split(',');
                    if (coords.Length == 2
                        && double.TryParse(coords[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pLat)
                        && double.TryParse(coords[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pLon))
                    {
                        WeakReferenceMessenger.Default.Send(
                            new BoundaryGpsPointCapturedMessage(new GpsPoint(pLat, pLon)));
                    }
                    else
                    {
                        _evt.Error(Source, $"unreadable capture point payload '{parts[4]}'");
                        WeakReferenceMessenger.Default.Send(new RobotErrorMessage("CAPTURE_POINT_PARSE"));
                    }
                }
                else if (!ok)
                {
                    string pointReason = parts.Length >= 5 ? parts[4] : string.Empty;
                    WeakReferenceMessenger.Default.Send(new RobotErrorMessage($"CAPTURE_POINT_FAIL/{pointReason}"));
                }
                break;

            case "END":
             
                string? endReason = (!ok && parts.Length >= 5) ? parts[4] : null;
                WeakReferenceMessenger.Default.Send(new CaptureEndMessage(ok, endReason));
                if (!ok)
                    WeakReferenceMessenger.Default.Send(new RobotErrorMessage($"CAPTURE_END_FAIL/{endReason ?? string.Empty}"));
                break;

            case "OUTLINE":
                _logger.LogInformation("MOWER/CAPTURE/OUTLINE {Result}", ok ? "OK" : "FAIL");
                if (ok)
                    WeakReferenceMessenger.Default.Send(new OutlineCapturedMessage());
                else
                    WeakReferenceMessenger.Default.Send(new RobotErrorMessage("CAPTURE_OUTLINE_FAIL"));
                break;
        }
    }

    private void UpdateGps(GpsPoint pt)
    {
        _latestSensor = _latestSensor with
        {
            Gps        = pt,
            GpsFixType = InferFixType(_latestSensor.GpsAccuracyMm, pt),
            Timestamp  = DateTime.UtcNow
        };
        _sensorSubject.OnNext(_latestSensor);
    }

#if ANDROID
    private static MowerDevice ToMowerDevice(BluetoothDevice btDev) =>
        new()
        {
            Id   = MacToGuid(btDev.Address ?? string.Empty),
            Name = btDev.Name ?? RobotDeviceName,
            Rssi = 0
        };

    private static Guid MacToGuid(string mac)
    {
        var hex = mac.Replace(":", "").PadRight(32, '0');
        return Guid.Parse($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}");
    }

    private static string? GuidToMac(Guid id)
    {
        var n = id.ToString("N");
        if (!n[12..].All(c => c == '0')) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => n.Substring(i * 2, 2).ToUpper()));
    }
#endif

#if WINDOWS
    private async Task<RfcommDeviceService?> ResolveRfcommServiceAsync(WinDeviceIds ids, CancellationToken ct)
    {
        if (ids.ServiceId is { } svcId)
        {
            try
            {
                var svc = await RfcommDeviceService.FromIdAsync(svcId).AsTask(ct);
                if (svc is not null) return svc;
                _logger.LogWarning("RfcommDeviceService.FromIdAsync returned null for cached service id; falling back to paired-device lookup");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RfcommDeviceService.FromIdAsync threw; falling back to paired-device lookup");
            }
        }

        if (ids.DeviceId is { } devId)
        {
            var btDevice = await BluetoothDevice.FromIdAsync(devId).AsTask(ct);
            if (btDevice is null)
            {
                _logger.LogWarning("BluetoothDevice.FromIdAsync returned null for {DeviceId}", devId);
                return null;
            }

            var res = await btDevice.GetRfcommServicesForIdAsync(
                RfcommServiceId.SerialPort, BluetoothCacheMode.Cached).AsTask(ct);
            if (res.Services.Count == 0)
                res = await btDevice.GetRfcommServicesForIdAsync(
                    RfcommServiceId.SerialPort, BluetoothCacheMode.Uncached).AsTask(ct);

            return res.Services.FirstOrDefault();
        }

        return null;
    }
#endif

    public void Dispose()
    {
        _gpsTimer?.Dispose();
        _accuracyTimer?.Dispose();
        _readCts?.Cancel();
        _readCts?.Dispose();
        try { _writer?.Dispose(); } catch {  }
#if ANDROID
        try { _socket?.Close();   } catch {  }
        try { _socket?.Dispose(); } catch {  }
#endif
#if WINDOWS
        try { _winSocket?.Dispose(); } catch {  }
#endif
        _deviceSubject.OnCompleted(); _deviceSubject.Dispose();
        _stateSubject.OnCompleted();  _stateSubject.Dispose();
        _sensorSubject.OnCompleted(); _sensorSubject.Dispose();
        _statusSubject.OnCompleted(); _statusSubject.Dispose();
    }
}
