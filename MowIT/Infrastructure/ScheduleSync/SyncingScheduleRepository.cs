using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.ScheduleSync;

public sealed class SyncingScheduleRepository : IScheduleRepository
{
    private readonly IScheduleRepository  _inner;
    private readonly IScheduleSyncService _sync;

    public SyncingScheduleRepository(IScheduleRepository inner, IScheduleSyncService sync)
    {
        _inner = inner;
        _sync  = sync;
    }

    public Task<List<MowingSchedule>> GetAllAsync()
        => _inner.GetAllAsync();

    public Task<MowingSchedule?> GetByIdAsync(int id)
        => _inner.GetByIdAsync(id);

    public async Task SaveAsync(MowingSchedule schedule)
    {
        await _inner.SaveAsync(schedule);

        if (!_sync.IsEnabled) return;

        var all = await _inner.GetAllAsync();
        _ = _sync.PushAsync(all);
    }

    public async Task DeleteAsync(int id)
    {
        await _inner.DeleteAsync(id);
        if (_sync.IsEnabled)
            _ = _sync.DeleteAsync(id);
    }
}
