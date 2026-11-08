using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetSimple.Core;

/// <summary>A call another app is in: Id is unique per call, AppName is what people call it ("Zoom", "Google Meet").</summary>
public sealed record DetectedCall(string Id, string AppName);

/// <summary>
/// Notices when a call starts and ends, without listening to anything. Windows knows which apps are using a
/// microphone (the taskbar's microphone icon comes from the same information); a known call app, or a browser on a
/// meeting page, that keeps the microphone for a few seconds is a call, and letting go of it ends the call.
///
/// Two independent sources are combined so one missing still works:
///  1. Audio sessions on every active capture device (documented Core Audio API, gives the process).
///  2. The privacy "microphone in use" records Windows keeps per app (also covers packaged apps such as new Teams).
/// </summary>
public sealed class MeetingWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Voice typing or a quick voice message must not look like a call.</summary>
    private static readonly TimeSpan SustainTime = TimeSpan.FromSeconds(8);

    /// <summary>Some apps let go of the microphone for a moment (switching devices), which is not the end of a call.</summary>
    private static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(5);

    private const string MicrophoneConsentStore =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static readonly (string Token, string Name)[] CallApps =
    {
        ("teams", "Teams"), ("zoom", "Zoom"), ("webex", "Webex"), ("ciscocollabhost", "Webex"), ("atmgr", "Webex"),
        ("slack", "Slack"), ("discord", "Discord"), ("skype", "Skype"), ("whatsapp", "WhatsApp"), ("signal", "Signal"),
        ("telegram", "Telegram"), ("gotomeeting", "GoTo Meeting"), ("g2mcomm", "GoTo Meeting"), ("ringcentral", "RingCentral"),
    };

    private static readonly (string Executable, string Name)[] Browsers =
    {
        ("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", "Firefox"), ("brave", "Brave"),
        ("opera", "Opera"), ("vivaldi", "Vivaldi"), ("arc", "Arc"),
    };

    /// <summary>Window-title hints for meetings that run in a browser tab.</summary>
    private static readonly (string TitleHint, string Name)[] BrowserMeetings =
    {
        ("Meet -", "Google Meet"), ("Meet –", "Google Meet"), ("Google Meet", "Google Meet"),
        ("Microsoft Teams", "Teams"), ("Zoom", "Zoom"), ("Webex", "Webex"), ("Jitsi", "Jitsi"),
        ("Whereby", "Whereby"), ("Discord", "Discord"), ("Slack", "Slack"),
    };

    private readonly SynchronizationContext context;
    private readonly object gate = new();
    private readonly Dictionary<string, CallState> calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly int ownProcessId = Environment.ProcessId;
    private readonly string ownProcessName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
    private Timer? pollTimer;
    private int pollRunning;

    public MeetingWatcher()
    {
        context = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    /// <summary>Raised on the creating thread once an app has held the microphone long enough to be a call.</summary>
    public event Action<DetectedCall>? CallStarted;

    /// <summary>Raised on the creating thread when that app has let go of the microphone.</summary>
    public event Action<DetectedCall>? CallEnded;

    public bool IsRunning
    {
        get { lock (gate) return pollTimer != null; }
    }

    public void Start()
    {
        lock (gate)
        {
            pollTimer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            pollTimer?.Dispose();
            pollTimer = null;
            calls.Clear();
        }
    }

    public void Dispose() => Stop();

    private sealed class CallState
    {
        public required DetectedCall Call { get; init; }
        public required DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; set; }
        public bool Announced { get; set; }
    }

    private void Poll()
    {
        // A slow poll (a device waking up) must not stack up behind itself.
        if (Interlocked.Exchange(ref pollRunning, 1) == 1) return;
        try
        {
            var appsInCalls = FindAppsInCalls();
            var now = DateTime.UtcNow;
            var started = new List<DetectedCall>();
            var ended = new List<DetectedCall>();
            lock (gate)
            {
                if (pollTimer == null) return;
                foreach (var (identity, appName) in appsInCalls)
                {
                    if (!calls.TryGetValue(identity, out var state))
                    {
                        state = new CallState { Call = new DetectedCall(Guid.NewGuid().ToString("N"), appName), FirstSeen = now };
                        calls[identity] = state;
                    }
                    state.LastSeen = now;
                    if (!state.Announced && now - state.FirstSeen >= SustainTime)
                    {
                        state.Announced = true;
                        started.Add(state.Call);
                    }
                }
                foreach (var (identity, state) in calls.ToList())
                {
                    if (now - state.LastSeen < EndGrace) continue;
                    calls.Remove(identity);
                    if (state.Announced) ended.Add(state.Call);
                }
            }
            foreach (var call in started) context.Post(_ => CallStarted?.Invoke(call), null);
            foreach (var call in ended) context.Post(_ => CallEnded?.Invoke(call), null);
        }
        catch (Exception error)
        {
            AppHealth.LogError(error, "Call watcher");
        }
        finally
        {
            Volatile.Write(ref pollRunning, 0);
        }
    }

    /// <summary>Apps that are using a microphone right now and look like a call, keyed by app identity.</summary>
    private Dictionary<string, string> FindAppsInCalls()
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFromAudioSessions(identities);
        AddFromPrivacyRecords(identities);
        identities.Remove(ownProcessName);

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string identity in identities)
        {
            if (Recognize(identity) is { } appName) found[identity] = appName;
        }
        return found;
    }

    private void AddFromAudioSessions(HashSet<string> identities)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession) continue;
                        int processId = (int)session.GetProcessID;
                        if (processId == 0 || processId == ownProcessId) continue;
                        if (ProcessNameOf(processId) is { } name) identities.Add(name);
                    }
                }
            }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // A device disappearing mid-scan; the next poll sees the new state.
        }
    }

    private static void AddFromPrivacyRecords(HashSet<string> identities)
    {
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(MicrophoneConsentStore);
            if (store == null) return;
            foreach (string appKeyName in store.GetSubKeyNames())
            {
                if (appKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var desktopApps = store.OpenSubKey(appKeyName);
                    if (desktopApps == null) continue;
                    foreach (string pathKeyName in desktopApps.GetSubKeyNames())
                    {
                        using var appKey = desktopApps.OpenSubKey(pathKeyName);
                        if (IsUsingMicrophoneNow(appKey))
                        {
                            // Paths are stored with '#' in place of '\', e.g. C:#Program Files#Zoom#bin#Zoom.exe
                            string fileName = pathKeyName[(pathKeyName.LastIndexOf('#') + 1)..];
                            identities.Add(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant());
                        }
                    }
                }
                else
                {
                    using var appKey = store.OpenSubKey(appKeyName);
                    if (IsUsingMicrophoneNow(appKey)) identities.Add(appKeyName.ToLowerInvariant());
                }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unavailable on this Windows version or blocked by policy; audio sessions still work.
        }
    }

    /// <summary>In use while the last start is newer than the last stop (the stop is 0 while it has never stopped).</summary>
    private static bool IsUsingMicrophoneNow(RegistryKey? appKey) =>
        appKey?.GetValue("LastUsedTimeStart") is long started
        && appKey.GetValue("LastUsedTimeStop") is long stopped
        && started > stopped;

    private static string? ProcessNameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null; // Exited in the meantime.
        }
    }

    private static string? Recognize(string identity)
    {
        foreach (var (token, name) in CallApps)
        {
            if (identity.Contains(token, StringComparison.OrdinalIgnoreCase)) return name;
        }
        foreach (var (executable, name) in Browsers)
        {
            if (identity.Equals(executable, StringComparison.OrdinalIgnoreCase)) return BrowserCallName(executable, name);
        }
        return null;
    }

    /// <summary>Names the meeting from the browser's window title when it can ("Google Meet"), else "Chrome call".</summary>
    private static string BrowserCallName(string executable, string browserName)
    {
        foreach (var process in Process.GetProcessesByName(executable))
        {
            using (process)
            {
                string title;
                try
                {
                    title = process.MainWindowTitle;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                foreach (var (titleHint, meetingName) in BrowserMeetings)
                {
                    if (title.Contains(titleHint, StringComparison.OrdinalIgnoreCase)) return meetingName;
                }
            }
        }
        return browserName + " call";
    }
}
