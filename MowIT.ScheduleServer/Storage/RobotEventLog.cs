using System.Collections.Concurrent;
using MowIT.Shared.Telemetry;

namespace MowIT.ScheduleServer.Storage;

public interface IRobotEventLog
{
    RobotEventDto Append(string robotId, RobotEventDto evt);
    EventListResponse GetSince(string robotId, long cursor);
}

public sealed class InMemoryRobotEventLog : IRobotEventLog
{
    private const int MaxEntries = 200;

    private sealed class Channel
    {
        public long Seq;
        public readonly LinkedList<RobotEventDto> Events = new();
    }

    private readonly ConcurrentDictionary<string, Channel> _channels =
        new(StringComparer.Ordinal);

    public RobotEventDto Append(string robotId, RobotEventDto evt)
    {
        var channel = _channels.GetOrAdd(robotId, _ => new Channel());
        lock (channel)
        {
            var stored = evt with { Seq = ++channel.Seq };
            channel.Events.AddLast(stored);
            while (channel.Events.Count > MaxEntries)
                channel.Events.RemoveFirst();
            return stored;
        }
    }

    public EventListResponse GetSince(string robotId, long cursor)
    {
        if (!_channels.TryGetValue(robotId, out var channel))
            return new EventListResponse { Cursor = cursor };

        lock (channel)
        {
            var newer = channel.Events.Where(e => e.Seq > cursor).ToArray();
            var nextCursor = newer.Length > 0 ? newer[^1].Seq : Math.Max(cursor, channel.Seq);
            return new EventListResponse { Events = newer, Cursor = nextCursor };
        }
    }
}
