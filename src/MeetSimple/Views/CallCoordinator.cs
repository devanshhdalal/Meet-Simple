using System.Runtime.InteropServices;
using System.Windows;
using MeetSimple.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace MeetSimple.Views;

/// <summary>
/// Calls from start to finish, outside the main window:
/// the watcher notices a call, the island at the top of the screen asks, recording runs in the background, the call
/// ending stops it, and a Windows notification offers Summarize, Transcribe only or Save for now.
/// Nothing is ever recorded without a click on Record.
/// </summary>
public sealed class CallCoordinator : IDisposable
{
    private const string ActionKey = "action";
    private const string FolderKey = "folder";

    private readonly MainWindow mainWindow;
    private readonly MeetingWatcher watcher = new();
    private readonly TrayIcon trayIcon = new();
    private CallPromptWindow? prompt;
    private DetectedCall? promptedCall;
    private AudioRecorder? recorder;
    private DetectedCall? recordedCall;
    private System.Windows.Threading.DispatcherTimer? endingTimer;
    private int endingSecondsLeft;
    private bool keepUntilStopped;

    /// <summary>
    /// How long a recorded call may stay quiet before recording stops, after the watcher's own 8 s. A call app gives
    /// no signal at all while nobody talks and the person uses push-to-talk or mute (Discord), so silence is only a
    /// hint: the island asks "Call ended?" and counts down, and any sign of the call coming back cancels it.
    /// </summary>
    private const int EndingCountdownSeconds = 20;

    public CallCoordinator(MainWindow mainWindow)
    {
        this.mainWindow = mainWindow;
        watcher.CallStarted += OnCallStarted;
        watcher.CallEnded += OnCallEnded;
        watcher.CallDevicesChanged += OnCallDevicesChanged;
        trayIcon.OpenRequested += () => mainWindow.ShowFromTray();
        trayIcon.QuitRequested += () => mainWindow.RequestQuit();
        trayIcon.WatchToggled += watching =>
        {
            AppSettings.Current.WatchForCalls = watching;
            AppSettings.Current.Save();
            ApplySettings();
        };
        // Clicks on a "call recorded" notification arrive here, also when the click started Meet Simple.
        ToastNotificationManagerCompat.OnActivated += OnNotificationActivated;
        ApplySettings();
    }

    /// <summary>True while a call is being recorded in the background.</summary>
    public bool IsRecording => recorder?.IsRecording == true;

    /// <summary>The folder being recorded into, so it is not offered as "waiting to be transcribed" meanwhile.</summary>
    public string? RecordingFolder { get; private set; }

    /// <summary>Applies Watch for calls and Start with Windows after they change.</summary>
    public void ApplySettings()
    {
        var settings = AppSettings.Current;
        // While a call is being recorded the watcher must keep running, or the end of the call would never be noticed.
        if (settings.WatchForCalls || IsRecording) watcher.Start(); else watcher.Stop();
        StartupRegistration.Apply(settings.StartWithWindows);
        RefreshTray();

        // Without watching there is nothing to keep running for, so a hidden window must come back.
        if (!settings.WatchForCalls && !IsRecording && !mainWindow.IsVisible) mainWindow.ShowFromTray();
    }

    public void ShowTrayNotice(string title, string message)
    {
        RefreshTray();
        if (trayIcon.Visible) trayIcon.ShowNotice(title, message);
    }

    /// <summary>Stops a background recording before the app quits; the notification still offers what to do with it.</summary>
    public void StopRecordingForExit()
    {
        if (IsRecording) StopRecording();
    }

    public void Dispose()
    {
        ToastNotificationManagerCompat.OnActivated -= OnNotificationActivated;
        watcher.Dispose();
        prompt?.Close();
        recorder?.Dispose();
        trayIcon.Dispose();
    }

    private void RefreshTray()
    {
        trayIcon.Visible = AppSettings.Current.WatchForCalls || IsRecording;
        trayIcon.SetState(AppSettings.Current.WatchForCalls, IsRecording);
    }

