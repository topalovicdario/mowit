using System.Diagnostics;
using System.Globalization;
using MowIT.Application.Logging;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.Services;

public sealed class GpsTraceService : IDisposable
{
    private const string Source = "GPSTRACE";

    private readonly IRobotSensors    _sensors;
    private readonly IRobotConnection _connection;
    private readonly EventLogService  _evt;

    private readonly object _fileLock = new();
    private IDisposable?  _connectionSub;
    private IDisposable?  _sensorSub;
    private StreamWriter? _csv;
    private Stopwatch?    _clock;
    private GpsPoint      _lastWritten;
    private int           _rows;

    public string? CurrentFilePath { get; private set; }
    public int     RowCount        => _rows;

    public GpsTraceService(IRobotSensors sensors, IRobotConnection connection, EventLogService evt)
    {
        _sensors    = sensors;
        _connection = connection;
        _evt        = evt;

        _connectionSub = _connection.ConnectionState.Subscribe(OnConnectionState);
    }

    private void OnConnectionState(RobotConnectionState state)
    {
        if (state == RobotConnectionState.Connected)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        Stop();

        try
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "logs");
            Directory.CreateDirectory(dir);

            CurrentFilePath = Path.Combine(dir, $"gpstrace_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _csv = new StreamWriter(CurrentFilePath, append: false) { AutoFlush = true };
            _csv.WriteLine("utc_iso,elapsed_ms,latitude,longitude,accuracy_m,fix,pos_changed,manual,moving");

            _clock       = Stopwatch.StartNew();
            _rows        = 0;
            _lastWritten = default;

            _sensorSub = _sensors.SensorStream.Subscribe(Write);
            _evt.State(Source, $"recording to {Path.GetFileName(CurrentFilePath)}");
        }
        catch (Exception ex)
        {
            _csv = null;
            CurrentFilePath = null;
            _evt.Error(Source, $"could not start GPS trace: {ex.Message}");
        }
    }

    private void Stop()
    {
        _sensorSub?.Dispose();
        _sensorSub = null;

        lock (_fileLock)
        {
            if (_csv is null) return;

            try { _csv.Dispose(); } catch { }
            _csv = null;
            _evt.State(Source, $"stopped after {_rows} samples ({Path.GetFileName(CurrentFilePath)})");
        }

        _clock?.Stop();
        _clock = null;
    }

    private void Write(SensorSnapshot s)
    {
        if (s.Gps.Latitude == 0 && s.Gps.Longitude == 0) return;

        lock (_fileLock)
        {
            if (_csv is null) return;

            bool changed = s.Gps.Latitude  != _lastWritten.Latitude
                        || s.Gps.Longitude != _lastWritten.Longitude;

            try
            {
                _csv.WriteLine(string.Join(',',
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    (_clock?.ElapsedMilliseconds ?? 0).ToString(CultureInfo.InvariantCulture),
                    s.Gps.Latitude.ToString("F7", CultureInfo.InvariantCulture),
                    s.Gps.Longitude.ToString("F7", CultureInfo.InvariantCulture),
                    (s.GpsAccuracyMm / 1000f).ToString("F3", CultureInfo.InvariantCulture),
                    s.GpsFixType.ToString(),
                    changed ? "1" : "0",
                    s.IsManualMode ? "1" : "0",
                    s.IsMotorMoving ? "1" : "0"));

                _rows++;
                _lastWritten = s.Gps;
            }
            catch (Exception ex)
            {
                _evt.Error(Source, $"GPS trace write failed: {ex.Message}");
                try { _csv.Dispose(); } catch { }
                _csv = null;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _connectionSub?.Dispose();
        _connectionSub = null;
    }
}
