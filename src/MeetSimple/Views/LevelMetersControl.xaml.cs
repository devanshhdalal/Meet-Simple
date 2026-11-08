using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetSimple.Core;

namespace MeetSimple.Views;

/// <summary>Two live level bars (microphone and computer audio) fed by an AudioRecorder.</summary>
public partial class LevelMetersControl : UserControl
{
    private readonly DispatcherTimer refreshTimer;
    private AudioRecorder? recorder;

    public LevelMetersControl()
    {
        InitializeComponent();
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        refreshTimer.Tick += OnTick;
        Unloaded += (_, _) => refreshTimer.Stop();
    }

    public void Attach(AudioRecorder audioRecorder)
    {
        recorder = audioRecorder;
        MicrophoneStatus.Text = audioRecorder.MicrophoneError != null
            ? "No microphone found, or Windows is blocking access. Check Settings > Privacy & security > Microphone."
            : "Speak to see the bar move.";
        ComputerStatus.Text = audioRecorder.ComputerAudioError != null
            ? "Could not listen to the computer's sound output."
            : "Play something (a video or a call) to see the bar move.";
        refreshTimer.Start();
    }

    public void Detach()
    {
        refreshTimer.Stop();
        recorder = null;
        MicrophoneBar.Value = 0;
        ComputerBar.Value = 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (recorder == null) return;
        // Square root makes quiet speech visible; the 0.85 factor makes the bars fall smoothly.
        MicrophoneBar.Value = Math.Max(Math.Sqrt(recorder.MicrophoneLevel) * 100, MicrophoneBar.Value * 0.85);
        ComputerBar.Value = Math.Max(Math.Sqrt(recorder.ComputerAudioLevel) * 100, ComputerBar.Value * 0.85);
    }
}
