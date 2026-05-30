using MowIT.Domain.Entities;

namespace MowIT.Domain.Interfaces;

public interface IScheduleRepository
{
    Task<List<MowingSchedule>> GetAllAsync();
    Task<MowingSchedule?> GetByIdAsync(int id);
    Task SaveAsync(MowingSchedule schedule);
    Task DeleteAsync(int id);
}
