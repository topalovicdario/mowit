using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MowIT.Application.Logging;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;
using MowIT.Shared.Schedules;

namespace MowIT.Infrastructure.ScheduleSync;

public sealed class HttpScheduleSyncService : IScheduleSyncService
{
    private readonly HttpClient _http;
    private readonly ScheduleSyncOptions _opts;
    private readonly ILogger<HttpScheduleSyncService> _logger;
    private readonly EventLogService _evt;
    private const string Source = "SYNC";

    public HttpScheduleSyncService(
        HttpClient http,
        IOptions<ScheduleSyncOptions> opts,
        ILogger<HttpScheduleSyncService> logger,
        EventLogService evt)
    {
        _http   = http;
        _opts   = opts.Value;
        _logger = logger;
        _evt    = evt;

        if (_opts.IsConfigured)
        {
            _http.BaseAddress = new Uri(_opts.BaseUrl);
            _http.Timeout     = _opts.Timeout;
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _opts.BearerToken);
        }
    }

    public bool IsEnabled => _opts.IsConfigured;

    public async Task PushAsync(IReadOnlyList<MowingSchedule> schedules, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("Schedule sync skipped - not configured");
            return;
        }

        var payload = new ScheduleUploadRequest
        {
            Schedules = schedules.Select(ToDto).ToArray()
        };

        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"/robots/{Uri.EscapeDataString(_opts.RobotId)}/schedules",
                payload, ct);

            if (resp.IsSuccessStatusCode)
            {
                _evt.Info(Source, $"pushed {schedules.Count} schedule(s) to server");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _evt.Warn(Source, $"push failed ({(int)resp.StatusCode}) {body}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schedule push failed");
            _evt.Warn(Source, $"push exception: {ex.Message}");
        }
    }

    public async Task DeleteAsync(int scheduleId, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        try
        {
            var resp = await _http.DeleteAsync(
                $"/robots/{Uri.EscapeDataString(_opts.RobotId)}/schedules/{scheduleId}", ct);

            if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                _evt.Info(Source, $"deleted schedule {scheduleId} on server");
            else
                _evt.Warn(Source, $"delete failed ({(int)resp.StatusCode})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schedule delete failed");
            _evt.Warn(Source, $"delete exception: {ex.Message}");
        }
    }

    private static ScheduleDto ToDto(MowingSchedule s) => new()
    {
        Id              = s.Id,
        ActiveDays      = s.ActiveDays.Select(d => (int)d).ToArray(),
        StartTimeTicks  = s.StartTime.Ticks,
        DurationMinutes = s.DurationMinutes,
        IsActive        = s.IsActive,
        ZoneName        = s.ZoneName,
        ZoneId          = s.ZoneId,
        LastExecutedUtc = s.LastExecuted.Kind == DateTimeKind.Utc
            ? s.LastExecuted
            : s.LastExecuted.ToUniversalTime()
    };
}
