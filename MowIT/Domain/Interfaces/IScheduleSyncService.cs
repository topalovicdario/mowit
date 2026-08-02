using MowIT.Domain.Entities;

namespace MowIT.Domain.Interfaces;

public interface IScheduleSyncService
{
    bool IsEnabled { get; }

    Task PushAsync(IReadOnlyList<MowingSchedule> schedules, CancellationToken ct = default);

    Task DeleteAsync(int scheduleId, CancellationToken ct = default);
}
