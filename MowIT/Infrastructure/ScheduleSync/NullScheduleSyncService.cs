using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.ScheduleSync;

public sealed class NullScheduleSyncService : IScheduleSyncService
{
    public bool IsEnabled => false;

    public Task PushAsync(IReadOnlyList<MowingSchedule> schedules, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteAsync(int scheduleId, CancellationToken ct = default)
        => Task.CompletedTask;
}
