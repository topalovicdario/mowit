using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Maui.Dispatching;
using MowIT.Domain.Entities;
using MowIT.Presentation.Controls;
using MowIT.Presentation.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MowIT.Presentation.Pages;

public partial class ControlPage : ContentPage
{
    private readonly ControlViewModel _vm;

    private IDispatcherTimer? _animTimer;
    private float _dispX, _dispY, _dispHeading;
    private bool  _hasDisp;

    private static readonly SKColor RouteColor = new(0xFF, 0x98, 0x00, 0xE6);

    private static readonly SKColor BgTop      = new(0xF6, 0xF8, 0xF2);
    private static readonly SKColor BgBottom   = new(0xE8, 0xEE, 0xE3);
    private static readonly SKColor GridMinor  = new(0xC8, 0xD0, 0xC0, 0x60);
    private static readonly SKColor GridMajor  = new(0x90, 0xA0, 0x88, 0xA0);
    private static readonly SKColor PolyFill   = new(0x4A, 0x7C, 0x59, 0x55);
    private static readonly SKColor PolyStroke = new(0x4A, 0x7C, 0x59, 0xFF);
    private static readonly SKColor ActiveDot  = new(0x6B, 0x8E, 0x4E, 0xFF);
    private static readonly SKColor MowerBody  = new(0xE5, 0x39, 0x35, 0xFF);
    private static readonly SKColor MowerStroke = SKColors.White;
    private static readonly SKColor TextColor  = new(0x4A, 0x55, 0x42, 0xFF);

    public ControlPage(ControlViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;

        _vm.PropertyChanged += OnVmChanged;
        _vm.ClosedPolygons.CollectionChanged += OnCollectionChanged;
        _vm.ActivePolygon.CollectionChanged  += OnCollectionChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ControlViewModel.HasDatum):
            case nameof(ControlViewModel.IsRecordingBoundary):
            case nameof(ControlViewModel.IsPlanMode):
            case nameof(ControlViewModel.PlanRevision):
                LocalMap.InvalidateSurface();
                break;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => LocalMap.InvalidateSurface();

    private void OnLocalMapPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info   = e.Info;
        canvas.Clear(BgTop);

