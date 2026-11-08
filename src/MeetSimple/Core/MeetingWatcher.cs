using Microsoft.Win32;

namespace MeetSimple.Core;

/// <summary>
/// A call another app is in. Id is unique per call; AppName is what people call it ("Zoom", "Google Meet").
/// The device ids are the microphone and speakers the call app itself uses (null when unknown), so a recording
/// captures the right devices even when the call runs on a headset that is not the Windows default.
/// </summary>
public sealed record DetectedCall(string Id, string AppName, string? MicrophoneDeviceId, string? SpeakerDeviceId);

/// <summary>
/// Notices when a call starts and ends without listening to anything: it only looks at which apps hold a microphone.
///
/// Sources, combined so either one alone still works:
///  1. Running audio sessions on every microphone (Core Audio; gives the process and the device).
///  2. Windows' privacy "microphone in use" records per app (the taskbar's microphone icon uses them; they also name
///     packaged apps such as new Teams and WhatsApp). Windows writes them the instant an app opens or closes a
///     microphone, and the watcher is woken by that write, so a call is noticed within moments.
///
/// One app is one call, whichever source saw it and whichever helper process does its audio (WebView2 helpers are
/// traced back to their app). Holding the microphone starts a call, but does not have to last: Discord with
/// push-to-talk or self-mute lets go of it between words. So once a call has been announced, a dedicated call app
/// that is still playing call audio counts as still in the call. Browsers do not get this (a video playing after a
/// Meet call would otherwise keep a recording going for ever).
///
/// The steadiest "still in the call" signal is the app's network connections: voice travels over UDP, and the call
/// connection stays open whether anyone talks or not. Which socket is the voice one cannot be told from outside
/// (Discord opens helper sockets that live under two minutes, and its voice socket only after the call starts), so
/// every UDP socket the app holds during the call counts, and while any is open a quiet call stays alive. Leaving
/// closes all of them: none for 3 s with no microphone ends the call for certain, as does the app quitting. For apps
/// that keep sockets between calls, a long-standing one closing while the call is quiet raises the "Call ended?"
/// question, as does a call that has been completely silent for 3 minutes.
/// </summary>
public sealed class MeetingWatcher : IDisposable
{
    private static readonly TimeSpan BusyCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CertainCallDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan UncertainCallDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan UnannouncedEndGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AnnouncedEndGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SettleAfterChange = TimeSpan.FromMilliseconds(150);

    /// <summary>A socket that has been open this long and then closes during a quiet call suggests the call was left.</summary>
    private static readonly TimeSpan LongLivedSocketAge = TimeSpan.FromMinutes(2);

    /// <summary>No microphone and no call audio for this long asks "Call ended?" even with connections still open.</summary>
    private static readonly TimeSpan QuietCap = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Leaving a call closes all of the app's UDP sockets (Discord holds none outside a call). This long with none
    /// open, and no microphone, means the call was left; the wait covers a reconnect to a new voice server.
    /// </summary>
    private static readonly TimeSpan NoConnectionGrace = TimeSpan.FromSeconds(3);

    /// <summary>Using the microphone this long with no UDP socket means the call runs some other way (TCP relay).</summary>
    private static readonly TimeSpan NoUdpCall = TimeSpan.FromSeconds(10);

    /// <summary>A call app using another microphone or output for this long has moved there (recording follows).</summary>
    private static readonly TimeSpan DeviceMoveSettle = TimeSpan.FromSeconds(3);

