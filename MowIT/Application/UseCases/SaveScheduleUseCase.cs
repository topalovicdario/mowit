using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.UseCases;

public class SaveScheduleUseCase
{
    private readonly IScheduleRepository _repo;

    public SaveScheduleUseCase(IScheduleRepository repo) => _repo = repo;

    public async Task ExecuteAsync(MowingSchedule schedule)
    {
        if (schedule.ActiveDays.Length == 0)
            throw new InvalidOperationException("At least one day must be selected");

        if (schedule.DurationMinutes <= 0)
            throw new InvalidOperationException("Duration must be greater than zero");

        await _repo.SaveAsync(schedule);
    }
}
