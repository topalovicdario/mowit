using System.Collections.Concurrent;
using MowIT.Shared.Telemetry;

namespace MowIT.ScheduleServer.Storage;

public interface ITelemetryStore
{
    void Update(string robotId, TelemetryDto telemetry);
    TelemetryEnvelope Get(string robotId);
    TelemetryEnvelope[] GetActive();
}

public sealed class InMemoryTelemetryStore : ITelemetryStore
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(5);

    private sealed record Entry(TelemetryDto Telemetry, DateTime LastSeenUtc);

    private readonly ConcurrentDictionary<string, Entry> _latest = new(StringComparer.Ordinal);

    public void Update(string robotId, TelemetryDto telemetry)
        => _latest[robotId] = new Entry(telemetry, DateTime.UtcNow);

    public TelemetryEnvelope Get(string robotId)
    {
        if (!_latest.TryGetValue(robotId, out var entry))
            return new TelemetryEnvelope { RobotId = robotId, IsOnline = false };

        return new TelemetryEnvelope
        {
            RobotId     = robotId,
            IsOnline    = DateTime.UtcNow - entry.LastSeenUtc < OnlineWindow,
            LastSeenUtc = entry.LastSeenUtc,
            Latest      = entry.Telemetry
        };
    }

    public TelemetryEnvelope[] GetActive()
    {
        var now = DateTime.UtcNow;
        return _latest
            .Where(kv => now - kv.Value.LastSeenUtc < OnlineWindow)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new TelemetryEnvelope
            {
                RobotId     = kv.Key,
                IsOnline    = true,
                LastSeenUtc = kv.Value.LastSeenUtc,
                Latest      = kv.Value.Telemetry
            })
            .ToArray();
    }
}
