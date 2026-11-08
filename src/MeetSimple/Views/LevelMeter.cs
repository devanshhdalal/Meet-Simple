using System.Windows;
using System.Windows.Media;

namespace MeetSimple.Views;

/// <summary>
/// A slim, rounded level meter drawn every frame. Levels are shown on a decibel scale (quiet speech is visible, hiss
/// is not), rise almost at once and fall slowly so the bar reads steadily instead of jittering, and a small dot
/// holds the latest peak before it drops.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    /// <summary>Quietest level drawn, in dBFS; anything lower is an empty bar.</summary>
    private const double FloorDecibels = -54.0;

    /// <summary>Shapes the scale so speech fills most of the bar and room noise barely shows.</summary>
    private const double ScaleCurve = 1.4;

    /// <summary>Fast attack, slow release: the bar jumps up with the voice and glides down between words.</summary>
    private const double AttackSeconds = 0.012;
    private const double ReleaseSeconds = 0.65;

    private const double PeakHoldSeconds = 0.8;
    private const double PeakFallAcceleration = 1.6;

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender,
                                      (meter, _) => ((LevelMeter)meter).fillCache = null));

    public static readonly DependencyProperty PeakBrushProperty = DependencyProperty.Register(
        nameof(PeakBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private Func<float>? peakSource;
    private TimeSpan lastFrame;
    private double level;
    private double peak;
    private double peakHeldFor;
    private double peakFallSpeed;
    private bool rendering;
    private (Color BaseColor, LinearGradientBrush Fill, SolidColorBrush Halo)? fillCache;

    public LevelMeter()
    {
        SetResourceReference(TrackBrushProperty, "TrackBrush");
        SetResourceReference(FillBrushProperty, "AccentBrush");
        SetResourceReference(PeakBrushProperty, "TextBrush");
        Loaded += (_, _) => UpdateRendering();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateRendering();
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush PeakBrush
    {
        get => (Brush)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    /// <summary>Returns the highest level (0..1) since it was last asked; null stops the meter and empties it.</summary>
    public Func<float>? PeakSource
    {
        get => peakSource;
        set
        {
            peakSource = value;
            if (value == null)
            {
                level = 0;
                peak = 0;
                InvalidateVisual();
            }
            UpdateRendering();
        }
    }

    private void UpdateRendering()
    {
        if (peakSource != null && IsLoaded && IsVisible) StartRendering(); else StopRendering();
    }

    private void StartRendering()
    {
        if (rendering) return;
        rendering = true;
        lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopRendering()
    {
        if (!rendering) return;
        rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (peakSource == null) return;
        var frameTime = e is RenderingEventArgs renderingArgs ? renderingArgs.RenderingTime : TimeSpan.Zero;
        if (frameTime == lastFrame) return; // WPF can report the same frame twice.
        double seconds = lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((frameTime - lastFrame).TotalSeconds, 0.001, 0.1);
        lastFrame = frameTime;

        double target = ToBarFraction(peakSource());
        double timeConstant = target > level ? AttackSeconds : ReleaseSeconds;
        double previousLevel = level;
        level += (target - level) * (1 - Math.Exp(-seconds / timeConstant));

        double previousPeak = peak;
        if (level >= peak)
        {
            peak = level;
            peakHeldFor = 0;
            peakFallSpeed = 0;
        }
        else if ((peakHeldFor += seconds) > PeakHoldSeconds)
        {
            peakFallSpeed += PeakFallAcceleration * seconds;
            peak = Math.Max(level, peak - peakFallSpeed * seconds);
        }

        if (Math.Abs(level - previousLevel) > 0.0005 || Math.Abs(peak - previousPeak) > 0.0005) InvalidateVisual();
    }

    private static double ToBarFraction(float sample)
    {
        if (sample <= 0) return 0;
        double decibels = 20 * Math.Log10(sample);
        double linear = Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0, 1);
        return Math.Pow(linear, ScaleCurve);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsNaN(Height) ? 6 : Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double radius = height / 2;

        drawingContext.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        var (fill, halo) = FillBrushes();
        double fillWidth = level * width;
        if (fillWidth > 0.5)
        {
            // Below a pill's own width the bar fades in rather than appearing as a squashed dot.
            double presence = Math.Clamp(fillWidth / height, 0, 1);
            drawingContext.PushOpacity(presence);
            double drawnWidth = Math.Max(fillWidth, height);
            drawingContext.DrawRoundedRectangle(halo, null, new Rect(-1, -1, drawnWidth + 2, height + 2), radius + 1, radius + 1);
            drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, 0, drawnWidth, height), radius, radius);
            drawingContext.Pop();
        }

        double peakX = peak * width;
        if (peakX > height * 1.5 && peak - level > 0.015)
        {
            drawingContext.PushOpacity(Math.Clamp((peak - level) * 12, 0, 0.85));
            drawingContext.DrawEllipse(PeakBrush, null, new Point(Math.Min(peakX, width - radius), radius), radius * 0.7, radius * 0.7);
            drawingContext.Pop();
        }
    }

    /// <summary>A gradient that brightens toward the leading edge, and a faint halo, both from the accent colour.</summary>
    private (Brush Fill, Brush Halo) FillBrushes()
    {
        if (FillBrush is not SolidColorBrush solid) return (FillBrush, Brushes.Transparent);
        if (fillCache is { } cached && cached.BaseColor == solid.Color) return (cached.Fill, cached.Halo);

        Color baseColor = solid.Color;
        Color dim = Color.FromArgb(200, baseColor.R, baseColor.G, baseColor.B);
        Color bright = Mix(baseColor, Colors.White, 0.28);
        var fill = new LinearGradientBrush(dim, bright, new Point(0, 0), new Point(1, 0)) { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        fill.Freeze();
        var halo = new SolidColorBrush(Color.FromArgb(46, baseColor.R, baseColor.G, baseColor.B));
        halo.Freeze();
        fillCache = (baseColor, fill, halo);
        return (fill, halo);
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