    // ----- a call starts: ask -----

    private void OnCallStarted(DetectedCall call)
    {
        // The call being recorded came back (someone spoke, push-to-talk): carry on with the same recording.
        if (IsRecording && recordedCall?.AppName == call.AppName)
        {
            recordedCall = call;
            CancelEndingCountdown();
            return;
        }

        // Already recording (here or in the app), or already asking about another call: stay quiet.
        if (IsRecording || mainWindow.IsRecordingInApp || prompt != null) return;

        promptedCall = call;
        prompt = new CallPromptWindow();
        prompt.RecordClicked += () => StartRecording(call);
        prompt.NotNowClicked += ClosePrompt;
        prompt.StopClicked += StopRecording;
        prompt.KeepRecordingClicked += () =>
        {
            // Recording now only stops on stop, or when the call app quits.
            keepUntilStopped = true;
            CancelEndingCountdown();
            AppHealth.LogEvent("Call recording kept going by the user after the call went quiet.");
        };
        prompt.Closed += (_, _) =>
        {
            prompt = null;
            promptedCall = null;
        };
        prompt.Ask(call.AppName);
    }

    private void ClosePrompt() => prompt?.Dismiss();

    private void StartRecording(DetectedCall call)
    {
        if (mainWindow.IsRecordingInApp)
        {
            ClosePrompt();
            return;
        }
        string folder = AppHealth.UniqueFolder(AppSettings.RecordingsRoot,
            $"{DateTime.Now:yyyy-MM-dd HH.mm} {InputValidation.SafeFileName(call.AppName)} call");
        // Record the microphone and speakers the call itself uses, which are often a headset rather than the defaults.
        var callRecorder = new AudioRecorder(call.MicrophoneDeviceId, call.SpeakerDeviceId);
        try
        {
            callRecorder.StartRecording(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or NotEnoughDiskSpaceException or COMException)
        {
            callRecorder.Dispose();
            AppHealth.LogError(error, "Call recording could not start");
            ClosePrompt();
            ShowTrayNotice("Couldn't record", error.Message);
            return;
        }

        // Until it is transcribed, the next start offers it again (see RecordingRecovery).
        RecordingRecovery.MarkUnfinished(folder, JobMode.TranscribeAndSummarize);
        recorder = callRecorder;
        recordedCall = call;
        RecordingFolder = folder;
        prompt?.ShowRecording(() => callRecorder.Elapsed);
        RefreshTray();
    }

    /// <summary>The call moved to a headset or another output: the recording follows it, in the same files.</summary>
    private void OnCallDevicesChanged(DetectedCall call)
    {
        if (!IsRecording || recorder == null || recordedCall?.Id != call.Id) return;
        recordedCall = call;
        if (call.MicrophoneDeviceId is { } microphoneId && microphoneId != recorder.Microphone?.Id)
        {
            recorder.SwitchMicrophone(microphoneId);
        }
        if (call.SpeakerDeviceId is { } speakerId && speakerId != recorder.ComputerAudio?.Id)
        {
            recorder.SwitchComputerAudio(speakerId);
        }
    }

    // ----- the call ends: stop and offer what to do -----

    private void OnCallEnded(DetectedCall call, bool isCertain)
    {
        if (IsRecording && recordedCall != null && (recordedCall.Id == call.Id || recordedCall.AppName == call.AppName))
        {
            // Certain (app closed, call connection closed): stop now. Only quiet: ask with a countdown first.
            if (isCertain) StopRecording();
            else if (!keepUntilStopped) BeginEndingCountdown();
        }
        else if (promptedCall?.Id == call.Id && !IsRecording)
        {
            ClosePrompt(); // The call ended before anyone answered.
        }
    }

    private void BeginEndingCountdown()
    {
        if (endingTimer != null) return;
        endingSecondsLeft = EndingCountdownSeconds;
        prompt?.ShowEnding(endingSecondsLeft);
        endingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        endingTimer.Tick += (_, _) =>
        {
            endingSecondsLeft--;
            if (endingSecondsLeft <= 0)
            {
                AppHealth.LogEvent("Call recording stopped: the call stayed quiet through the countdown.");
                StopRecording();
                return;
            }
            prompt?.ShowEnding(endingSecondsLeft);
        };
        endingTimer.Start();
    }

    private void CancelEndingCountdown()
    {
        if (endingTimer == null) return;
        endingTimer.Stop();
        endingTimer = null;
        if (IsRecording && recorder is { } activeRecorder) prompt?.ShowRecording(() => activeRecorder.Elapsed);
    }

    private void StopRecording()
    {
        endingTimer?.Stop();
        endingTimer = null;
        keepUntilStopped = false;
        var callRecorder = recorder;
        var call = recordedCall;
        string? folder = RecordingFolder;
        if (callRecorder == null || call == null || folder == null) return;

        var result = callRecorder.StopRecording();
        callRecorder.Dispose();
        recorder = null;
        recordedCall = null;
        RecordingFolder = null;
        ClosePrompt();
        ApplySettings(); // Stops the watcher if watching was switched off during the call.

        if (result.Duration < TimeSpan.FromSeconds(3))
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return;
        }
        ShowCallRecordedNotification(call.AppName, folder, result.Duration);
    }

