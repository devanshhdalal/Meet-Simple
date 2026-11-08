using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetSimple.Core;
using MeetSimple.Theming;

namespace MeetSimple.Views;

/// <summary>
/// Live level meters for the microphone and the computer's sound, each with a device picker that works mid-recording,
/// and a nudge when the sound is clearly on another device than the one being recorded.
/// </summary>
public partial class LevelMetersControl : UserControl
{
    /// <summary>A shown nudge stays this long after the last sign of sound elsewhere, so it does not flicker.</summary>
    private static readonly TimeSpan HintLinger = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer routeTimer;
    private readonly DeviceRow microphoneRow;
    private readonly DeviceRow computerRow;
    private AudioRecorder? recorder;
    private bool populating;

    public LevelMetersControl()
    {
        InitializeComponent();
        microphoneRow = new DeviceRow(isMicrophone: true, MicrophonePicker, MicrophoneMeter, MicrophoneStatus, MicrophoneHint, MicrophoneHintText);
        computerRow = new DeviceRow(isMicrophone: false, ComputerPicker, ComputerMeter, ComputerStatus, ComputerHint, ComputerHintText);
        routeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        routeTimer.Tick += OnRouteTick;
        Loaded += (_, _) =>
        {
            if (recorder != null) routeTimer.Start();
        };
        Unloaded += (_, _) => routeTimer.Stop();
    }

    public void Attach(AudioRecorder audioRecorder)
    {
        recorder = audioRecorder;
        MicrophoneMeter.PeakSource = audioRecorder.TakeMicrophonePeak;
        ComputerMeter.PeakSource = audioRecorder.TakeComputerAudioPeak;
        microphoneRow.Advisor = new AudioRouteAdvisor(microphones: true);
        computerRow.Advisor = new AudioRouteAdvisor(microphones: false);
        PopulatePickers();
        UpdateStatus();
        routeTimer.Start();
    }

    public void Detach()
    {
        routeTimer.Stop();
        recorder = null;
        foreach (var row in new[] { microphoneRow, computerRow })
        {
            row.Meter.PeakSource = null;
            row.Advisor?.Dispose();
            row.Advisor = null;
            HideHint(row);
        }
    }

    private void UpdateStatus()
    {
        if (recorder == null) return;
        MicrophoneStatus.Text = recorder.MicrophoneError != null
            ? "No microphone found, or Windows is blocking access. Check Settings > Privacy & security > Microphone."
            : "Speak to see the bar move.";
        ComputerStatus.Text = recorder.ComputerAudioError != null
            ? "Could not listen to this output. Pick another one."
            : "Play something (a video or a call) to see the bar move.";
    }

    // ----- device pickers -----

    private void PopulatePickers()
    {
        populating = true;
        try
        {
            Fill(microphoneRow, recorder?.Microphone);
            Fill(computerRow, recorder?.ComputerAudio);
        }
        finally
        {
            populating = false;
        }
    }

