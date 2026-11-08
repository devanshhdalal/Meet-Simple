using System.Runtime.InteropServices;

namespace MeetSimple.Core;

/// <summary>One app stream that is running right now: which process, on which device, and whether it records or plays.</summary>
internal sealed record ActiveAudioSession(int ProcessId, string DeviceId, bool IsCapture);

/// <summary>
/// Read-only snapshots of what the system is doing, for the call watcher: running audio sessions, processes and
/// window titles. Everything here is a plain query - no callbacks are registered, nothing is listened to - so it can
/// run every second for hours without leaking anything (NAudio's session manager registers a COM callback each time
/// it is created and unregisters it in a finalizer that may throw, which is the wrong pattern for a long-lived poll).
/// </summary>
internal static class SystemAudioState
{
    private const int CaptureFlow = 1;
    private const int RenderFlow = 0;
    private const int ActiveDeviceState = 1;
    private const int ActiveSessionState = 1;
    private const int AllContexts = 0x17;
    private const int SystemSoundsSession = 0; // IsSystemSoundsSession returns S_OK for it, S_FALSE otherwise.
    private static readonly Guid AudioSessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>Every active (started) audio session on every active microphone and speaker.</summary>
    public static List<ActiveAudioSession> ActiveSessions()
    {
        var sessions = new List<ActiveAudioSession>();
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            AddSessions(enumerator, CaptureFlow, sessions);
            AddSessions(enumerator, RenderFlow, sessions);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // Audio service restarting or a device disappearing mid-scan; the next check sees the new state.
        }
        finally
        {
            Release(enumerator);
        }
        return sessions;
    }

    private static void AddSessions(IMMDeviceEnumerator enumerator, int dataFlow, List<ActiveAudioSession> sessions)
    {
        if (enumerator.EnumAudioEndpoints(dataFlow, ActiveDeviceState, out var devices) != 0 || devices == null) return;
        try
        {
            devices.GetCount(out int deviceCount);
            for (int deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                if (devices.Item(deviceIndex, out var device) != 0 || device == null) continue;
                object? managerObject = null;
                IAudioSessionEnumerator? sessionList = null;
                try
                {
                    if (device.GetId(out string deviceId) != 0) continue;
                    Guid managerId = AudioSessionManager2Id;
                    if (device.Activate(ref managerId, AllContexts, IntPtr.Zero, out managerObject) != 0) continue;
                    var manager = (IAudioSessionManager2)managerObject;
                    if (manager.GetSessionEnumerator(out sessionList) != 0 || sessionList == null) continue;
                    sessionList.GetCount(out int sessionCount);
                    for (int sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                    {
                        if (sessionList.GetSession(sessionIndex, out var control) != 0 || control == null) continue;
                        try
                        {
                            var session = (IAudioSessionControl2)control;
                            if (session.GetState(out int state) != 0 || state != ActiveSessionState) continue;
                            if (session.IsSystemSoundsSession() == SystemSoundsSession) continue;
                            session.GetProcessId(out int processId);
                            if (processId > 0) sessions.Add(new ActiveAudioSession(processId, deviceId, dataFlow == CaptureFlow));
                        }
                        finally
                        {
                            Release(control);
                        }
                    }
                }
                catch (Exception error) when (error is COMException or InvalidCastException)
                {
                    // This device went away; carry on with the others.
                }
                finally
                {
                    Release(sessionList);
                    Release(managerObject);
                    Release(device);
                }
            }
        }
        finally
        {
            Release(devices);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }

    // ----- processes -----

    /// <summary>All running processes with their parent, from one consistent snapshot (much cheaper than Process.GetProcessById per lookup).</summary>
    public static ProcessTable Processes()
    {
        var table = new ProcessTable();
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle) return table;
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (bool hasEntry = Process32FirstW(snapshot, ref entry); hasEntry; hasEntry = Process32NextW(snapshot, ref entry))
            {
                string name = Path.GetFileNameWithoutExtension(entry.ExeFile).ToLowerInvariant();
                table.Add(entry.ProcessId, entry.ParentProcessId, name);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return table;
    }

    // ----- network -----

    /// <summary>Ports used by system discovery protocols (mDNS, LLMNR, SSDP, NetBIOS), never a call's media connection.</summary>
    private static readonly HashSet<int> DiscoveryPorts = new() { 137, 138, 1900, 3702, 5353, 5355 };

    /// <summary>
    /// Open UDP sockets with the process that owns them (the same list "netstat -ano -p udp" shows). Calls carry
    /// voice over UDP, and that socket stays open for exactly as long as the call - silent or not. Only which
    /// sockets exist is read, never any traffic.
    /// </summary>
    public static List<(int ProcessId, string Endpoint)> UdpSockets()
    {
        var sockets = new List<(int, string)>();
        AddUdpSockets(InterNetwork, 12, sockets);
        AddUdpSockets(InterNetworkV6, 28, sockets);
        return sockets;
    }

    private static void AddUdpSockets(int addressFamily, int rowSize, List<(int, string)> sockets)
    {
        int bufferSize = 0;
        IntPtr table = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint result = GetExtendedUdpTable(table, ref bufferSize, false, addressFamily, UdpTableOwnerProcess, 0);
                if (result == 0) break;
                if (result != InsufficientBuffer) return;
                if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
                table = Marshal.AllocHGlobal(bufferSize);
                if (attempt == 2) return;
            }
            if (table == IntPtr.Zero) return;

            int rowCount = Marshal.ReadInt32(table);
            // IPv4 row: address, port, process. IPv6 row: 16-byte address, scope, port, process.
            int portOffset = addressFamily == InterNetwork ? 4 : 20;
            int processOffset = addressFamily == InterNetwork ? 8 : 24;
            string family = addressFamily == InterNetwork ? "v4" : "v6";
            for (int index = 0; index < rowCount; index++)
            {
                IntPtr row = table + 4 + index * rowSize;
                int rawPort = Marshal.ReadInt32(row, portOffset);
                int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF); // network byte order
                if (port < 1024 || DiscoveryPorts.Contains(port)) continue;
                sockets.Add((Marshal.ReadInt32(row, processOffset), $"{family}:{port}"));
            }
        }
        finally
        {
            if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
        }
    }

    // ----- window titles -----

    /// <summary>Titles of the visible top-level windows that belong to the given processes (a browser shows its active tab).</summary>
    public static List<string> WindowTitles(IReadOnlySet<int> processIds)
    {
        var titles = new List<string>();
        if (processIds.Count == 0) return titles;
        var buffer = new char[512];
        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle)) return true;
            // 0 means the window disappeared while being enumerated.
            if (GetWindowThreadProcessId(windowHandle, out int processId) == 0 || !processIds.Contains(processId)) return true;
            int length = GetWindowTextW(windowHandle, buffer, buffer.Length);
            if (length > 0) titles.Add(new string(buffer, 0, length));
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    // ----- Core Audio COM interfaces (vtable order exactly as in mmdeviceapi.h / audiopolicy.h) -----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, int classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object anInterface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessionList);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object? session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out int processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    // ----- process and window functions -----

    private const int InterNetwork = 2;
    private const int InterNetworkV6 = 23;
    private const int UdpTableOwnerProcess = 1;
    private const uint InsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted,
        int addressFamily, int tableClass, int reserved);

    private const int SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size;
        public int UsageCount;
        public int ProcessId;
        public IntPtr DefaultHeapId;
        public int ModuleId;
        public int ThreadCount;
        public int ParentProcessId;
        public int BasePriority;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr windowHandle, [Out] char[] text, int maximumCount);
}

