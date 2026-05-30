using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace MowIT.Presentation.Controls;

public class JoystickEventArgs : EventArgs
{
    public float NormalizedX { get; }
    public float NormalizedY { get; }
    public JoystickEventArgs(float x, float y) { NormalizedX = x; NormalizedY = y; }
}

public class JoystickView : SKCanvasView
{
    public static readonly BindableProperty ThumbColorProperty =
        BindableProperty.Create(nameof(ThumbColor), typeof(Color), typeof(JoystickView),
            Colors.Green, propertyChanged: (b, _, _) => ((JoystickView)b).InvalidateSurface());

    public static readonly BindableProperty BackgroundCircleColorProperty =
        BindableProperty.Create(nameof(BackgroundCircleColor), typeof(Color), typeof(JoystickView),
            Color.FromArgb("#E0E0E0"), propertyChanged: (b, _, _) => ((JoystickView)b).InvalidateSurface());

    public Color ThumbColor
    {
        get => (Color)GetValue(ThumbColorProperty);
        set => SetValue(ThumbColorProperty, value);
    }

    public Color BackgroundCircleColor
    {
        get => (Color)GetValue(BackgroundCircleColorProperty);
        set => SetValue(BackgroundCircleColorProperty, value);
    }

    public event EventHandler<JoystickEventArgs>? JoystickMoved;
    public event EventHandler? JoystickReleased;

    private SKPoint _thumbOffset;
    private SKPoint _center;
    private float   _maxRadius;

    public JoystickView()
    {
        EnableTouchEvents = true;
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear();

        _center    = new SKPoint(e.Info.Width / 2f, e.Info.Height / 2f);
        _maxRadius = Math.Min(e.Info.Width, e.Info.Height) / 2f - 20f;

using var bgPaint = new SKPaint
        {
            Color = BackgroundCircleColor.ToSKColor(),
            IsAntialias = true
        };
        canvas.DrawCircle(_center, _maxRadius, bgPaint);

using var linePaint = new SKPaint
        {
            Color       = SKColor.Parse("#BDBDBD"),
            StrokeWidth = 1.5f,
            IsAntialias = true,
            IsStroke    = true
        };
        canvas.DrawLine(_center.X - _maxRadius, _center.Y, _center.X + _maxRadius, _center.Y, linePaint);
        canvas.DrawLine(_center.X, _center.Y - _maxRadius, _center.X, _center.Y + _maxRadius, linePaint);

using var ringPaint = new SKPaint
        {
            Color       = ThumbColor.ToSKColor().WithAlpha(60),
            StrokeWidth = 2,
            IsAntialias = true,
            IsStroke    = true
        };
        canvas.DrawCircle(_center, _maxRadius, ringPaint);

var thumbCenter = new SKPoint(_center.X + _thumbOffset.X, _center.Y + _thumbOffset.Y);
        using var shadowPaint = new SKPaint { Color = SKColors.Black.WithAlpha(40), IsAntialias = true };
        canvas.DrawCircle(thumbCenter.X + 2, thumbCenter.Y + 3, 32f, shadowPaint);

        using var thumbPaint = new SKPaint { Color = ThumbColor.ToSKColor(), IsAntialias = true };
        canvas.DrawCircle(thumbCenter, 32f, thumbPaint);

using var highlightPaint = new SKPaint { Color = SKColors.White.WithAlpha(80), IsAntialias = true };
        canvas.DrawCircle(thumbCenter.X - 8, thumbCenter.Y - 8, 12f, highlightPaint);
    }

    protected override void OnTouch(SKTouchEventArgs e)
    {
        e.Handled = true;
        var pos = e.Location;
        float dx = pos.X - _center.X;
        float dy = pos.Y - _center.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        if (e.ActionType is SKTouchAction.Released or SKTouchAction.Cancelled)
        {
            _thumbOffset = SKPoint.Empty;
            JoystickReleased?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            if (dist > _maxRadius)
            {
                dx = dx / dist * _maxRadius;
                dy = dy / dist * _maxRadius;
            }
            _thumbOffset = new SKPoint(dx, dy);
            float nx =  dx / _maxRadius;
            float ny = -dy / _maxRadius; 
            JoystickMoved?.Invoke(this, new JoystickEventArgs(nx, ny));
        }

        InvalidateSurface();
    }
}