    private static void Fill(DeviceRow row, AudioDevice? current)
    {
        var devices = AudioDevices.List(row.IsMicrophone).ToList();
        if (current != null && devices.All(device => device.Id != current.Id)) devices.Insert(0, current);
        row.Picker.ItemsSource = devices;
        row.Picker.SelectedItem = current == null ? null : devices.First(device => device.Id == current.Id);
        row.Picker.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Lists devices again on open, so one plugged in a moment ago is there.</summary>
    private void OnPickerOpened(object? sender, EventArgs e) => PopulatePickers();

    private void OnMicrophonePicked(object sender, SelectionChangedEventArgs e) => OnPicked(microphoneRow);

    private void OnComputerPicked(object sender, SelectionChangedEventArgs e) => OnPicked(computerRow);

    private void OnPicked(DeviceRow row)
    {
        if (populating || recorder == null || row.Picker.SelectedItem is not AudioDevice chosen) return;
        if (row.IsMicrophone) recorder.SwitchMicrophone(chosen.Id); else recorder.SwitchComputerAudio(chosen.Id);
        RememberChoice(row.IsMicrophone, chosen.Id);
        HideHint(row);
        UpdateStatus();
    }

    /// <summary>The next recording starts on the same device. Picking the Windows default keeps following the default.</summary>
    private static void RememberChoice(bool isMicrophone, string deviceId)
    {
        string remembered = deviceId == AudioDevices.DefaultId(isMicrophone) ? "" : deviceId;
        var settings = AppSettings.Current;
        if (isMicrophone) settings.RecordMicrophoneId = remembered; else settings.RecordComputerAudioId = remembered;
        settings.Save();
    }

    // ----- "the sound is over there" nudge -----

    private void OnRouteTick(object? sender, EventArgs e)
    {
        if (recorder == null) return;
        CheckRoute(microphoneRow, recorder.Microphone?.Id);
        CheckRoute(computerRow, recorder.ComputerAudio?.Id);
    }

    private void CheckRoute(DeviceRow row, string? recordedDeviceId)
    {
        if (row.Advisor?.Sample(recordedDeviceId) is { } suggestion)
        {
            row.SuggestedAt = DateTime.UtcNow;
            if (row.Suggestion?.Id != suggestion.Id) ShowHint(row, suggestion);
        }
        else if (row.Suggestion != null && DateTime.UtcNow - row.SuggestedAt > HintLinger)
        {
            HideHint(row);
        }
    }

    private static void ShowHint(DeviceRow row, AudioDevice suggestion)
    {
        bool wasHidden = row.Suggestion == null;
        row.Suggestion = suggestion;
        row.HintText.Text = row.IsMicrophone ? $"{suggestion.Name} is picking up sound." : $"Sound is playing on {suggestion.Name}.";
        row.HintText.ToolTip = suggestion.Name;
        row.Status.Visibility = Visibility.Collapsed;
        row.Hint.Visibility = Visibility.Visible;
        if (wasHidden) Motion.FadeIn(row.Hint);
    }

    private static void HideHint(DeviceRow row)
    {
        row.Suggestion = null;
        row.Hint.Visibility = Visibility.Collapsed;
        row.Status.Visibility = Visibility.Visible;
    }

    private void OnMicrophoneHintSwitch(object sender, RoutedEventArgs e) => SwitchToSuggestion(microphoneRow);

    private void OnComputerHintSwitch(object sender, RoutedEventArgs e) => SwitchToSuggestion(computerRow);

    private void OnMicrophoneHintDismiss(object sender, RoutedEventArgs e) => DismissSuggestion(microphoneRow);

    private void OnComputerHintDismiss(object sender, RoutedEventArgs e) => DismissSuggestion(computerRow);

    private void SwitchToSuggestion(DeviceRow row)
    {
        if (row.Suggestion is not { } suggestion) return;
        if (row.Picker.ItemsSource is not IEnumerable<AudioDevice> listed || listed.All(device => device.Id != suggestion.Id))
        {
            PopulatePickers();
        }
        var match = (row.Picker.ItemsSource as IEnumerable<AudioDevice>)?.FirstOrDefault(device => device.Id == suggestion.Id);
        if (match != null) row.Picker.SelectedItem = match; // OnPicked switches, remembers and hides the nudge.
        else HideHint(row);
    }

    private static void DismissSuggestion(DeviceRow row)
    {
        if (row.Suggestion is { } suggestion) row.Advisor?.Dismiss(suggestion.Id);
        HideHint(row);
    }

    private sealed class DeviceRow
    {
        public DeviceRow(bool isMicrophone, ComboBox picker, LevelMeter meter, TextBlock status, FrameworkElement hint, TextBlock hintText)
        {
            IsMicrophone = isMicrophone;
            Picker = picker;
            Meter = meter;
            Status = status;
            Hint = hint;
            HintText = hintText;
        }

        public bool IsMicrophone { get; }
        public ComboBox Picker { get; }
        public LevelMeter Meter { get; }
        public TextBlock Status { get; }
        public FrameworkElement Hint { get; }
        public TextBlock HintText { get; }
        public AudioRouteAdvisor? Advisor { get; set; }
        public AudioDevice? Suggestion { get; set; }
        public DateTime SuggestedAt { get; set; }
    }
}