    private const uint NotifyOnSubkeyChange = 0x00000001;
    private const uint NotifyOnValueChange = 0x00000004;
    private const string MicrophoneConsentStore =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static readonly (string Token, string Name)[] CallApps =
    {
        ("teams", "Teams"), ("zoom", "Zoom"), ("webex", "Webex"), ("ciscocollabhost", "Webex"), ("atmgr", "Webex"),
        ("slack", "Slack"), ("discord", "Discord"), ("skype", "Skype"), ("whatsapp", "WhatsApp"), ("signal", "Signal"),
        ("telegram", "Telegram"), ("gotomeeting", "GoTo Meeting"), ("g2mcomm", "GoTo Meeting"), ("ringcentral", "RingCentral"),
        ("facetime", "FaceTime"), ("phonelink", "Phone Link"), ("yourphone", "Phone Link"), ("phoneexperiencehost", "Phone Link"),
        ("messenger", "Messenger"), ("viber", "Viber"), ("mumble", "Mumble"), ("teamspeak", "TeamSpeak"), ("ts3client", "TeamSpeak"),
        ("guilded", "Guilded"), ("bluejeans", "BlueJeans"), ("chime", "Amazon Chime"), ("dialpad", "Dialpad"),
    };

    private static readonly (string Executable, string Name)[] Browsers =
    {
        ("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", "Firefox"), ("brave", "Brave"),
        ("opera", "Opera"), ("vivaldi", "Vivaldi"), ("arc", "Arc"),
    };

    /// <summary>Window-title hints for meetings in a browser tab. Specific on purpose: a hint only speeds up the question.</summary>
    private static readonly (string TitleHint, string Name)[] BrowserMeetings =
    {
        ("Meet -", "Google Meet"), ("Meet –", "Google Meet"), ("Google Meet", "Google Meet"),
        ("Microsoft Teams", "Teams"), ("Zoom Meeting", "Zoom"), ("Zoom Workplace", "Zoom"), ("Webex", "Webex"),
        ("Jitsi Meet", "Jitsi"), ("Whereby", "Whereby"), ("Discord", "Discord"), ("Slack", "Slack"),
        ("WhatsApp", "WhatsApp"), ("Messenger", "Messenger"), ("Skype", "Skype"),
    };

    private readonly SynchronizationContext context;
    private readonly object gate = new();
    private readonly Dictionary<string, CallState> calls = new(StringComparer.Ordinal);

