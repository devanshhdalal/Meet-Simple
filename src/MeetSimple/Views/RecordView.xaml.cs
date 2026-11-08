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

        if (recorder.MicrophoneError != null && recorder.ComputerAudioError != null)
        {
            StatusText.Text = "Neither the microphone nor the computer's sound could be opened, so nothing can be recorded.";
            RecordButton.IsEnabled = false;
        }
    }

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
        sessionFolder = Path.Combine(AppSettings.RecordingsRoot, DateTime.Now.ToString("yyyy-MM-dd HH.mm") + " Meeting");
        try
        {
            recorder.StartRecording(sessionFolder);
        }
        catch (Exception error)
        {
            StatusText.Text = "Could not start recording: " + error.Message;
            return;
        }

        BackButton.IsEnabled = false;
        RecordButton.Content = "Stop and transcribe";
        RecordButton.Style = (Style)FindResource("DangerButton");
        Motion.Settle(RecordButton);
        StatusText.Text = "Recording... your audio stays on this computer.";
        clockTimer.Start();
    }

    private async Task StopAndProcessAsync()
    {
        var recording = recorder.StopRecording();
        clockTimer.Stop();

        if (recording.Duration < TimeSpan.FromSeconds(3))
        {
            ResetToIdle("That recording was too short. Try again when you're ready.");
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

    private void ResetToIdle(string message)
    {
        BackButton.IsEnabled = true;
        RecordButton.Content = "Start recording";
        RecordButton.Style = (Style)FindResource("AccentButton");
        Motion.Settle(RecordButton);
        TimerText.Text = "00:00:00";
        StatusText.Text = message;
    }

    public void Dispose()
    {
        clockTimer.Stop();
        Meters.Detach();
        recorder.Dispose();
    }
}
