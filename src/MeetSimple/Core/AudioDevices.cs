using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MeetSimple.Core;

/// <summary>A Windows audio device: its endpoint id and the name Windows shows for it.</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>The microphones and outputs that are plugged in and enabled.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> List(bool microphones)
    {
        var devices = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(microphones ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDevice(device.ID, device.FriendlyName));
                }
            }
        }
        catch (COMException)
        {
            // The audio service is restarting; an empty list just hides the choice for now.
        }
        return devices;
    }

    /// <summary>The Windows default device's id (the one AudioRecorder records when none is chosen), or null.</summary>
    public static string? DefaultId(bool microphones)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(microphones ? DataFlow.Capture : DataFlow.Render,
                                                                  microphones ? Role.Console : Role.Multimedia);
            return device.ID;
        }
        catch (COMException)
        {
            return null; // No such device at all.
        }
    }
}

/// <summary>
/// Notices when the sound is somewhere other than where it is being recorded: Discord playing to an audio interface
/// while the default speakers are recorded, or a call using a headset microphone. Windows keeps a level meter on
/// every device, so this only reads them. Call <see cref="Sample"/> a few times a second.
/// </summary>
public sealed class AudioRouteAdvisor : IDisposable
{
    /// <summary>Below this the recorded device counts as silent (a working microphone always hears a little hiss).</summary>
    private const float SilentPeak = 0.002f;

    /// <summary>Above this another device counts as having real sound (speech or a call), not hiss.</summary>
    private const float SoundPeak = 0.03f;

    /// <summary>Samples looked back over (5 s at 5 a second) and how many must have sound elsewhere (1.6 s).</summary>
    private const int WindowSamples = 25;
    private const int SoundSamplesNeeded = 8;
    private const int RefreshDevicesEverySamples = 25;

    private readonly bool microphones;
    private readonly HashSet<string> dismissed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<bool>> soundHistory = new(StringComparer.Ordinal);
    private MMDeviceEnumerator? enumerator;
    private List<MMDevice> devices = new();
    private int samplesUntilRefresh;
    private int silentSamples;
    private string? lastRecordedId;

    public AudioRouteAdvisor(bool microphones)
    {
        this.microphones = microphones;
    }

    /// <summary>Never suggest this device again (the user said no).</summary>
    public void Dismiss(string deviceId) => dismissed.Add(deviceId);

    /// <summary>
    /// Reads every device's level once. Returns a device that has had sound for a while when the recorded one has
    /// been silent all along; null otherwise.
    /// </summary>
    public AudioDevice? Sample(string? recordedDeviceId)
    {
        try
        {
            if (recordedDeviceId != lastRecordedId)
            {
                lastRecordedId = recordedDeviceId;
                silentSamples = 0;
                soundHistory.Clear();
            }
            if (samplesUntilRefresh-- <= 0) RefreshDevices();

            AudioDevice? suggestion = null;
            int bestSoundSamples = 0;
            bool recordedIsSilent = true;
            foreach (var device in devices)
            {
                float peak = PeakOf(device);
                if (device.ID == recordedDeviceId)
                {
                    recordedIsSilent = peak < SilentPeak;
                    continue;
                }
                if (!soundHistory.TryGetValue(device.ID, out var history))
                {
                    soundHistory[device.ID] = history = new Queue<bool>();
                }
                history.Enqueue(peak >= SoundPeak);
                if (history.Count > WindowSamples) history.Dequeue();

                int soundSamples = history.Count(hadSound => hadSound);
                if (soundSamples >= SoundSamplesNeeded && soundSamples > bestSoundSamples && !dismissed.Contains(device.ID))
                {
                    bestSoundSamples = soundSamples;
                    suggestion = new AudioDevice(device.ID, device.FriendlyName);
                }
            }

            silentSamples = recordedIsSilent ? silentSamples + 1 : 0;
            return silentSamples >= WindowSamples ? suggestion : null;
        }
        catch (COMException)
        {
            RefreshDevices();
            return null;
        }
    }

    private static float PeakOf(MMDevice device)
    {
        try
        {
            return device.AudioMeterInformation.MasterPeakValue;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return 0f; // Unplugged a moment ago; the next refresh drops it.
        }
    }

    private void RefreshDevices()
    {
        samplesUntilRefresh = RefreshDevicesEverySamples;
        DisposeDevices();
        try
        {
            enumerator ??= new MMDeviceEnumerator();
            devices = enumerator.EnumerateAudioEndPoints(microphones ? DataFlow.Capture : DataFlow.Render, DeviceState.Active).ToList();
        }
        catch (COMException)
        {
            devices = new List<MMDevice>();
        }
        var present = devices.Select(device => device.ID).ToHashSet(StringComparer.Ordinal);
        foreach (string gone in soundHistory.Keys.Where(deviceId => !present.Contains(deviceId)).ToList())
        {
            soundHistory.Remove(gone);
        }
    }

    private void DisposeDevices()
    {
        foreach (var device in devices) device.Dispose();
        devices = new List<MMDevice>();
    }

    public void Dispose()
    {
        DisposeDevices();
        enumerator?.Dispose();
        enumerator = null;
    }
}