    /// <summary>When each open UDP socket ("process|family:port") was first seen. Only touched by the watcher thread.</summary>
    private readonly Dictionary<string, DateTime> socketFirstSeen = new(StringComparer.Ordinal);
    private readonly int ownProcessId = Environment.ProcessId;
    private readonly string ownProcessName =
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "MeetSimple").ToLowerInvariant();
    private Thread? watchThread;
    private ManualResetEvent? stopSignal;

    public MeetingWatcher()
    {
        context = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    /// <summary>Raised on the creating thread once an app has held the microphone long enough to be a call.</summary>
    public event Action<DetectedCall>? CallStarted;

    /// <summary>
    /// Raised on the creating thread when the app has gone quiet (no microphone, no call audio) or has quit.
    /// The flag is true when the end is certain (the app quit, or it closed all its connections); false when the app has
    /// only gone quiet, which may be a silent moment in a call.
    /// </summary>
    public event Action<DetectedCall, bool>? CallEnded;

    /// <summary>
    /// An announced call moved to another microphone or output (headset plugged in, device changed in the call app).
    /// Raised on the UI thread with the same call id and the new device ids.
    /// </summary>
    public event Action<DetectedCall>? CallDevicesChanged;

    public bool IsRunning
    {
        get { lock (gate) return watchThread != null; }
    }

    public void Start()
    {
        lock (gate)
        {
            if (watchThread != null) return;
            var signal = new ManualResetEvent(false);
            stopSignal = signal;
            watchThread = new Thread(() => WatchLoop(signal)) { IsBackground = true, Name = "Meet Simple call watcher" };
            watchThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        ManualResetEvent? signal;
        lock (gate)
        {
            thread = watchThread;
            signal = stopSignal;
            watchThread = null;
            stopSignal = null;
            calls.Clear();
        }
        signal?.Set();
        thread?.Join(TimeSpan.FromSeconds(3));
        signal?.Dispose();
    }

    public void Dispose() => Stop();

    // ----- the loop -----

    /// <summary>
    /// Checks every second while anything is being tracked (every 3 s otherwise), and immediately whenever Windows
    /// records an app opening or closing a microphone. A change notification belongs to the thread that asked for
    /// it, so the loop owns its thread, and each notification is requested again only after it has fired.
    /// </summary>
    private void WatchLoop(ManualResetEvent stop)
    {
        using var microphoneRecordsChanged = new AutoResetEvent(false);
        RegistryKey? microphoneRecords = null;
        try
        {
            microphoneRecords = Registry.CurrentUser.OpenSubKey(MicrophoneConsentStore);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No records on this Windows; the regular checks still work.
        }

        try
        {
            bool notificationArmed = false;
            var wakeSignals = new WaitHandle[] { stop, microphoneRecordsChanged };
            while (true)
            {
                if (!notificationArmed && microphoneRecords != null)
                {
                    notificationArmed = RegNotifyChangeKeyValue(microphoneRecords.Handle, true,
                        NotifyOnSubkeyChange | NotifyOnValueChange, microphoneRecordsChanged.SafeWaitHandle, true) == 0;
                }

                bool tracking = Check(stop);

                int woken = WaitHandle.WaitAny(wakeSignals, tracking ? BusyCheckInterval : IdleCheckInterval);
                if (woken == 0) return;
                if (woken == 1)
                {
                    notificationArmed = false;
                    // Windows writes the start and the stop time separately; read them after both have landed.
                    if (stop.WaitOne(SettleAfterChange)) return;
                }
            }
        }
        catch (Exception error)
        {
            AppHealth.LogError(error, "Call watcher stopped");
        }
        finally
        {
            microphoneRecords?.Dispose();
        }
    }

    // ----- one check -----

    private sealed record Sighting(string AppName, bool IsCertainlyCall, string? MicrophoneDeviceId, string? SpeakerDeviceId);

    private sealed class CallState
    {
        public required string CallId { get; init; }
        public required DateTime FirstSeen { get; init; }
        public required string AppName { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsCertainlyCall { get; set; }
        public string? MicrophoneDeviceId { get; set; }
        public string? SpeakerDeviceId { get; set; }
        public DetectedCall? Announced { get; set; }

        /// <summary>The app's open UDP sockets during this call, with when each was first seen.</summary>
        public Dictionary<string, DateTime> CallSockets { get; } = new(StringComparer.Ordinal);
        public bool SocketsSeeded { get; set; }
        public bool LongLivedSocketClosed { get; set; }

        /// <summary>The app had a UDP socket open during this call, so all of them closing means it was left.</summary>
        public bool HadSockets { get; set; }
        public DateTime? SocketsGoneSince { get; set; }
        public DateTime? QuietSince { get; set; }
        public DateTime? DevicesDifferSince { get; set; }
        public bool QuietLogged { get; set; }
    }

    /// <summary>Updates the calls being tracked. Returns true while anything is tracked (so checks stay frequent).</summary>
    private bool Check(WaitHandle stop)
    {
        try
        {
            var processes = SystemAudioState.Processes();
            var sightings = FindCalls(processes, out var callAppsPlayingAudio);
            var now = DateTime.UtcNow;
            var socketsByCall = SocketsByCall(TrackUdpSockets(now), processes);
            var started = new List<DetectedCall>();
            var moved = new List<DetectedCall>();
            var ended = new List<(DetectedCall Call, bool IsCertain)>();
            bool tracking;
            lock (gate)
            {
                if (stop.WaitOne(0)) return false;
                foreach (var (callKey, sighting) in sightings)
                {
                    if (!calls.TryGetValue(callKey, out var state))
                    {
                        state = new CallState
                        {
                            CallId = Guid.NewGuid().ToString("N"),
                            FirstSeen = now,
                            AppName = sighting.AppName,
                            IsCertainlyCall = sighting.IsCertainlyCall,
                        };
                        calls[callKey] = state;
                    }
                    state.LastSeen = now;
                    state.MicrophoneDeviceId = sighting.MicrophoneDeviceId ?? state.MicrophoneDeviceId;
                    state.SpeakerDeviceId = sighting.SpeakerDeviceId ?? state.SpeakerDeviceId;
                    // A browser whose tab turns out to be a meeting becomes certain, with a better name.
                    if (state.Announced == null && sighting.IsCertainlyCall && !state.IsCertainlyCall)
                    {
                        state.IsCertainlyCall = true;
                        state.AppName = sighting.AppName;
                    }
                    TimeSpan delay = state.IsCertainlyCall ? CertainCallDelay : UncertainCallDelay;
                    bool devicesDiffer = state.Announced is { } announcedCall
                        && (announcedCall.MicrophoneDeviceId != state.MicrophoneDeviceId || announcedCall.SpeakerDeviceId != state.SpeakerDeviceId);
                    if (!devicesDiffer) state.DevicesDifferSince = null; else state.DevicesDifferSince ??= now;
                    // Only a move that holds counts, so an app with sessions on two devices does not flip back and forth.
                    if (state.Announced is { } announced && state.DevicesDifferSince is { } differSince && now - differSince >= DeviceMoveSettle)
                    {
                        state.DevicesDifferSince = null;
                        state.Announced = announced with
                        {
                            MicrophoneDeviceId = state.MicrophoneDeviceId,
                            SpeakerDeviceId = state.SpeakerDeviceId,
                        };
                        moved.Add(state.Announced);
                        AppHealth.LogEvent($"Call devices changed: {state.AppName} now uses another microphone or output.");
                    }
                    if (state.Announced == null && now - state.FirstSeen >= delay)
                    {
                        state.Announced = new DetectedCall(state.CallId, state.AppName, state.MicrophoneDeviceId, state.SpeakerDeviceId);
                        started.Add(state.Announced);
                        AppHealth.LogEvent($"Call detected: {state.AppName} ({(state.IsCertainlyCall ? "call app or meeting page" : "browser")}, "
                                           + $"microphone {(state.MicrophoneDeviceId != null ? "known" : "default")}, "
                                           + $"speakers {(state.SpeakerDeviceId != null ? "known" : "default")}).");
                    }
                }

                foreach (var (callKey, state) in calls.ToList())
                {
                    UpdateCallSockets(callKey, state, socketsByCall, now);

                    // Using the microphone: plainly in the call, and whatever happened to sockets meanwhile is forgiven.
                    if (sightings.ContainsKey(callKey))
                    {
                        state.QuietSince = null;
                        state.LongLivedSocketClosed = false;
                        if (state.SocketsGoneSince is { } goneWhileTalking && now - goneWhileTalking >= NoUdpCall)
                        {
                            state.SocketsGoneSince = null;
                            state.HadSockets = false;
                        }
                        continue;
                    }

                    string? endReason = null;
                    bool isCertain = false;
                    if (!IsAppRunning(callKey, processes))
                    {
                        endReason = $"{state.AppName} closed.";
                        isCertain = true;
                    }
                    else if (state.Announced != null)
                    {
                        bool playingCallAudio = callAppsPlayingAudio.Contains(callKey);
                        if (playingCallAudio) state.QuietSince = null; else state.QuietSince ??= now;

                        if (state.SocketsGoneSince is { } goneSince)
                        {
                            // Muted or push-to-talk keeps the voice connection open; leaving closes every one.
                            if (now - goneSince < NoConnectionGrace)
                            {
                                state.LastSeen = now;
                                continue;
                            }
                            endReason = $"{state.AppName} let go of the microphone and closed all its connections (left the call).";
                            isCertain = true;
                        }
                        else if (state.LongLivedSocketClosed)
                        {
                            endReason = $"{state.AppName} went quiet and closed a long-standing connection (probably left the call).";
                        }
                        else if (state.QuietSince is { } quietSince && now - quietSince >= QuietCap)
                        {
                            endReason = $"{state.AppName} has used no microphone and played no call audio for {QuietCap.TotalMinutes:0} minutes.";
                        }
                        else if (playingCallAudio || state.CallSockets.Count > 0)
                        {
                            // Push-to-talk, muted, or nobody talking: the call is still connected.
                            state.LastSeen = now;
                            if (!playingCallAudio && !state.QuietLogged)
                            {
                                state.QuietLogged = true;
                                AppHealth.LogEvent($"Call quiet: {state.AppName} let go of the microphone; {state.CallSockets.Count} of its connections are still open.");
                            }
                            continue;
                        }
                    }

                    if (endReason == null)
                    {
                        TimeSpan grace = state.Announced == null ? UnannouncedEndGrace : AnnouncedEndGrace;
                        if (now - state.LastSeen < grace) continue;
                        endReason = $"{state.AppName} used no microphone, played no call audio and had no open connection for {grace.TotalSeconds:0} s.";
                    }

                    calls.Remove(callKey);
                    if (state.Announced != null)
                    {
                        ended.Add((state.Announced, isCertain));
                        AppHealth.LogEvent("Call ended: " + endReason);
                    }
                }
                tracking = calls.Count > 0;
            }
            foreach (var call in started) context.Post(_ => CallStarted?.Invoke(call), null);
            foreach (var call in moved) context.Post(_ => CallDevicesChanged?.Invoke(call), null);
            foreach (var (call, isCertain) in ended) context.Post(_ => CallEnded?.Invoke(call, isCertain), null);
            return tracking;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppHealth.LogError(error, "Call watcher check");
            return true;
        }
    }

    /// <summary>Apps holding a microphone right now that look like a call, keyed so that one app is always one call.</summary>
    /// <summary>Updates when each UDP socket was first seen and returns the ones open now.</summary>
    private HashSet<string> TrackUdpSockets(DateTime now)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (processId, endpoint) in SystemAudioState.UdpSockets())
        {
            if (processId == ownProcessId) continue;
            string socketKey = $"{processId}|{endpoint}";
            open.Add(socketKey);
            socketFirstSeen.TryAdd(socketKey, now);
        }
        foreach (string closed in socketFirstSeen.Keys.Where(socketKey => !open.Contains(socketKey)).ToList())
        {
            socketFirstSeen.Remove(closed);
        }
        return open;
    }

    /// <summary>Open UDP sockets grouped by the call app that owns them (whichever of the app's processes holds them).</summary>
    private static Dictionary<string, HashSet<string>> SocketsByCall(HashSet<string> openSockets, ProcessTable processes)
    {
        var byCall = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string socketKey in openSockets)
        {
            int separator = socketKey.IndexOf('|');
            if (!int.TryParse(socketKey.AsSpan(0, separator), out int processId)) continue;
            if (processes.OwnerNameOf(processId) is not { } owner || ClassifyCallKey(owner) is not { } callKey) continue;
            if (!callKey.StartsWith(AppKeyPrefix, StringComparison.Ordinal)) continue; // browsers: too much unrelated traffic
            if (!byCall.TryGetValue(callKey, out var sockets)) byCall[callKey] = sockets = new HashSet<string>(StringComparer.Ordinal);
            sockets.Add(socketKey);
        }
        return byCall;
    }

    /// <summary>
    /// Keeps a call's socket list current. Which socket carries the voice cannot be told (Discord opens helpers that
    /// live under two minutes, and its voice socket after the call starts), so only "none left" is trusted; a
    /// long-standing one closing during a quiet stretch is a hint for apps that keep sockets open between calls.
    /// </summary>
    private void UpdateCallSockets(string callKey, CallState state, Dictionary<string, HashSet<string>> socketsByCall, DateTime now)
    {
        if (state.Announced == null || !callKey.StartsWith(AppKeyPrefix, StringComparison.Ordinal)) return;
        var appSockets = socketsByCall.TryGetValue(callKey, out var open) ? open : new HashSet<string>();

        var closedAges = new List<string>();
        foreach (var (socketKey, firstSeen) in state.CallSockets.ToList())
        {
            if (appSockets.Contains(socketKey)) continue;
            state.CallSockets.Remove(socketKey);
            TimeSpan openFor = now - firstSeen;
            closedAges.Add($"{openFor.TotalSeconds:0} s");
            if (openFor >= LongLivedSocketAge && state.QuietSince != null) state.LongLivedSocketClosed = true;
        }

        int openedCount = 0;
        foreach (string socketKey in appSockets)
        {
            if (state.CallSockets.ContainsKey(socketKey) || !socketFirstSeen.TryGetValue(socketKey, out DateTime firstSeen)) continue;
            state.CallSockets[socketKey] = firstSeen;
            openedCount++;
        }

        if (!state.SocketsSeeded)
        {
            state.SocketsSeeded = true;
            AppHealth.LogEvent($"Call connections: {state.AppName} had {state.CallSockets.Count} UDP connections open when the call was detected.");
        }
        else if (openedCount > 0 || closedAges.Count > 0)
        {
            string closedText = closedAges.Count > 0 ? $"closed {closedAges.Count} (open {string.Join(", ", closedAges)})" : "closed 0";
            AppHealth.LogEvent($"Call connections: {state.AppName} opened {openedCount}, {closedText}; {state.CallSockets.Count} open.");
        }

        if (state.CallSockets.Count > 0)
        {
            state.HadSockets = true;
            state.SocketsGoneSince = null;
        }
        else if (state.HadSockets)
        {
            state.SocketsGoneSince ??= now;
        }
    }

    private Dictionary<string, Sighting> FindCalls(ProcessTable processes, out HashSet<string> callAppsPlayingAudio)
    {
        // Which apps hold a microphone, and on which devices they record and play.
        var microphoneUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var microphoneDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var speakerDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in SystemAudioState.ActiveSessions())
        {
            if (session.ProcessId == ownProcessId || processes.OwnerNameOf(session.ProcessId) is not { } owner) continue;
            if (session.IsCapture)
            {
                microphoneUsers.Add(owner);
                microphoneDevices.TryAdd(owner, session.DeviceId);
            }
            else
            {
                speakerDevices.TryAdd(owner, session.DeviceId);
            }
        }
        AddFromPrivacyRecords(microphoneUsers, processes);
        microphoneUsers.Remove(ownProcessName);

        // Dedicated call apps that are playing sound right now (browsers excluded on purpose, see the class summary).
        callAppsPlayingAudio = speakerDevices.Keys
            .Where(owner => owner != ownProcessName)
            .Select(ClassifyCallKey)
            .Where(callKey => callKey != null && callKey.StartsWith(AppKeyPrefix, StringComparison.Ordinal))
            .Select(callKey => callKey!)
            .ToHashSet(StringComparer.Ordinal);

        // The same app can show up under several names (new Teams: "ms-teams" in audio sessions and
        // "msteams_8wekyb3d8bbwe" in the privacy records), so group by call key and merge what each name knows.
        var usersByCall = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string user in microphoneUsers)
        {
            if (ClassifyCallKey(user) is not { } callKey) continue;
            if (!usersByCall.TryGetValue(callKey, out var users)) usersByCall[callKey] = users = new List<string>();
            users.Add(user);
        }

        var sightings = new Dictionary<string, Sighting>(StringComparer.Ordinal);
        foreach (var (callKey, users) in usersByCall)
        {
            string? microphoneDevice = users.Select(user => microphoneDevices.GetValueOrDefault(user)).FirstOrDefault(id => id != null);
            string? speakerDevice = users.Select(user => speakerDevices.GetValueOrDefault(user)).FirstOrDefault(id => id != null);

            if (callKey.StartsWith(BrowserKeyPrefix, StringComparison.Ordinal))
            {
                string executable = callKey[BrowserKeyPrefix.Length..];
                var (appName, isMeeting) = NameBrowserCall(executable, processes, NeedsBrowserTitle(callKey));
                sightings[callKey] = new Sighting(appName, isMeeting, microphoneDevice, speakerDevice);
            }
            else
            {
                sightings[callKey] = new Sighting(callKey[AppKeyPrefix.Length..], true, microphoneDevice, speakerDevice);
            }
        }
        return sightings;
    }

    private const string AppKeyPrefix = "app:";
    private const string BrowserKeyPrefix = "browser:";

    /// <summary>"app:Teams" for every Teams process or package name, "browser:chrome" for Chrome; null when not a call app.</summary>
    private static string? ClassifyCallKey(string identity)
    {
        foreach (var (token, name) in CallApps)
        {
            if (identity.Contains(token, StringComparison.OrdinalIgnoreCase)) return AppKeyPrefix + name;
        }
        foreach (var (executable, _) in Browsers)
        {
            if (identity.Equals(executable, StringComparison.OrdinalIgnoreCase)) return BrowserKeyPrefix + executable;
        }
        return null;
    }

    /// <summary>Window titles are only read while a browser call has not been asked about yet (once a second at most).</summary>
    private bool NeedsBrowserTitle(string callKey)
    {
        lock (gate)
        {
            return !calls.TryGetValue(callKey, out var state) || (state.Announced == null && !state.IsCertainlyCall);
        }
    }

    private static (string AppName, bool IsMeeting) NameBrowserCall(string executable, ProcessTable processes, bool readTitles)
    {
        string browserName = Browsers.First(browser => browser.Executable == executable).Name;
        if (readTitles)
        {
            var browserProcessIds = processes.IdsNamed(executable).ToHashSet();
            foreach (string title in SystemAudioState.WindowTitles(browserProcessIds))
            {
                foreach (var (titleHint, meetingName) in BrowserMeetings)
                {
                    if (title.Contains(titleHint, StringComparison.OrdinalIgnoreCase)) return (meetingName, true);
                }
            }
        }
        return (browserName + " call", false);
    }

    /// <summary>A call ends at once when its app is gone entirely (closed, crashed), without waiting out the grace.</summary>
    private static bool IsAppRunning(string callKey, ProcessTable processes)
    {
        if (callKey.StartsWith(BrowserKeyPrefix, StringComparison.Ordinal))
        {
            return processes.IsRunning(callKey[BrowserKeyPrefix.Length..]);
        }
        string appName = callKey[AppKeyPrefix.Length..];
        return CallApps.Any(entry => entry.Name == appName && processes.AnyNameContains(entry.Token));
    }

    // ----- Windows' privacy records -----

    private static void AddFromPrivacyRecords(HashSet<string> microphoneUsers, ProcessTable processes)
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
                        if (!IsUsingMicrophoneNow(appKey)) continue;
                        // Paths are stored with '#' in place of '\', e.g. C:#Program Files#Zoom#bin#Zoom.exe
                        string fileName = pathKeyName[(pathKeyName.LastIndexOf('#') + 1)..];
                        string name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
                        // A record left "in use" by an app that crashed must not keep a call alive forever.
                        if (processes.IsRunning(name)) microphoneUsers.Add(name);
                    }
                }
                else
                {
                    using var appKey = store.OpenSubKey(appKeyName);
                    if (IsUsingMicrophoneNow(appKey)) microphoneUsers.Add(appKeyName.ToLowerInvariant());
                }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unavailable on this Windows version or blocked by policy; audio sessions still work.
        }
    }

    /// <summary>In use while the last start is newer than the last stop (the stop is 0 while the app has never stopped).</summary>
    private static bool IsUsingMicrophoneNow(RegistryKey? appKey) =>
        appKey?.GetValue("LastUsedTimeStart") is long started
        && appKey.GetValue("LastUsedTimeStop") is long stopped
        && started > stopped;

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree,
        uint notifyFilter, Microsoft.Win32.SafeHandles.SafeWaitHandle eventHandle, bool asynchronous);
}
