using Microsoft.Extensions.Logging;
using MowIT.Application.UseCases;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.Services;

public sealed class MowingSchedulerService : IDisposable
{
    private readonly IScheduleRepository    _repo;
    private readonly IRobotConnection         _connection;
    private readonly SendZoneToRobotUseCase _sendZone;
    private readonly ILogger<MowingSchedulerService> _logger;
    private readonly Timer _timer;
    private bool _ticking; 

    public MowingSchedulerService(
        IScheduleRepository    repo,
        IRobotConnection         connection,
        SendZoneToRobotUseCase sendZone,
        ILogger<MowingSchedulerService> logger)
    {
        _repo       = repo;
        _connection = connection;
        _sendZone   = sendZone;
        _logger     = logger;

}

    private async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            var now       = DateTime.Now;
            var schedules = await _repo.GetAllAsync();

            foreach (var schedule in schedules)
            {
                if (!schedule.IsActive) continue;
                if (!schedule.ActiveDays.Contains(now.DayOfWeek)) continue;

var diff = now.TimeOfDay - schedule.StartTime;
                if (diff.TotalMinutes is < 0 or >= 1) continue;

if (schedule.LastExecuted.Date == now.Date
                    && schedule.LastExecuted.TimeOfDay >= schedule.StartTime) continue;

                if (!_connection.IsConnected)
                {
                    _logger.LogWarning(
                        "Scheduled mow skipped — robot not connected (schedule {Id} \"{Name}\")",
                        schedule.Id, schedule.ZoneName);
                    continue;
                }

                _logger.LogInformation(
                    "Executing schedule {Id} \"{Name}\" at {Time}",
                    schedule.Id, schedule.ZoneName, now.ToString("HH:mm"));

                try
                {
                    if (schedule.ZoneId.HasValue)
                        await _sendZone.ExecuteAsync(schedule.ZoneId.Value, startMowingAfter: true);
                    else
                        await _sendZone.StartMowingOnlyAsync();

                    schedule.LastExecuted = now;
                    await _repo.SaveAsync(schedule);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Scheduled mow failed for schedule {Id} \"{Name}\"",
                        schedule.Id, schedule.ZoneName);
                }
            }
        }
        finally
        {
            _ticking = false;
        }
    }

    public void Dispose() => _timer.Dispose();
}
