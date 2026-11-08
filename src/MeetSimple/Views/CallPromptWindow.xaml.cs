using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

/// <summary>
/// The island at the top of the screen, above whatever app the call is in. It drops down to ask "Zoom call -
/// Record?", then becomes a small recording pill with the time and a stop button. It never takes the keyboard focus
/// away from the call, and it goes away by itself if nobody answers.
/// </summary>
public partial class CallPromptWindow : Window
{
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20);
    private static readonly SpringEase DropSpring = Frozen(new SpringEase(0.62, 9.5));
    private static readonly SpringEase MorphSpring = Frozen(new SpringEase(0.7, 9.0));

    private const int ExtendedStyleIndex = -20;
    private const int NoActivateStyle = 0x08000000;
    private const int ToolWindowStyle = 0x00000080;

    private readonly DispatcherTimer answerTimer;
    private readonly DispatcherTimer clockTimer;
    private Func<TimeSpan>? elapsedSource;
    private bool isClosing;

    public CallPromptWindow()
    {
        InitializeComponent();
        answerTimer = new DispatcherTimer { Interval = AnswerTimeout };
        answerTimer.Tick += (_, _) =>
        {
            answerTimer.Stop();
            NotNowClicked?.Invoke();
        };
        clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        clockTimer.Tick += (_, _) => UpdateClock();
        SourceInitialized += (_, _) => MakeNonActivating();
        Loaded += (_, _) => DropIn();
    }

    public event Action? RecordClicked;
    public event Action? NotNowClicked;
    public event Action? StopClicked;
    public event Action? KeepRecordingClicked;

    /// <summary>Shows the question for a call that just started.</summary>
    public void Ask(string callName)
    {
        CallNameText.Text = callName;
        AskPanel.Visibility = Visibility.Visible;
        RecordingPanel.Visibility = Visibility.Collapsed;
        PlaceAtTopOfScreen();
        if (!IsVisible) Show();
        answerTimer.Start();
    }

    /// <summary>Turns the question into the recording pill.</summary>
    public void ShowRecording(Func<TimeSpan> elapsed)
    {
        answerTimer.Stop();
        elapsedSource = elapsed;
        UpdateClock();
        bool wasEnding = EndingPanel.Visibility == Visibility.Visible;
        AskPanel.Visibility = Visibility.Collapsed;
        EndingPanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = Visibility.Visible;
        clockTimer.Start();
        if (!Motion.Enabled) return;
        if (wasEnding)
        {
            Settle();
            return;
        }

        // The pill gives a little as it changes shape, like the island settling around new content.
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.82, 1.0, new Duration(TimeSpan.FromMilliseconds(520))) { EasingFunction = MorphSpring });
        Motion.FadeIn(RecordingPanel);
        RecordingDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.25, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            EasingFunction = Motion.StandardCurve,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    /// <summary>The call has gone quiet: "Call ended? · Stopping in 20" with Keep recording. Call again each second.</summary>
    public void ShowEnding(int secondsLeft)
    {
        EndingText.Text = $"Call ended? · Stopping in {secondsLeft}";
        if (EndingPanel.Visibility == Visibility.Visible) return;
        RecordingPanel.Visibility = Visibility.Collapsed;
        EndingPanel.Visibility = Visibility.Visible;
        if (Motion.Enabled) Settle();
    }

    /// <summary>The pill gives a little as its content changes, like the island settling around new content.</summary>
    private void Settle()
    {
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.86, 1.0, new Duration(TimeSpan.FromMilliseconds(520))) { EasingFunction = MorphSpring });
    }

    /// <summary>Rises back into the top edge, then closes.</summary>
    public void Dismiss()
    {
        if (isClosing) return;
        isClosing = true;
        answerTimer.Stop();
        clockTimer.Stop();
        if (!Motion.Enabled || !IsVisible)
        {
            Close();
            return;
        }
        var duration = new Duration(TimeSpan.FromMilliseconds(300));
        var rise = new DoubleAnimation(0.0, -34.0, duration) { EasingFunction = Motion.StandardCurve };
        var shrink = new DoubleAnimation(1.0, 0.5, duration) { EasingFunction = Motion.StandardCurve };
        var fade = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = Motion.StandardCurve };
        fade.Completed += (_, _) => Close();
        PillShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, rise);
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, shrink);
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, shrink);
        Pill.BeginAnimation(OpacityProperty, fade);
    }

    private void DropIn()
    {
        if (!Motion.Enabled) return;
        // Drops out of the top edge and springs open sideways, the way the island grows on a phone.
        var drop = new DoubleAnimation(-34.0, 0.0, new Duration(TimeSpan.FromMilliseconds(620))) { EasingFunction = DropSpring };
        var widen = new DoubleAnimation(0.35, 1.0, new Duration(TimeSpan.FromMilliseconds(680))) { EasingFunction = DropSpring };
        var deepen = new DoubleAnimation(0.6, 1.0, new Duration(TimeSpan.FromMilliseconds(560))) { EasingFunction = DropSpring };
        PillShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, drop);
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, widen);
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, deepen);
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(180))));
    }

    private void UpdateClock()
    {
        TimeSpan elapsed = elapsedSource?.Invoke() ?? TimeSpan.Zero;
        TimerText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
    }

    private void PlaceAtTopOfScreen()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2.0;
        Top = workArea.Top;
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        answerTimer.Stop();
        RecordClicked?.Invoke();
    }

    private void OnNotNowClick(object sender, RoutedEventArgs e)
    {
        answerTimer.Stop();
        NotNowClicked?.Invoke();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopClicked?.Invoke();

    private void OnKeepRecordingClick(object sender, RoutedEventArgs e) => KeepRecordingClicked?.Invoke();

    /// <summary>Clicking the pill must never pull the keyboard away from the call (no activation, no taskbar or Alt+Tab entry).</summary>
    private void MakeNonActivating()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int currentStyle = GetWindowLong(handle, ExtendedStyleIndex);
        _ = SetWindowLong(handle, ExtendedStyleIndex, currentStyle | NoActivateStyle | ToolWindowStyle);
    }

    private static SpringEase Frozen(SpringEase spring)
    {
        spring.Freeze();
        return spring;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newValue);
}
