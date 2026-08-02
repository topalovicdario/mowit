using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MowIT.Application.UseCases;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;
using MowIT.Presentation.ViewModels.Base;

namespace MowIT.Presentation.ViewModels;

public partial class ScheduleViewModel : BaseViewModel
{
    private readonly IScheduleRepository    _repo;
    private readonly IBoundaryRepository    _boundaryRepo;
    private readonly SendZoneToRobotUseCase _sendZone;

    public ObservableCollection<MowingSchedule> Schedules  { get; } = new();
    public ObservableCollection<BoundaryZone>   SavedZones { get; } = new();

[ObservableProperty] private bool     _monday, _tuesday, _wednesday,
                                          _thursday, _friday, _saturday, _sunday;
    [ObservableProperty] private TimeSpan _startTime       = new(8, 0, 0);
    [ObservableProperty] private string   _scheduleName    = "Weekly Mow";
    [ObservableProperty] private BoundaryZone? _selectedZone;

[ObservableProperty] private bool _isSendingZone;
    [ObservableProperty] private int  _sendProgress;
    [ObservableProperty] private string _sendingZoneLabel = string.Empty;

    public DayOfWeek[] SelectedDays => new[]
    {
        (Monday,    DayOfWeek.Monday),
        (Tuesday,   DayOfWeek.Tuesday),
        (Wednesday, DayOfWeek.Wednesday),
        (Thursday,  DayOfWeek.Thursday),
        (Friday,    DayOfWeek.Friday),
        (Saturday,  DayOfWeek.Saturday),
        (Sunday,    DayOfWeek.Sunday)
    }.Where(x => x.Item1).Select(x => x.Item2).ToArray();

    public double SendFraction  => SendProgress / 100.0;

    public ScheduleViewModel(
        IScheduleRepository repo,
        IBoundaryRepository boundaryRepo,
        SendZoneToRobotUseCase sendZone)
    {
        _repo         = repo;
        _boundaryRepo = boundaryRepo;
        _sendZone     = sendZone;
        Title = "Schedule";
    }

    public override Task OnAppearingAsync()
        => RunSafeAsync(async () =>
        {
            var all   = await _repo.GetAllAsync();
            var zones = await _boundaryRepo.GetAllAsync();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var selectedId = SelectedZone?.Id;

                SavedZones.Clear();
                foreach (var z in zones)
                    SavedZones.Add(z);

                SelectedZone = SavedZones.FirstOrDefault(z => z.Id == selectedId);

                Schedules.Clear();
                foreach (var s in all)
                    Schedules.Add(s);
            });
        }, "Failed to load schedules");

[RelayCommand]
    private void ToggleDay(string day)
    {
        switch (day)
        {
            case "Monday":    Monday    = !Monday;    break;
            case "Tuesday":   Tuesday   = !Tuesday;   break;
            case "Wednesday": Wednesday = !Wednesday; break;
            case "Thursday":  Thursday  = !Thursday;  break;
            case "Friday":    Friday    = !Friday;    break;
            case "Saturday":  Saturday  = !Saturday;  break;
            case "Sunday":    Sunday    = !Sunday;    break;
        }
    }

[RelayCommand]
    private async Task SaveScheduleAsync()
    {
        if (SelectedDays.Length == 0) { ErrorMessage = "Select at least one day"; return; }

        var schedule = new MowingSchedule
        {
            ActiveDays      = SelectedDays,
            StartTime       = StartTime,
            DurationMinutes = 60,
            IsActive        = true,
            ZoneName        = SelectedZone?.Name ?? string.Empty,
            ZoneId          = SelectedZone?.Id
        };

        await RunSafeAsync(async () =>
        {
            await _repo.SaveAsync(schedule);
            await MainThread.InvokeOnMainThreadAsync(() => Schedules.Add(schedule));
        }, "Save failed");
    }

    [RelayCommand]
    private Task ToggleScheduleAsync(MowingSchedule schedule)
        => RunSafeAsync(() => _repo.SaveAsync(schedule));

    [RelayCommand]
    private async Task DeleteScheduleAsync(MowingSchedule schedule)
    {
        await RunSafeAsync(async () =>
        {
            await _repo.DeleteAsync(schedule.Id);
            await MainThread.InvokeOnMainThreadAsync(() => Schedules.Remove(schedule));
        }, "Delete failed");
    }

[RelayCommand]
    private async Task MowNowAsync(MowingSchedule schedule)
    {
        if (IsSendingZone) return;

        await RunSafeAsync(async () =>
        {
            IsSendingZone    = true;
            SendProgress     = 0;
            SendingZoneLabel = schedule.ZoneId.HasValue
                ? $"Sending \"{schedule.ZoneName}\" to robot..."
                : "Starting mow with onboard zone...";

            if (schedule.ZoneId.HasValue)
            {
                var progress = new Progress<int>(p =>
                {
                    SendProgress = p;
                    OnPropertyChanged(nameof(SendFraction));
                });
                await _sendZone.ExecuteAsync(
                    schedule.ZoneId.Value, startMowingAfter: true, progress);
            }
            else
            {
                await _sendZone.StartMowingOnlyAsync();
            }

            SendingZoneLabel = string.Empty;
        }, "Failed to start mowing");

        IsSendingZone = false;
        SendProgress  = 0;
        OnPropertyChanged(nameof(SendFraction));
    }


    partial void OnSendProgressChanged(int value)
        => OnPropertyChanged(nameof(SendFraction));
}