    /// <summary>A real Windows notification (like Snipping Tool's) that stays in the notification centre until answered.</summary>
    private void ShowCallRecordedNotification(string appName, string folder, TimeSpan duration)
    {
        int minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        try
        {
            new ToastContentBuilder()
                .AddArgument(ActionKey, "open")
                .AddArgument(FolderKey, folder)
                .AddText($"{appName} call recorded")
                .AddText($"{minutes} min")
                .AddButton(new ToastButton().SetContent("Summarize")
                    .AddArgument(ActionKey, "summarize").AddArgument(FolderKey, folder))
                .AddButton(new ToastButton().SetContent("Transcribe only")
                    .AddArgument(ActionKey, "transcribe").AddArgument(FolderKey, folder))
                .AddButton(new ToastButton().SetContent("Save for now")
                    .AddArgument(ActionKey, "save").AddArgument(FolderKey, folder).SetBackgroundActivation())
                .Show(toast => toast.ExpirationTime = DateTimeOffset.Now.AddDays(1));
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Notifications switched off or unavailable: ask in the app instead, so the recording is never forgotten.
            AppHealth.LogError(error, "Call notification");
            mainWindow.ShowFromTray();
            if (RecordingRecovery.Load(folder) is { } recording) _ = mainWindow.OfferRecordingAsync(recording);
        }
    }

    private void OnNotificationActivated(ToastNotificationActivatedEventArgsCompat activation)
    {
        // Arrives on a background thread.
        var arguments = ToastArguments.Parse(activation.Argument);
        string action = arguments.TryGetValue(ActionKey, out string? actionValue) ? actionValue : "open";
        string? folder = arguments.TryGetValue(FolderKey, out string? folderValue) ? folderValue : null;
        mainWindow.Dispatcher.BeginInvoke(() => _ = HandleNotificationChoiceAsync(action, folder));
    }

    private async Task HandleNotificationChoiceAsync(string action, string? folder)
    {
        // "Save for now" leaves the recording and its marker as they are: it is offered again next time.
        if (action == "save" || folder == null) return;

        // Load only accepts real recording folders, whatever the notification arguments say.
        var recording = RecordingRecovery.Load(folder);
        mainWindow.ShowFromTray();
        if (recording == null) return;

        if (action is "summarize" or "transcribe")
        {
            var mode = action == "summarize" ? JobMode.TranscribeAndSummarize : JobMode.TranscribeOnly;
            var request = mainWindow.CreateRequest(mode, recording.Tracks, recording.Folder, "Meeting", recording.Folder);
            await mainWindow.StartJobAsync(request);
        }
        else
        {
            await mainWindow.OfferRecordingAsync(recording);
        }
    }
}
