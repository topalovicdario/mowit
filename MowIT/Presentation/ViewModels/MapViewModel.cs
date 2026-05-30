using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MowIT.Application.UseCases;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;
using MowIT.Presentation.ViewModels.Base;

namespace MowIT.Presentation.ViewModels;

public partial class MapViewModel : BaseViewModel
{
    private readonly IRobotSensors       _sensors;
    private readonly IRobotBoundary      _boundary;
    private readonly IBoundaryRepository _repo;
    private readonly SendBoundaryUseCase _sendBoundary;
    private IDisposable? _sensorSub;
    private int _currentZoneId;

    public ObservableCollection<GpsPoint>     BoundaryPoints { get; } = new();
    public ObservableCollection<GpsPoint>     RobotTrail     { get; } = new();
    public ObservableCollection<BoundaryZone> SavedZones     { get; } = new();

    [ObservableProperty] private GpsPoint  _robotPosition;
    [ObservableProperty] private float     _robotHeadingDeg;
    [ObservableProperty] private bool      _isDrawingMode;
    [ObservableProperty] private bool      _isSendingBoundary;
    [ObservableProperty] private int       _sendProgress;
    [ObservableProperty] private string    _zoneName = "My Garden";
    [ObservableProperty] private bool      _showZonesPanel;

    public bool CanSendBoundary => BoundaryPoints.Count >= 3 && !IsSendingBoundary;
    public bool CanSaveLocally  => BoundaryPoints.Count >= 3 && !IsBusy;

    public MapViewModel(
        IRobotSensors sensors,
        IRobotBoundary boundary,
        IBoundaryRepository repo,
        SendBoundaryUseCase sendBoundary)
    {
        _sensors      = sensors;
        _boundary     = boundary;
        _repo         = repo;
        _sendBoundary = sendBoundary;
        Title = "Map & Boundary";

        _sensorSub = _sensors.SensorStream
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
            {
                RobotPosition   = s.Gps;
                RobotHeadingDeg = s.HeadingRad * 180f / MathF.PI;

                if (RobotTrail.Count == 0 || RobotTrail[^1].DistanceTo(s.Gps) > 0.3)
                    RobotTrail.Add(s.Gps);
            }));
    }

    public override async Task OnAppearingAsync() => await RefreshZonesAsync();

[RelayCommand]
    private void ToggleDrawingMode() => IsDrawingMode = !IsDrawingMode;

    public void OnMapTapped(GpsPoint tappedPoint)
    {
        if (!IsDrawingMode) return;
        BoundaryPoints.Add(tappedPoint);
        NotifyBoundaryChanged();
    }

    [RelayCommand]
    private void UndoLastPoint()
    {
        if (BoundaryPoints.Count > 0)
        {
            BoundaryPoints.RemoveAt(BoundaryPoints.Count - 1);
            NotifyBoundaryChanged();
        }
    }

    [RelayCommand]
    private void ClearBoundaryPoints()
    {
        BoundaryPoints.Clear();
        _currentZoneId = 0;
        NotifyBoundaryChanged();
    }

[RelayCommand]
    private void ToggleZonesPanel() => ShowZonesPanel = !ShowZonesPanel;

    [RelayCommand]
    private void LoadZone(BoundaryZone zone)
    {
        _currentZoneId = zone.Id;
        ZoneName       = zone.Name;
        BoundaryPoints.Clear();
        foreach (var p in zone.Points)
            BoundaryPoints.Add(p);
        ShowZonesPanel = false;
        NotifyBoundaryChanged();
    }

    [RelayCommand]
    private async Task DeleteZoneAsync(BoundaryZone zone)
    {
        await RunSafeAsync(async () =>
        {
            await _repo.DeleteAsync(zone.Id);
            await MainThread.InvokeOnMainThreadAsync(() => SavedZones.Remove(zone));
            if (_currentZoneId == zone.Id) _currentZoneId = 0;
        }, "Delete failed");
    }

[RelayCommand]
    private async Task SaveZoneLocallyAsync()
    {
        if (BoundaryPoints.Count < 3) { ErrorMessage = "Need at least 3 points"; return; }

        await RunSafeAsync(async () =>
        {
            var zone = new BoundaryZone
            {
                Id     = _currentZoneId,
                Name   = ZoneName,
                Points = BoundaryPoints.ToList()
            };
            await _repo.SaveAsync(zone);
            _currentZoneId = zone.Id;
            await RefreshZonesAsync();
        }, "Save failed");

        NotifyBoundaryChanged();
    }

[RelayCommand(CanExecute = nameof(CanSendBoundary))]
    private async Task SendBoundaryToRobotAsync()
    {
        await RunSafeAsync(async () =>
        {
            IsSendingBoundary = true;
            SendProgress      = 0;

            var zone = new BoundaryZone
            {
                Id     = _currentZoneId,
                Name   = ZoneName,
                Points = BoundaryPoints.ToList()
            };
            await _repo.SaveAsync(zone);
            _currentZoneId = zone.Id;
            await RefreshZonesAsync();

            var progress = new Progress<int>(p =>
            {
                SendProgress = p;
                OnPropertyChanged(nameof(CanSendBoundary));
            });
            await _boundary.SendBoundaryAsync(zone, progress);
        }, "Send boundary failed");

        IsSendingBoundary = false;
        OnPropertyChanged(nameof(CanSendBoundary));
    }

    public override void OnDisappearing() => _sensorSub?.Dispose();

private async Task RefreshZonesAsync()
    {
        var zones = await _repo.GetAllAsync();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            SavedZones.Clear();
            foreach (var z in zones) SavedZones.Add(z);
        });
    }

    private void NotifyBoundaryChanged()
    {
        OnPropertyChanged(nameof(CanSendBoundary));
        OnPropertyChanged(nameof(CanSaveLocally));
    }
}
