using System.Collections.Concurrent;
using MowIT.Shared.Telemetry;

namespace MowIT.ScheduleServer.Storage;

public interface ICommandQueue
{
    void Enqueue(string robotId, RobotCommandDto command);
    RobotCommandDto[] Drain(string robotId);
}

public sealed class InMemoryCommandQueue : ICommandQueue
{
    private readonly ConcurrentDictionary<string, List<RobotCommandDto>> _pending =
        new(StringComparer.Ordinal);

    public void Enqueue(string robotId, RobotCommandDto command)
    {
        var list = _pending.GetOrAdd(robotId, _ => new List<RobotCommandDto>());
        lock (list)
        {
            if (command.Kind == CommandKinds.Motor)
                list.RemoveAll(c => c.Kind == CommandKinds.Motor);
            list.Add(command);
        }
    }

    public RobotCommandDto[] Drain(string robotId)
    {
        if (!_pending.TryGetValue(robotId, out var list))
            return Array.Empty<RobotCommandDto>();

        lock (list)
        {
            if (list.Count == 0) return Array.Empty<RobotCommandDto>();
            var snapshot = list.ToArray();
            list.Clear();
            return snapshot;
        }
    }
}