        using (var bgPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, info.Height),
                new[] { BgTop, BgBottom },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(0, 0, info.Width, info.Height, bgPaint);
        }

        var bounds = ComputeBounds();
        if (bounds is null)
        {
            DrawEmptyHint(canvas, info);
            return;
        }

        var (minX, minY, maxX, maxY) = bounds.Value;

        const float marginCm = 200f;
        minX -= marginCm; maxX += marginCm;
        minY -= marginCm; maxY += marginCm;

        const float minSpanCm = 1000f;
        if (maxX - minX < minSpanCm) { var c = (minX + maxX) / 2; minX = c - minSpanCm / 2; maxX = c + minSpanCm / 2; }
        if (maxY - minY < minSpanCm) { var c = (minY + maxY) / 2; minY = c - minSpanCm / 2; maxY = c + minSpanCm / 2; }

        const float padPx = 16f;
        float availW = info.Width  - 2 * padPx;
        float availH = info.Height - 2 * padPx;
        float scaleX = availW / (maxX - minX);
        float scaleY = availH / (maxY - minY);
        float scale  = Math.Min(scaleX, scaleY);

        float worldWpx = (maxX - minX) * scale;
        float worldHpx = (maxY - minY) * scale;
        float offX     = (info.Width  - worldWpx) / 2f - minX * scale;
        float offY     = (info.Height + worldHpx) / 2f + minY * scale;

        SKPoint W2S(LocalPoint p) => new(p.XCm * scale + offX, -p.YCm * scale + offY);

        DrawGrid(canvas, info, scale, offX, offY, minX, minY, maxX, maxY);
        DrawDatum(canvas, W2S(new LocalPoint(0, 0)));
        if (_vm.IsPlanMode)
        {
            DrawPlanRoute(canvas, W2S);
            DrawPlanPoints(canvas, W2S);
        }
        else
        {
            DrawClosedPolygons(canvas, W2S);
            DrawActivePolygon(canvas, W2S);
        }
        DrawMower(canvas, W2S);
        DrawAxes(canvas, info);
        DrawScaleBar(canvas, info, scale);
    }

    private (float minX, float minY, float maxX, float maxY)? ComputeBounds()
    {
        bool any = false;
        float minX = 0, minY = 0, maxX = 0, maxY = 0;

        void Visit(LocalPoint p)
        {
            if (!any) { minX = maxX = p.XCm; minY = maxY = p.YCm; any = true; }
            else { minX = Math.Min(minX, p.XCm); maxX = Math.Max(maxX, p.XCm);
                   minY = Math.Min(minY, p.YCm); maxY = Math.Max(maxY, p.YCm); }
        }

        Visit(new LocalPoint(0, 0));
        foreach (var poly in _vm.ClosedPolygons)
            foreach (var p in poly) Visit(p);
        foreach (var p in _vm.ActivePolygon) Visit(p);
        foreach (var p in _vm.PlanPointsLocal) Visit(p);
        if (_vm.MowerLocal is LocalPoint mw) Visit(mw);

        return any ? (minX, minY, maxX, maxY) : null;
    }

    private static void DrawEmptyHint(SKCanvas canvas, SKImageInfo info)
    {
        using var paint = new SKPaint
        {
            Color = TextColor,
            TextSize = 13,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
            Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold)
        };
        canvas.DrawText("Capture base + start recording to see the local map",
            info.Width / 2f, info.Height / 2f, paint);
    }

    private static void DrawGrid(SKCanvas canvas, SKImageInfo info, float scale,
                                 float offX, float offY,
                                 float minX, float minY, float maxX, float maxY)
    {
        float[] candidates = { 50, 100, 200, 500, 1000, 2000, 5000 };
        float worldSpan = Math.Max(maxX - minX, maxY - minY);
        float step = candidates[^1];
        foreach (var c in candidates) { if (worldSpan / c <= 10) { step = c; break; } }

        using var minorPaint = new SKPaint { Color = GridMinor, StrokeWidth = 1, IsAntialias = false };
        using var majorPaint = new SKPaint { Color = GridMajor, StrokeWidth = 1, IsAntialias = false };

        float startX = (float)Math.Floor(minX / step) * step;
        for (float x = startX; x <= maxX; x += step)
        {
            float sx = x * scale + offX;
            var paint = (Math.Abs(x) < 0.5f) ? majorPaint : minorPaint;
            canvas.DrawLine(sx, 0, sx, info.Height, paint);
        }
        float startY = (float)Math.Floor(minY / step) * step;
        for (float y = startY; y <= maxY; y += step)
        {
            float sy = -y * scale + offY;
            var paint = (Math.Abs(y) < 0.5f) ? majorPaint : minorPaint;
            canvas.DrawLine(0, sy, info.Width, sy, paint);
        }
    }

    private static void DrawDatum(SKCanvas canvas, SKPoint origin)
    {
        using var paint = new SKPaint { Color = GridMajor, StrokeWidth = 2, IsAntialias = true };
        const float r = 7;
        canvas.DrawLine(origin.X - r, origin.Y, origin.X + r, origin.Y, paint);
        canvas.DrawLine(origin.X, origin.Y - r, origin.X, origin.Y + r, paint);
        using var ring = new SKPaint { Color = GridMajor, StrokeWidth = 1.5f, IsAntialias = true, IsStroke = true };
        canvas.DrawCircle(origin, 9, ring);
    }

    private void DrawClosedPolygons(SKCanvas canvas, Func<LocalPoint, SKPoint> w2s)
    {
        using var fill   = new SKPaint { Color = PolyFill, IsAntialias = true };
        using var stroke = new SKPaint { Color = PolyStroke, StrokeWidth = 2, IsAntialias = true, IsStroke = true };

        foreach (var poly in _vm.ClosedPolygons)
        {
            if (poly.Count < 3) continue;
            using var path = new SKPath();
            var p0 = w2s(poly[0]);
            path.MoveTo(p0);
            for (int i = 1; i < poly.Count; i++) path.LineTo(w2s(poly[i]));
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }
    }

    private void DrawActivePolygon(SKCanvas canvas, Func<LocalPoint, SKPoint> w2s)
    {
        if (_vm.ActivePolygon.Count == 0) return;

        using var line = new SKPaint
        {
            Color = ActiveDot, StrokeWidth = 2, IsAntialias = true, IsStroke = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0)
        };
        using var dot = new SKPaint { Color = ActiveDot, IsAntialias = true };

        if (_vm.ActivePolygon.Count >= 2)
        {
            using var path = new SKPath();
            path.MoveTo(w2s(_vm.ActivePolygon[0]));
            for (int i = 1; i < _vm.ActivePolygon.Count; i++)
                path.LineTo(w2s(_vm.ActivePolygon[i]));
            canvas.DrawPath(path, line);
        }

        foreach (var p in _vm.ActivePolygon)
            canvas.DrawCircle(w2s(p), 4, dot);
    }

    private void DrawPlanPoints(SKCanvas canvas, Func<LocalPoint, SKPoint> w2s)
    {
        var pts = _vm.PlanPointsLocal;
        if (pts.Count == 0) return;

        using var line = new SKPaint
        {
            Color = ActiveDot, StrokeWidth = 2, IsAntialias = true, IsStroke = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0)
        };
        using var dot = new SKPaint { Color = ActiveDot, IsAntialias = true };

        if (pts.Count >= 2)
        {
            using var path = new SKPath();
            path.MoveTo(w2s(pts[0]));
            for (int i = 1; i < pts.Count; i++) path.LineTo(w2s(pts[i]));
            if (pts.Count >= 3) path.Close();
            canvas.DrawPath(path, line);
        }

        foreach (var p in pts) canvas.DrawCircle(w2s(p), 5, dot);
    }

    private void DrawPlanRoute(SKCanvas canvas, Func<LocalPoint, SKPoint> w2s)
    {
        var route = _vm.PlanRoute;
        if (route.Count < 2) return;

        using var line = new SKPaint
        {
            Color = RouteColor, StrokeWidth = 2, IsAntialias = true, IsStroke = true
        };
        using var path = new SKPath();
        path.MoveTo(w2s(route[0]));
        for (int i = 1; i < route.Count; i++) path.LineTo(w2s(route[i]));
        canvas.DrawPath(path, line);
    }

    private void DrawMower(SKCanvas canvas, Func<LocalPoint, SKPoint> w2s)
    {
        if (!_hasDisp) return;
        var s = w2s(new LocalPoint(_dispX, _dispY));

        using var body   = new SKPaint { Color = MowerBody, IsAntialias = true };
        using var stroke = new SKPaint { Color = MowerStroke, StrokeWidth = 2.5f, IsAntialias = true, IsStroke = true };
        canvas.DrawCircle(s, 8, body);
        canvas.DrawCircle(s, 8, stroke);

        float h  = _dispHeading;
        float dx = (float)Math.Cos(h) * 16;
        float dy = -(float)Math.Sin(h) * 16;
        using var arrow = new SKPaint { Color = MowerBody, StrokeWidth = 3, IsAntialias = true, IsStroke = true, StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(s.X, s.Y, s.X + dx, s.Y + dy, arrow);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (_vm.MowerLocal is LocalPoint mw)
        {
            _dispX = mw.XCm; _dispY = mw.YCm; _hasDisp = true;
        }
        _dispHeading = _vm.MowerHeadingRad;

        _animTimer ??= CreateAnimTimer();
        _animTimer.Start();
        LocalMap.InvalidateSurface();
    }

    private IDispatcherTimer CreateAnimTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(33);
        timer.Tick += (_, _) => AnimStep();
        return timer;
    }

    private void AnimStep()
    {
        if (_vm.MowerLocal is not LocalPoint target) return;

        if (!_hasDisp)
        {
            _dispX = target.XCm; _dispY = target.YCm;
            _dispHeading = _vm.MowerHeadingRad; _hasDisp = true;
            LocalMap.InvalidateSurface();
            return;
        }

        float ddx = target.XCm - _dispX;
        float ddy = target.YCm - _dispY;
        float dh  = NormalizeAngle(_vm.MowerHeadingRad - _dispHeading);

        if (ddx * ddx + ddy * ddy < 0.04f && Math.Abs(dh) < 0.002f)
            return;

        const float k = 0.25f;
        _dispX       += ddx * k;
        _dispY       += ddy * k;
        _dispHeading += dh  * k;
        LocalMap.InvalidateSurface();
    }

    private static float NormalizeAngle(float a)
    {
        while (a >  MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    private static void DrawAxes(SKCanvas canvas, SKImageInfo info)
    {
        using var label = new SKPaint
        {
            Color = TextColor, TextSize = 11, IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold)
        };
        canvas.DrawText("N", info.Width / 2f - 4, 14, label);
        canvas.DrawText("E", info.Width - 14, info.Height / 2f + 4, label);
    }

    private static void DrawScaleBar(SKCanvas canvas, SKImageInfo info, float pxPerCm)
    {
        float[] meters = { 1, 2, 5, 10, 20, 50, 100, 200 };
        float chosen = meters[0];
        foreach (var m in meters)
        {
            float w = m * 100f * pxPerCm;
            if (w >= 60 && w <= 140) { chosen = m; break; }
            if (w > 140) break;
            chosen = m;
        }
        float barW = chosen * 100f * pxPerCm;
        float x0 = info.Width - barW - 14;
        float y0 = info.Height - 16;

        using var bar  = new SKPaint { Color = TextColor, StrokeWidth = 2, IsAntialias = true, IsStroke = true };
        canvas.DrawLine(x0, y0, x0 + barW, y0, bar);
        canvas.DrawLine(x0, y0 - 4, x0, y0 + 4, bar);
        canvas.DrawLine(x0 + barW, y0 - 4, x0 + barW, y0 + 4, bar);

        using var label = new SKPaint
        {
            Color = TextColor, TextSize = 10, IsAntialias = true,
            TextAlign = SKTextAlign.Right
        };
        string text = chosen >= 1 ? $"{chosen:0} m" : $"{chosen * 100:0} cm";
        canvas.DrawText(text, x0 + barW, y0 - 6, label);
    }

    private void OnJoystickMoved(object? sender, JoystickEventArgs e)
        => _vm.OnJoystickMoved(e.NormalizedX, e.NormalizedY);

    private void OnJoystickReleased(object? sender, EventArgs e)
        => _vm.OnJoystickReleased();

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _animTimer?.Stop();
        _vm.OnDisappearing();
    }
}
