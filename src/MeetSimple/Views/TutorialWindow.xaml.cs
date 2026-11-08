using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

/// <summary>
/// First-run tour. "Skip tour" is always available, and it can be replayed any time from "Quick tour".
/// Closing it either way counts as done, so it is only offered automatically once.
/// </summary>
public partial class TutorialWindow : Window, IDisposable
{
    private const int AudioCheckStepIndex = 2;

    private static readonly (string Title, string Body)[] Steps =
    {
        ("Welcome",
         "Meet Simple turns meetings and recordings into text and short summaries - all on this computer, so nothing is uploaded.\n\nThis one-minute tour shows how it works. You can skip it now and replay it any time with \"Quick tour\" at the top of the window."),
        ("Two ways to start",
         "Record a meeting captures your microphone and the sound from your computer at the same time, so the transcript can show who said what: You or Others.\n\nUse an existing file lets you pick any video or audio recording you already have."),
        ("Check your audio",
         "Speak, or play something, and watch the bars move. If a bar stays still, check your Windows sound settings and microphone privacy settings.\n\nTip: wear headphones so your microphone doesn't also pick up the other people from your speakers."),
        ("Choose how much you want",
         "Transcribe only is the fastest: you get the full text with timestamps.\n\nTranscribe + summarize also writes a short summary with just the important points, decisions and tasks. The first time, it sets up a local AI (several gigabytes) - you'll be asked before anything is downloaded."),
        ("You're ready",
         "Your results are saved in Documents > Meet Simple, next to the audio.\n\nOnly record calls when everyone on them agrees - rules differ by country and company."),
    };

    private int stepIndex;
    private bool hasShownStep;
    private AudioRecorder? meterRecorder;

    public TutorialWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this, "SurfaceBrush");
        ShowStep();
        Closed += OnClosed;
        Loaded += (_, _) => Motion.FadeIn(Root, Motion.Gentle);
    }

    private void ShowStep()
    {
        StepCounterText.Text = $"STEP {stepIndex + 1} OF {Steps.Length}";
        TitleText.Text = Steps[stepIndex].Title;
        BodyText.Text = Steps[stepIndex].Body;
        BackButton.Visibility = stepIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool isLast = stepIndex == Steps.Length - 1;
        NextButton.Content = isLast ? "Get started" : "Next";
        SkipButton.Visibility = isLast ? Visibility.Collapsed : Visibility.Visible;
        RebuildStepDots();

        if (stepIndex == AudioCheckStepIndex)
        {
            StartMeters();
        }
        else
        {
            StopMeters();
        }

        // Ease the new step's text in (not the very first one - the whole window already fades in).
        if (hasShownStep)
        {
            Motion.FadeIn(StepCounterText);
            Motion.FadeIn(TitleText);
            Motion.FadeIn(BodyText);
            Motion.FadeIn(StepDots);
            if (stepIndex == AudioCheckStepIndex) Motion.FadeIn(MetersPanel);
        }
        hasShownStep = true;
    }

    private void RebuildStepDots()
    {
        StepDots.Children.Clear();
        for (int index = 0; index < Steps.Length; index++)
        {
            bool active = index == stepIndex;
            var dot = new Ellipse
            {
                Width = active ? 8 : 6,
                Height = active ? 8 : 6,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.SetResourceReference(Shape.FillProperty, active ? "AccentBrush" : "TrackBrush");
            StepDots.Children.Add(dot);
        }
    }

    private void StartMeters()
    {
        MetersPanel.Visibility = Visibility.Visible;
        meterRecorder = new AudioRecorder();
        meterRecorder.StartMonitoring();
        Meters.Attach(meterRecorder);
    }

    /// <summary>Releases the microphone level monitor if the tour is closed while it is still running.</summary>
    public void Dispose() => StopMeters();

    private void StopMeters()
    {
        MetersPanel.Visibility = Visibility.Collapsed;
        Meters.Detach();
        meterRecorder?.Dispose();
        meterRecorder = null;
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (stepIndex == Steps.Length - 1)
        {
            Close();
            return;
        }
        stepIndex++;
        ShowStep();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (stepIndex == 0) return;
        stepIndex--;
        ShowStep();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        StopMeters();
        AppSettings.Current.TutorialCompleted = true;
        AppSettings.Current.Save();
    }
}
