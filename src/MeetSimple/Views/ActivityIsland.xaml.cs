using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeetSimple.Theming;

namespace MeetSimple.Views;

public enum IslandState { Working, Done, Failed }

/// <summary>
/// The running job, shrunk into a pill at the bottom of the card while people look at other screens - modelled on
/// Apple's Dynamic Island. Leaving the progress screen shrinks the whole page into the pill; clicking the pill grows
/// it back into the page. Hovering lifts it to show more detail, and it briefly lifts by itself when the job ends.
///
/// Everything is driven by two numbers animated with a spring: Expansion (0 = pill, 1 = the whole card) and Lift
/// (-1 = hidden, 0 = resting pill, 1 = lifted). The pill's position, size, corner radius and the opacity of its
/// contents are all derived from them in Relayout, so every state blends into every other without special cases.
/// </summary>
public partial class ActivityIsland : UserControl
{
    private const double RestingWidth = 320.0;
    private const double RestingHeight = 48.0;
    private const double LiftExtraWidth = 64.0;
    private const double LiftExtraHeight = 18.0;
    private const double BottomGap = 18.0;
    private const double CardCornerRadius = 13.0;

    // Hover is deliberately unhurried, like the island on a phone: the shape eases open first and the extra detail
    // fades in only once there is room for it. Closing is a little quicker so the pill never feels sticky.
    private const double HoverOpenMilliseconds = 700.0;
    private const double HoverCloseMilliseconds = 560.0;
    private static readonly TimeSpan HoverIntentDelay = TimeSpan.FromMilliseconds(90);

    // Collapsing lands with a little bounce, expanding is a touch firmer, hovering is quick and lively.
    private static readonly SpringEase CollapseSpring = Frozen(new SpringEase(0.62, 9.5));
    private static readonly SpringEase ExpandSpring = Frozen(new SpringEase(0.74, 9.0));
    private static readonly SpringEase LiftSpring = Frozen(new SpringEase(0.6, 9.0));

    public static readonly DependencyProperty ExpansionProperty = DependencyProperty.Register(
        nameof(Expansion), typeof(double), typeof(ActivityIsland), new PropertyMetadata(0.0, OnShapeChanged));

    public static readonly DependencyProperty LiftProperty = DependencyProperty.Register(
        nameof(Lift), typeof(double), typeof(ActivityIsland), new PropertyMetadata(0.0, OnShapeChanged));

    public static readonly DependencyProperty RingFractionProperty = DependencyProperty.Register(
        nameof(RingFraction), typeof(double), typeof(ActivityIsland), new PropertyMetadata(0.0, OnRingChanged));

    private IslandState currentState = IslandState.Working;
    private bool fadesWhileExpanding;
    private bool isPulsing;
    private int transitionVersion;
    private DispatcherTimer? announcementTimer;
    private DispatcherTimer? hoverIntentTimer;

