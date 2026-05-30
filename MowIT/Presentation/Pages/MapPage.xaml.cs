using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using MowIT.Domain.Entities;
using MowIT.Presentation.ViewModels;
using NetTopologySuite.Geometries;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;

namespace MowIT.Presentation.Pages;

public partial class MapPage : ContentPage
{
    private readonly MapViewModel _vm;
    private WritableLayer? _robotLayer;
    private WritableLayer? _boundaryLayer;
    private WritableLayer? _trailLayer;
    private WritableLayer? _savedZonesLayer;
    private bool _mapCentred;

    public MapPage(MapViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;

        SetupMap();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MapViewModel.RobotPosition))
                UpdateRobotPin();
        };
        _vm.BoundaryPoints.CollectionChanged += (_, _) => UpdateBoundary();
        _vm.RobotTrail.CollectionChanged     += (_, _) => UpdateTrail();
        _vm.SavedZones.CollectionChanged     += (_, _) => UpdateSavedZones();
    }

    private void SetupMap()
    {
        MapView.Map.Layers.Add(OpenStreetMap.CreateTileLayer());

        _savedZonesLayer = new WritableLayer { Name = "SavedZones", Style = null };
        _trailLayer      = new WritableLayer { Name = "Trail",       Style = null };
        _boundaryLayer   = new WritableLayer { Name = "Boundary",    Style = null };
        _robotLayer      = new WritableLayer { Name = "Robot",       Style = null };

        MapView.Map.Layers.Add(_savedZonesLayer);
        MapView.Map.Layers.Add(_trailLayer);
        MapView.Map.Layers.Add(_boundaryLayer);
        MapView.Map.Layers.Add(_robotLayer);

        MapView.Map.Info += OnMapInfo;
    }

    private static MPoint ToMPoint(GpsPoint p)
    {
        var (x, y) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude);
        return new MPoint(x, y);
    }

    private static Coordinate ToCoord(GpsPoint p)
    {
        var (x, y) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude);
        return new Coordinate(x, y);
    }

    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
        if (!_vm.IsDrawingMode) return;
        var wp = e.MapInfo?.WorldPosition;
        if (wp is null) return;
        var (lon, lat) = SphericalMercator.ToLonLat(wp.X, wp.Y);
        MainThread.BeginInvokeOnMainThread(() =>
            _vm.OnMapTapped(new GpsPoint(lat, lon)));
    }

    private void UpdateRobotPin()
    {
        if (_robotLayer is null) return;
        _robotLayer.Clear();

        var pos = _vm.RobotPosition;
        var pt  = ToMPoint(pos);
        var feature = new PointFeature(pt);
        feature.Styles.Add(new SymbolStyle
        {
            Fill        = new MBrush(MColor.Red),
            Outline     = new Pen(MColor.White, 2),
            SymbolScale = 0.8,
            SymbolType  = SymbolType.Ellipse
        });
        _robotLayer.Add(feature);

        if (!_mapCentred && (pos.Latitude != 0 || pos.Longitude != 0))
        {
            _mapCentred = true;
            MapView.Map.Navigator.CenterOn(pt);
            MapView.Map.Navigator.ZoomTo(3.0);
        }

        MapView.RefreshGraphics();
    }

    private void UpdateBoundary()
    {
        if (_boundaryLayer is null) return;
        _boundaryLayer.Clear();

        var pts = _vm.BoundaryPoints;

        if (pts.Count >= 3)
        {
            var coords = pts.Select(ToCoord).ToList();
            coords.Add(coords[0]);
            var polygon = new GeometryFeature(new Polygon(new LinearRing(coords.ToArray())));
            polygon.Styles.Add(new VectorStyle
            {
                Fill = new MBrush(MColor.FromArgb(50, 0, 200, 0)),
                Line = new Pen(MColor.FromArgb(220, 50, 205, 50), 3)
            });
            _boundaryLayer.Add(polygon);
        }
        else if (pts.Count == 2)
        {
            var line = new GeometryFeature(new LineString(pts.Select(ToCoord).ToArray()));
            line.Styles.Add(new VectorStyle
            {
                Fill = null,
                Line = new Pen(MColor.FromArgb(220, 50, 205, 50), 3)
            });
            _boundaryLayer.Add(line);
        }

        foreach (var p in pts)
        {
            var dot = new PointFeature(ToMPoint(p));
            dot.Styles.Add(new SymbolStyle
            {
                Fill        = new MBrush(MColor.FromArgb(255, 50, 205, 50)),
                Outline     = new Pen(MColor.White, 1),
                SymbolScale = 0.4,
                SymbolType  = SymbolType.Ellipse
            });
            _boundaryLayer.Add(dot);
        }

        MapView.RefreshGraphics();
    }

    private void UpdateTrail()
    {
        if (_trailLayer is null) return;
        _trailLayer.Clear();

        var trail = _vm.RobotTrail;
        if (trail.Count >= 2)
        {
            var line = new GeometryFeature(new LineString(trail.Select(ToCoord).ToArray()));
            line.Styles.Add(new VectorStyle
            {
                Fill = null,
                Line = new Pen(MColor.FromArgb(200, 33, 150, 243), 3)
            });
            _trailLayer.Add(line);
        }

        MapView.RefreshGraphics();
    }

private void UpdateSavedZones()
    {
        if (_savedZonesLayer is null) return;
        _savedZonesLayer.Clear();

        foreach (var zone in _vm.SavedZones)
        {
            if (zone.Points.Count < 3) continue;

            var coords = zone.Points.Select(ToCoord).ToList();
            coords.Add(coords[0]);

            var polygon = new GeometryFeature(new Polygon(new LinearRing(coords.ToArray())));
            polygon.Styles.Add(new VectorStyle
            {
                Fill = new MBrush(MColor.FromArgb(25, 94, 140, 97)),
                Line = new Pen(MColor.FromArgb(140, 94, 140, 97), 2)
            });
            _savedZonesLayer.Add(polygon);
        }

        MapView.RefreshGraphics();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnAppearingAsync();
        UpdateSavedZones();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.OnDisappearing();
    }
}