/// <summary>Process names (lower case, no ".exe") and parents from one snapshot.</summary>
internal sealed class ProcessTable
{
    private const string WebViewHost = "msedgewebview2";
    private readonly Dictionary<int, (int ParentId, string Name)> processes = new();
    private readonly HashSet<string> runningNames = new(StringComparer.OrdinalIgnoreCase);

    public void Add(int processId, int parentId, string name)
    {
        processes[processId] = (parentId, name);
        runningNames.Add(name);
    }

    public bool IsRunning(string name) => runningNames.Contains(name);

    public bool AnyNameContains(string token) =>
        runningNames.Any(name => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<int> IdsNamed(string name) =>
        processes.Where(entry => entry.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key);

    /// <summary>
    /// The app a process belongs to. Apps built on WebView2 (new Teams, WhatsApp and others) do their audio in an
    /// msedgewebview2 helper, so the parent chain is followed up to the app that started it.
    /// </summary>
    public string? OwnerNameOf(int processId)
    {
        int currentId = processId;
        for (int depth = 0; depth < 6 && processes.TryGetValue(currentId, out var entry); depth++)
        {
            if (!entry.Name.Equals(WebViewHost, StringComparison.OrdinalIgnoreCase)) return entry.Name;
            if (entry.ParentId == currentId || entry.ParentId == 0) break;
            currentId = entry.ParentId;
        }
        return processes.TryGetValue(processId, out var original) ? original.Name : null;
    }
}
