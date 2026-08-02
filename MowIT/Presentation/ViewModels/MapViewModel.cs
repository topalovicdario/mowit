using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MowIT.Application.Services;
using MowIT.Application.UseCases;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
using MowIT.Presentation.ViewModels.Base;

namespace MowIT.Presentation.ViewModels;

public partial class MapViewModel : BaseViewModel
{
    private readonly IRobotSensors       _sensors;
    private readonly IRobotBoundary      _boundary;
    private readonly IRobotControl       _control;
    private readonly IBoundaryRepository _repo;
    private readonly SendBoundaryUseCase _sendBoundary;
    private readonly MowingRoutePlanner  _planner;
    private IDisposable? _sensorSub;
    private int _currentZoneId;

    public ObservableCollection<GpsPoint>     BoundaryPoints { get; } = new();
    public ObservableCollection<GpsPoint>     RobotTrail     { get; } = new();
    public ObservableCollection<BoundaryZone> SavedZones     { get; } = new();
    public List<GpsPoint>                     RoutePoints    { get; } = new();

    [ObservableProperty] private GpsPoint  _robotPosition;
    [ObservableProperty] private float     _robotHeadingDeg;
    [ObservableProperty] private bool      _isDrawingMode;
    [ObservableProperty] private bool      _isSendingBoundary;
    [ObservableProperty] private int       _sendProgress;
    [ObservableProperty] private string    _zoneName = "My Garden";
    [ObservableProperty] private bool      _showZonesPanel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStrategyName))]
    [NotifyPropertyChangedFor(nameof(IsStrategy0))]
    [NotifyPropertyChangedFor(nameof(IsStrategy1))]
    private int _strategyIndex;

    public bool IsStrategy0 => StrategyIndex == 0;
    public bool IsStrategy1 => StrategyIndex == 1;

    [ObservableProperty] private string _routeInfo = "No route - tap Calc Route";

    [ObservableProperty] private int _routeVersion;

    public string SelectedStrategyName => _planner.StrategyName(StrategyIndex);

    public bool CanSendBoundary  => BoundaryPoints.Count >= 3 && !IsSendingBoundary;
    public bool CanSaveLocally   => BoundaryPoints.Count >= 3 && !IsBusy;
    public bool CanCalculateRoute => BoundaryPoints.Count >= 3;
    public bool CanSendRoute      => RoutePoints.Count > 0 && !IsSendingBoundary;

    public MapViewModel(
        IRobotSensors sensors,
        IRobotBoundary boundary,
        IRobotControl control,
        IBoundaryRepository repo,
        SendBoundaryUseCase sendBoundary,
        MowingRoutePlanner planner)
    {
        _sensors      = sensors;
        _boundary     = boundary;
        _control      = control;
        _repo         = repo;
        _sendBoundary = sendBoundary;
        _planner      = planner;
        Title = "Map & Boundary";

        _sensorSub = _sensors.SensorStream
            .Sample(TimeSpan.FromSeconds(1))
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

    [RelayCommand]
    private void SelectStrategy(string indexText)
    {
        if (!int.TryParse(indexText, out int idx)) return;
        if (_planner.Strategies.Count == 0) return;

        StrategyIndex = idx % _planner.Strategies.Count;
        _planner.SelectedIndex = StrategyIndex;

        if (RoutePoints.Count > 0) CalculateRouteCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanCalculateRoute))]
    private async Task CalculateRouteAsync()
    {
        var points = BoundaryPoints.ToList();
        if (points.Count < 3) return;

        int index = StrategyIndex;
        var zone  = new BoundaryZone { Name = ZoneName, Points = points };

        var route = await Task.Run(() => _planner.Plan(zone, index));

        double meters = 0;
        for (int i = 1; i < route.Count; i++) meters += route[i - 1].DistanceTo(route[i]);

        RoutePoints.Clear();
        RoutePoints.AddRange(route);
        RouteVersion++;

        RouteInfo = route.Count == 0
            ? "No route - draw a larger area"
            : $"{route.Count} waypoints, {meters:F0} m, {_planner.StrategyName(index)}";

        OnPropertyChanged(nameof(CanSendRoute));
        SendRouteToRobotCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSendRoute))]
    private async Task SendRouteToRobotAsync()
    {
        await RunSafeAsync(async () =>
        {
            IsSendingBoundary = true;
            SendProgress      = 0;
            OnPropertyChanged(nameof(CanSendRoute));
            SendRouteToRobotCommand.NotifyCanExecuteChanged();

            var progress = new Progress<int>(p => SendProgress = p);

            await _boundary.SendRouteAsync(RoutePoints.ToList(), progress);
            await _control.SendActionAsync(RobotAction.StartMowing);
        }, "Send route failed");

        IsSendingBoundary = false;
        OnPropertyChanged(nameof(CanSendRoute));
        SendRouteToRobotCommand.NotifyCanExecuteChanged();
    }

    public override void OnDisappearing() { }

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
        if (RoutePoints.Count > 0)
        {
            RoutePoints.Clear();
            RouteVersion++;
            RouteInfo = "No route - tap Calc Route";
        }

        OnPropertyChanged(nameof(CanSendBoundary));
        OnPropertyChanged(nameof(CanSaveLocally));
        OnPropertyChanged(nameof(CanCalculateRoute));
        OnPropertyChanged(nameof(CanSendRoute));
        CalculateRouteCommand.NotifyCanExecuteChanged();
        SendRouteToRobotCommand.NotifyCanExecuteChanged();
    }
}
