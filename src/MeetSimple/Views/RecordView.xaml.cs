using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

public partial class RecordView : UserControl, IDisposable
{
    private readonly MainWindow shell;
    private readonly JobMode mode;
    private readonly AudioRecorder recorder = new();
    private readonly DispatcherTimer clockTimer;
    private string sessionFolder = "";

    public RecordView(MainWindow shell, JobMode mode)
    {
        this.shell = shell;
        this.mode = mode;
        InitializeComponent();
        StepStrip.Set(mode, "Record");

        recorder.StartMonitoring();
        Meters.Attach(recorder);

        clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        clockTimer.Tick += (_, _) => TimerText.Text = TextProcessing.FormatTimestamp(recorder.Elapsed);

        recorder.DeviceProblem += OnDeviceProblem;
        if (recorder.MicrophoneError != null && recorder.ComputerAudioError != null)
        {
            ShowStatus("Neither the microphone nor the computer's sound could be opened, so nothing can be recorded. "
                       + "Check Windows Settings > Privacy & security > Microphone.", isWarning: true);
            RecordButton.IsEnabled = false;
        }
        else if (DescribeMissingInput() is { } missingInput)
        {
            ShowStatus(missingInput, isWarning: true);
        }
    }

    private string? DescribeMissingInput()
    {
        if (recorder.MicrophoneError != null)
        {
            return "The microphone could not be opened, so only the computer's sound will be recorded. "
                   + "Check that a microphone is connected and allowed in Windows Settings > Privacy & security > Microphone.";
        }
        if (recorder.ComputerAudioError != null)
        {
            return "The computer's sound could not be captured, so only your microphone will be recorded.";
        }
        return null;
    }

    private void ShowStatus(string message, bool isWarning)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isWarning ? "WarningBrush" : "MutedBrush");
    }

    /// <summary>Raised from the audio threads (a device was unplugged, or the disk is full).</summary>
    private void OnDeviceProblem(string message) =>
        Dispatcher.BeginInvoke(() => ShowStatus(message, isWarning: true));

    public bool IsRecording => recorder.IsRecording;

    private void OnBackClick(object sender, RoutedEventArgs e) => shell.ShowSource(mode);

    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (!recorder.IsRecording)
        {
            StartRecording();
            return;
        }
        await StopAndProcessAsync();
    }

    private void StartRecording()
    {
        sessionFolder = AppHealth.UniqueFolder(AppSettings.RecordingsRoot, DateTime.Now.ToString("yyyy-MM-dd HH.mm") + " Meeting");
        try
        {
            recorder.StartRecording(sessionFolder);
        }
        catch (Exception error)
        {
            ShowStatus("Could not start recording: " + error.Message, isWarning: true);
            return;
        }

        BackButton.IsEnabled = false;
        RecordButton.Content = "Stop and transcribe";
        RecordButton.Style = (Style)FindResource("DangerButton");
        Motion.Settle(RecordButton);
        string? missingInput = DescribeMissingInput();
        ShowStatus(missingInput == null
            ? "Recording... your audio stays on this computer."
            : "Recording... " + missingInput, missingInput != null);
        clockTimer.Start();
    }

    private async Task StopAndProcessAsync()
    {
        var recording = recorder.StopRecording();
        clockTimer.Stop();

        if (recording.Duration < TimeSpan.FromSeconds(3))
        {
            ResetToIdle("That recording was too short. Try again when you're ready.", isWarning: true);
            return;
        }

        var tracks = new List<AudioTrack>();
        if (recording.MicrophonePath != null) tracks.Add(new AudioTrack("You", recording.MicrophonePath));
        if (recording.ComputerAudioPath != null) tracks.Add(new AudioTrack("Others", recording.ComputerAudioPath));

        var request = shell.CreateRequest(mode, tracks, sessionFolder, "Meeting");
        bool started = await shell.StartJobAsync(request);
        if (!started)
        {
            ResetToIdle($"Your recording is saved in: {sessionFolder}");
        }
    }

    private void ResetToIdle(string message, bool isWarning = false)
    {
        BackButton.IsEnabled = true;
        RecordButton.Content = "Start recording";
        RecordButton.Style = (Style)FindResource("AccentButton");
        Motion.Settle(RecordButton);
        TimerText.Text = "00:00:00";
        ShowStatus(message, isWarning);
    }

    public void Dispose()
    {
        clockTimer.Stop();
        Meters.Detach();
        recorder.DeviceProblem -= OnDeviceProblem;
        recorder.Dispose();
    }
}
