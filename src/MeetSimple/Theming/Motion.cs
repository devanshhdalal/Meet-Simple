using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MeetSimple.Theming;

/// <summary>Shared animation helpers so every movement in the app uses the same durations and the same ease-in-out curve.</summary>
public static class Motion
{
    public static readonly Duration Quick = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration Smooth = new(TimeSpan.FromMilliseconds(380));
    public static readonly Duration Gentle = new(TimeSpan.FromMilliseconds(560));

    private static readonly CubicBezierEase Curve = CreateCurve();

    /// <summary>The shared ease-in-out curve, for code that builds its own animations.</summary>
    public static IEasingFunction StandardCurve => Curve;

    /// <summary>False when the user turned off animations in Windows, in which case everything changes instantly.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static CubicBezierEase CreateCurve()
    {
        var curve = new CubicBezierEase();
        curve.Freeze();
        return curve;
    }

    /// <summary>An animation that returns the property to its normal value when it ends.</summary>
    private static DoubleAnimation Tween(double from, double to, Duration duration) => new(from, to, duration)
    {
        EasingFunction = Curve,
        FillBehavior = FillBehavior.Stop,
    };

    public static void FadeIn(UIElement element, Duration? duration = null)
    {
        if (!Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, Tween(0.0, 1.0, duration ?? Smooth));
    }

    /// <summary>Softens an abrupt change (for example a button changing colour) by easing back up from slightly faded.</summary>
    public static void Settle(UIElement element)
    {
        if (!Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, Tween(0.55, 1.0, Smooth));
    }

    /// <summary>Fades an element in while it glides up a few pixels into place.</summary>
    public static void RiseIn(FrameworkElement element, double distance = 14.0)
    {
        if (!Enabled) return;
        var shift = new TranslateTransform(0.0, 0.0);
        element.RenderTransform = shift;
        element.BeginAnimation(UIElement.OpacityProperty, Tween(0.0, 1.0, Smooth));

        var slide = Tween(distance, 0.0, Smooth);
        slide.Completed += (_, _) => element.ClearValue(UIElement.RenderTransformProperty);
        shift.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Fades an element out, then lets the caller hide it and release what it showed.</summary>
    public static void FadeOut(UIElement element, Duration duration, Action onCompleted)
    {
        var fade = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = Curve, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1.0;
            onCompleted();
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Glides a progress bar to its new value instead of jumping.</summary>
    public static void AnimateValue(RangeBase bar, double target)
    {
        if (!Enabled || !bar.IsLoaded)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            bar.Value = target;
            return;
        }
        bar.BeginAnimation(RangeBase.ValueProperty,
            new DoubleAnimation(target, Smooth) { EasingFunction = Curve, FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>A picture of what an element looks like right now, painted over a solid backdrop so it can be cross-faded away.</summary>
    public static BitmapSource? Capture(FrameworkElement element, Brush backdrop)
    {
        if (element.ActualWidth < 1.0 || element.ActualHeight < 1.0) return null;

        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        int pixelWidth = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
        int pixelHeight = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);

        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(backdrop, null, bounds);
            context.DrawRectangle(new VisualBrush(element), null, bounds);
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }
}
