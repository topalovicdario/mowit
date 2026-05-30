using System.Text.Json;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.Persistence;

public sealed class ScheduleRepository : IScheduleRepository
{
    private readonly AppDatabase _db;

    public ScheduleRepository(AppDatabase db) => _db = db;

    public async Task<List<MowingSchedule>> GetAllAsync()
    {
        await _db.InitializeAsync();
        var entities = await _db.Connection.Table<MowingScheduleEntity>().ToListAsync();
        return entities.Select(ToModel).ToList();
    }

    public async Task<MowingSchedule?> GetByIdAsync(int id)
    {
        await _db.InitializeAsync();
        var entity = await _db.Connection.FindAsync<MowingScheduleEntity>(id);
        return entity is null ? null : ToModel(entity);
    }

    public async Task SaveAsync(MowingSchedule schedule)
    {
        await _db.InitializeAsync();
        var entity = ToEntity(schedule);
        if (schedule.Id == 0)
        {
            await _db.Connection.InsertAsync(entity);
            schedule.Id = entity.Id;
        }
        else
        {
            await _db.Connection.UpdateAsync(entity);
        }
    }

    public async Task DeleteAsync(int id)
    {
        await _db.InitializeAsync();
        await _db.Connection.DeleteAsync<MowingScheduleEntity>(id);
    }

    private static MowingSchedule ToModel(MowingScheduleEntity e) => new()
    {
        Id              = e.Id,
        ActiveDays      = JsonSerializer.Deserialize<int[]>(e.ActiveDaysJson)!
                            .Select(d => (DayOfWeek)d).ToArray(),
        StartTime       = TimeSpan.FromTicks(e.StartTimeTicks),
        DurationMinutes = e.DurationMinutes,
        IsActive        = e.IsActive,
        ZoneName        = e.ZoneName,
        LastExecuted    = e.LastExecuted,
        ZoneId          = e.ZoneId == 0 ? null : e.ZoneId
    };

    private static MowingScheduleEntity ToEntity(MowingSchedule m) => new()
    {
        Id              = m.Id,
        ActiveDaysJson  = JsonSerializer.Serialize(m.ActiveDays.Select(d => (int)d).ToArray()),
        StartTimeTicks  = m.StartTime.Ticks,
        DurationMinutes = m.DurationMinutes,
        IsActive        = m.IsActive,
        ZoneName        = m.ZoneName,
        LastExecuted    = m.LastExecuted,
        ZoneId          = m.ZoneId ?? 0
    };
}
