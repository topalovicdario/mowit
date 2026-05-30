using System.Collections.Specialized;
using MowIT.Presentation.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MowIT.Presentation.Pages;

public partial class DashboardPage : ContentPage
{
    private readonly DashboardViewModel _vm;
    private NotifyCollectionChangedEventHandler? _accelChartHandler;

    public DashboardPage(DashboardViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnAppearingAsync();
        _accelChartHandler = (_, _) => AccelChart.InvalidateSurface();
        _vm.AccXHistory.CollectionChanged += _accelChartHandler;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_accelChartHandler is not null)
        {
            _vm.AccXHistory.CollectionChanged -= _accelChartHandler;
            _accelChartHandler = null;
        }
        _vm.OnDisappearing();
    }

    private void OnPaintAccelChart(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info   = e.Info;
        canvas.Clear(SKColors.Transparent);

        var data = _vm.AccXHistory;
        if (data.Count < 2) return;

        float w = info.Width;
        float h = info.Height;
        float pad = 4f;

        float min = data.Min();
        float max = data.Max();
        float range = max - min;
        if (range < 0.01f) range = 0.01f;

        using var linePaint = new SKPaint
        {
            Color       = SKColor.Parse("#4CAF50"),
            StrokeWidth = 2f,
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke
        };

        using var zeroPaint = new SKPaint
        {
            Color       = SKColor.Parse("#CCCCCC"),
            StrokeWidth = 1f,
            PathEffect  = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0)
        };

float zeroY = h - pad - (0f - min) / range * (h - 2 * pad);
        if (zeroY >= pad && zeroY <= h - pad)
            canvas.DrawLine(pad, zeroY, w - pad, zeroY, zeroPaint);

var path = new SKPath();
        for (int i = 0; i < data.Count; i++)
        {
            float x = pad + i / (float)(data.Count - 1) * (w - 2 * pad);
            float y = h - pad - (data[i] - min) / range * (h - 2 * pad);
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }
        canvas.DrawPath(path, linePaint);
    }
}