    public ActivityIsland()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Relayout();
        Shell.MouseEnter += (_, _) => OnHoverStarted();
        Shell.MouseLeave += (_, _) => OnHoverEnded();
        Shell.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            if (IsShown) Activated?.Invoke();
        };
        Shell.KeyDown += (_, args) =>
        {
            if (IsShown && args.Key is Key.Enter or Key.Space)
            {
                args.Handled = true;
                Activated?.Invoke();
            }
        };
    }

    /// <summary>Raised when the pill is clicked (or Enter/Space while it has focus).</summary>
    public event Action? Activated;

    /// <summary>True while the pill is on screen and clickable (false while hidden or growing back into a page).</summary>
    public bool IsShown { get; private set; }

    public double Expansion { get => (double)GetValue(ExpansionProperty); set => SetValue(ExpansionProperty, value); }
    public double Lift { get => (double)GetValue(LiftProperty); set => SetValue(LiftProperty, value); }
    public double RingFraction { get => (double)GetValue(RingFractionProperty); set => SetValue(RingFractionProperty, value); }

    // ----- content -----

    public void Update(IslandState state, string title, string detail, double? fraction)
    {
        bool justEnded = state != IslandState.Working && currentState == IslandState.Working;
        currentState = state;
        TitleText.Text = title;
        DetailText.Text = detail;
        AutomationProperties.SetName(Shell, $"{title}. {detail}");

        double ringTarget = state == IslandState.Done ? 1.0 : Math.Clamp(fraction ?? RingFraction, 0.0, 1.0);
        PercentText.Text = state == IslandState.Working && fraction != null ? $"{(int)(ringTarget * 100)}%" : "";
        AnimateRing(ringTarget);

        PulseDot.Visibility = state == IslandState.Working ? Visibility.Visible : Visibility.Collapsed;
        StateGlyph.Visibility = state == IslandState.Working ? Visibility.Collapsed : Visibility.Visible;
        if (state == IslandState.Done)
        {
            StateGlyph.Text = "";
            StateGlyph.SetResourceReference(TextBlock.ForegroundProperty, "IslandSuccessBrush");
            RingArc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "IslandSuccessBrush");
        }
        else if (state == IslandState.Failed)
        {
            StateGlyph.Text = "";
            StateGlyph.SetResourceReference(TextBlock.ForegroundProperty, "IslandErrorBrush");
            RingArc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "IslandErrorBrush");
        }
        else
        {
            RingArc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        }
        UpdatePulse();

        // Like a finished timer on a phone, lift for a moment to announce the result, then settle back.
        if (justEnded && IsShown) Announce();
    }

    // ----- transitions -----

    /// <summary>Shrinks a picture of the page being left into the resting pill.</summary>
    public void ShrinkFrom(BitmapSource? pageSnapshot)
    {
        if (pageSnapshot == null || !Motion.Enabled)
        {
            PopIn();
            return;
        }
        int version = BeginTransition(shown: true);
        fadesWhileExpanding = false;
        StopAnimation(LiftProperty, 0.0);
        SnapshotImage.Source = pageSnapshot;
        Animate(ExpansionProperty, 1.0, 0.0, CollapseSpring, 680, () =>
        {
            if (version == transitionVersion) SnapshotImage.Source = null;
        });
    }

    /// <summary>Appears from nothing: grows up out of the bottom edge into the resting pill.</summary>
    public void PopIn()
    {
        BeginTransition(shown: true);
        fadesWhileExpanding = false;
        SnapshotImage.Source = null;
        StopAnimation(ExpansionProperty, 0.0);
        Animate(LiftProperty, -1.0, 0.0, LiftSpring, 520);
        Animate(OpacityProperty, 0.0, 1.0, Motion.StandardCurve, 220);
    }

    /// <summary>Grows from the pill to the whole card while fading, revealing the page that opens underneath.</summary>
    public void ExpandAway()
    {
        int version = BeginTransition(shown: false);
        fadesWhileExpanding = true;
        SnapshotImage.Source = null;
        StopAnimation(LiftProperty, Math.Max(Lift, 0.0));
        Animate(LiftProperty, null, 0.0, ExpandSpring, 420);
        Animate(ExpansionProperty, 0.0, 1.0, ExpandSpring, 560, () =>
        {
            if (version == transitionVersion) Hide();
        });
    }

    /// <summary>Sinks back into the bottom edge and disappears.</summary>
    public void PopOut()
    {
        int version = BeginTransition(shown: false);
        Animate(LiftProperty, null, -1.0, CollapseSpring, 420);
        Animate(OpacityProperty, null, 0.0, Motion.StandardCurve, 240, () =>
        {
            if (version == transitionVersion) Hide();
        });
    }

    private int BeginTransition(bool shown)
    {
        StopAnnouncement();
        StopHoverIntent();
        IsShown = shown;
        Visibility = Visibility.Visible;
        Shell.IsHitTestVisible = shown;
        if (shown) StopAnimation(OpacityProperty, 1.0);
        UpdatePulse();
        return ++transitionVersion;
    }

    private void Hide()
    {
        Visibility = Visibility.Collapsed;
        fadesWhileExpanding = false;
        SnapshotImage.Source = null;
        StopAnimation(ExpansionProperty, 0.0);
        StopAnimation(LiftProperty, 0.0);
        StopAnimation(OpacityProperty, 1.0);
        UpdatePulse();
    }

    private void Announce()
    {
        StopAnnouncement();
        AnimateLift(1.0);
        announcementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        announcementTimer.Tick += (_, _) =>
        {
            StopAnnouncement();
            if (IsShown && !Shell.IsMouseOver) AnimateLift(0.0);
        };
        announcementTimer.Start();
    }

    /// <summary>
    /// A short dwell before opening, so a cursor that only passes over the pill on its way somewhere else
    /// does not set it breathing in and out.
    /// </summary>
    private void OnHoverStarted()
    {
        if (!IsShown) return;
        StopHoverIntent();
        hoverIntentTimer = new DispatcherTimer { Interval = HoverIntentDelay };
        hoverIntentTimer.Tick += (_, _) =>
        {
            StopHoverIntent();
            if (IsShown && Shell.IsMouseOver) AnimateLift(1.0);
        };
        hoverIntentTimer.Start();
    }

    private void OnHoverEnded()
    {
        StopHoverIntent();
        if (IsShown && announcementTimer == null) AnimateLift(0.0);
    }

    private void StopHoverIntent()
    {
        hoverIntentTimer?.Stop();
        hoverIntentTimer = null;
    }

    private void StopAnnouncement()
    {
        announcementTimer?.Stop();
        announcementTimer = null;
    }

    // ----- layout: everything follows from Expansion and Lift -----

    private static void OnShapeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) =>
        ((ActivityIsland)owner).Relayout();

    private void Relayout()
    {
        double stageWidth = ActualWidth;
        double stageHeight = ActualHeight;
        if (stageWidth < 1.0 || stageHeight < 1.0) return;

        double expansion = Expansion;
        double lift = Lift;
        double settledExpansion = Math.Clamp(expansion, 0.0, 1.0);

        double pillWidth = Math.Min(RestingWidth + LiftExtraWidth * lift, stageWidth - 32.0);
        double pillHeight = RestingHeight + LiftExtraHeight * lift;
        double pillLeft = (stageWidth - pillWidth) / 2.0;
        double pillTop = stageHeight - BottomGap - pillHeight;

        // The spring may overshoot past 1 or below 0; the shape follows it, which is what makes it feel elastic.
        double width = Math.Max(Lerp(pillWidth, stageWidth, expansion), 1.0);
        double height = Math.Max(Lerp(pillHeight, stageHeight, expansion), 1.0);
        double left = Lerp(pillLeft, 0.0, expansion);
        double top = Lerp(pillTop, 0.0, expansion);
        double radius = Math.Min(Lerp(pillHeight / 2.0, CardCornerRadius, settledExpansion), Math.Min(width, height) / 2.0);

        Canvas.SetLeft(Shell, left);
        Canvas.SetTop(Shell, top);
        Shell.Width = width;
        Shell.Height = height;
        Shell.CornerRadius = new CornerRadius(radius);
        double innerRadius = Math.Max(radius - 1.0, 0.0);
        ShellContent.Clip = new RectangleGeometry(
            new Rect(0, 0, Math.Max(width - 2.0, 0.0), Math.Max(height - 2.0, 0.0)), innerRadius, innerRadius);

        CompactContent.Opacity = Math.Clamp(1.0 - settledExpansion * 3.0, 0.0, 1.0);
        SnapshotImage.Opacity = Math.Clamp((settledExpansion - 0.2) / 0.6, 0.0, 1.0);
        Shell.Opacity = fadesWhileExpanding ? Math.Clamp(1.0 - (settledExpansion - 0.3) / 0.55, 0.0, 1.0) : 1.0;

        // The detail line takes its room with the shape, but its text only appears in the last part of the opening
        // (and leaves first when closing), so words never squeeze into a pill that is still too small for them.
        double liftAmount = Math.Clamp(lift, 0.0, 1.0);
        DetailText.Height = 15.0 * liftAmount;
        DetailText.Opacity = Math.Clamp((liftAmount - 0.45) / 0.55, 0.0, 1.0);

        // A lifted pill sits a little higher off the page.
        ShellShadow.BlurRadius = 24.0 + 14.0 * liftAmount;
        ShellShadow.ShadowDepth = 5.0 + 3.0 * liftAmount;
        ShellShadow.Opacity = 0.34 + 0.1 * liftAmount;
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    // ----- progress ring -----

    private static void OnRingChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) =>
        ((ActivityIsland)owner).DrawRing((double)args.NewValue);

    private void AnimateRing(double target)
    {
        if (!Motion.Enabled || !IsLoaded)
        {
            StopAnimation(RingFractionProperty, target);
            return;
        }
        Animate(RingFractionProperty, null, target, Motion.StandardCurve, 380);
    }

    private void DrawRing(double fraction)
    {
        const double ringSize = 30.0;
        const double strokeWidth = 3.0;
        double clamped = Math.Clamp(fraction, 0.0, 1.0);
        double radius = (ringSize - strokeWidth) / 2.0;
        var center = new Point(ringSize / 2.0, ringSize / 2.0);

        if (clamped <= 0.002)
        {
            RingArc.Data = null;
            return;
        }
        if (clamped >= 0.998)
        {
            RingArc.Data = new EllipseGeometry(center, radius, radius);
            return;
        }

        double sweepDegrees = clamped * 360.0;
        double endRadians = (sweepDegrees - 90.0) * Math.PI / 180.0;
        var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            new Point(center.X + radius * Math.Cos(endRadians), center.Y + radius * Math.Sin(endRadians)),
            new Size(radius, radius), 0.0, sweepDegrees > 180.0, SweepDirection.Clockwise, true));
        RingArc.Data = new PathGeometry(new[] { figure });
    }

    // ----- breathing dot -----

    private void UpdatePulse()
    {
        bool shouldPulse = Visibility == Visibility.Visible && currentState == IslandState.Working && Motion.Enabled;
        if (shouldPulse == isPulsing) return;
        isPulsing = shouldPulse;
        if (!shouldPulse)
        {
            HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PulseHalo.BeginAnimation(OpacityProperty, null);
            PulseHalo.Opacity = 0.0;
            return;
        }
        var duration = new Duration(TimeSpan.FromMilliseconds(1600));
        var grow = new DoubleAnimation(0.45, 1.25, duration) { EasingFunction = Motion.StandardCurve, RepeatBehavior = RepeatBehavior.Forever };
        var fade = new DoubleAnimation(0.5, 0.0, duration) { EasingFunction = Motion.StandardCurve, RepeatBehavior = RepeatBehavior.Forever };
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        PulseHalo.BeginAnimation(OpacityProperty, fade);
    }

    // ----- animation plumbing -----

    /// <summary>Hover and the end-of-job announcement use the shared ease-in-out cubic-bezier, slowly; appearing keeps the spring.</summary>
    private void AnimateLift(double target) =>
        Animate(LiftProperty, null, target, Motion.StandardCurve, target > Lift ? HoverOpenMilliseconds : HoverCloseMilliseconds);

    private void Animate(DependencyProperty property, double? from, double to, IEasingFunction easing, double milliseconds,
        Action? completed = null)
    {
        if (!Motion.Enabled)
        {
            StopAnimation(property, to);
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (from.HasValue) animation.From = from.Value;
        if (completed != null) animation.Completed += (_, _) => completed();
        BeginAnimation(property, animation);
    }

    private void StopAnimation(DependencyProperty property, double value)
    {
        BeginAnimation(property, null);
        SetValue(property, value);
    }

    private static SpringEase Frozen(SpringEase spring)
    {
        spring.Freeze();
        return spring;
    }
}
