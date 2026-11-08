using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetSimple.Core;

public sealed class RecordingResult
{
    /// <summary>One or more part files; a new part starts long before a WAV file's 4 GB limit.</summary>
    public IReadOnlyList<string>? MicrophonePaths { get; init; }
    public IReadOnlyList<string>? ComputerAudioPaths { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Captures the microphone ("You") and the computer's output sound ("Others") as two separate tracks.
/// Monitoring (level meters only) can run without recording. Either device can be switched while recording: the
/// track carries on in the same file, so a call that turns out to play on a headset is fixed with one click.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const long MinimumFreeBytesToRecord = 2L * 1024 * 1024 * 1024;

    private readonly object gate = new();
    private string? microphoneDeviceId;
    private string? speakerDeviceId;
    private MMDeviceEnumerator? deviceEnumerator;
    private MMDevice? microphoneDevice;
    private MMDevice? speakerDevice;
    private WasapiCapture? microphoneCapture;
    private WasapiLoopbackCapture? computerCapture;
    private TrackWriter? microphoneWriter;
    private TrackWriter? computerWriter;
    private string? recordingFolder;
    private Stopwatch? clock;
    private float microphoneLevel;
    private float computerLevel;
    private float microphonePeakSinceRead;
    private float computerPeakSinceRead;
    private TimeSpan finalDuration;
    private bool isDisposed;

    /// <summary>Raised (possibly on a background thread) when something stops working while recording, with a message for the user.</summary>
    public event Action<string>? DeviceProblem;

    public string? MicrophoneError { get; private set; }
    public string? ComputerAudioError { get; private set; }
    public bool IsRecording { get; private set; }
    public float MicrophoneLevel => Volatile.Read(ref microphoneLevel);
    public float ComputerAudioLevel => Volatile.Read(ref computerLevel);
    public TimeSpan Elapsed => IsRecording && clock != null ? clock.Elapsed : finalDuration;

    /// <summary>The microphone being recorded (null if none could be opened).</summary>
    public AudioDevice? Microphone { get; private set; }

    /// <summary>The output whose sound is being recorded (null if none could be opened).</summary>
    public AudioDevice? ComputerAudio { get; private set; }

    /// <param name="microphoneDeviceId">Record this microphone instead of the Windows default (for a call: the one the call app uses).</param>
    /// <param name="speakerDeviceId">Record what plays on this output instead of the default (call apps often use the headset).</param>
    public AudioRecorder(string? microphoneDeviceId = null, string? speakerDeviceId = null)
    {
        this.microphoneDeviceId = microphoneDeviceId;
        this.speakerDeviceId = speakerDeviceId;
    }

    /// <summary>Highest microphone level since the last call (0..1), so a meter drawn at 60 fps misses no peak.</summary>
    public float TakeMicrophonePeak() => Interlocked.Exchange(ref microphonePeakSinceRead, 0f);

    /// <summary>Highest computer-audio level since the last call (0..1).</summary>
    public float TakeComputerAudioPeak() => Interlocked.Exchange(ref computerPeakSinceRead, 0f);

    public void StartMonitoring()
    {
        if (microphoneCapture == null)
        {
            try
            {
                OpenMicrophone();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                MicrophoneError = error.Message;
            }
        }

        if (computerCapture == null)
        {
            try
            {
                OpenComputerAudio();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ComputerAudioError = error.Message;
            }
        }
    }

    private void OpenMicrophone()
    {
        var device = OpenActiveDevice(microphoneDeviceId) ?? DefaultDevice(DataFlow.Capture, Role.Console);
        WasapiCapture capture;
        try
        {
            capture = new WasapiCapture(device);
        }
        catch
        {
            device.Dispose();
            throw;
        }
        capture.DataAvailable += (_, data) => OnMicrophoneData(capture, data);
        capture.RecordingStopped += (_, stopped) =>
        {
            if (ReferenceEquals(capture, microphoneCapture)) OnCaptureStopped(stopped.Exception, isMicrophone: true);
        };
        lock (gate)
        {
            microphoneCapture = capture;
            microphoneDevice = device;
            microphoneWriter?.ChangeInput(capture.WaveFormat);
            // A microphone found after the recording began still joins it, in step with the other track.
            if (IsRecording && microphoneWriter == null && recordingFolder != null && clock != null)
            {
                microphoneWriter = new TrackWriter(recordingFolder, "microphone", capture.WaveFormat, clock);
            }
        }
        Microphone = new AudioDevice(device.ID, device.FriendlyName);
        MicrophoneError = null;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(microphoneCapture, capture)) microphoneCapture = null;
                if (ReferenceEquals(microphoneDevice, device)) microphoneDevice = null;
            }
            Microphone = null;
            StopQuietly(capture);
            device.Dispose();
            throw;
        }
    }

    private void OpenComputerAudio()
    {
        var device = OpenActiveDevice(speakerDeviceId) ?? DefaultDevice(DataFlow.Render, Role.Multimedia);
        WasapiLoopbackCapture capture;
        try
        {
            capture = new WasapiLoopbackCapture(device);
        }
        catch
        {
            device.Dispose();
            throw;
        }
        capture.DataAvailable += (_, data) => OnComputerData(capture, data);
        capture.RecordingStopped += (_, stopped) =>
        {
            if (ReferenceEquals(capture, computerCapture)) OnCaptureStopped(stopped.Exception, isMicrophone: false);
        };
        lock (gate)
        {
            computerCapture = capture;
            speakerDevice = device;
            computerWriter?.ChangeInput(capture.WaveFormat);
            if (IsRecording && computerWriter == null && recordingFolder != null && clock != null)
            {
                computerWriter = new TrackWriter(recordingFolder, "computer-audio", capture.WaveFormat, clock);
            }
        }
        ComputerAudio = new AudioDevice(device.ID, device.FriendlyName);
        ComputerAudioError = null;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(computerCapture, capture)) computerCapture = null;
                if (ReferenceEquals(speakerDevice, device)) speakerDevice = null;
            }
            ComputerAudio = null;
            StopQuietly(capture);
            device.Dispose();
            throw;
        }
    }

    /// <summary>Records another microphone from now on; while recording, the "You" track continues in the same file.</summary>
    public void SwitchMicrophone(string deviceId)
    {
        if (Microphone?.Id == deviceId && microphoneCapture != null) return;
        microphoneDeviceId = deviceId;
        WasapiCapture? oldCapture;
        MMDevice? oldDevice;
        lock (gate)
        {
            oldCapture = microphoneCapture;
            oldDevice = microphoneDevice;
            microphoneCapture = null; // Late buffers from the old device are ignored from here on.
            microphoneDevice = null;
        }
        StopQuietly(oldCapture);
        oldDevice?.Dispose();
        Volatile.Write(ref microphoneLevel, 0f);
        try
        {
            OpenMicrophone();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MicrophoneError = error.Message;
            Microphone = null;
            DeviceProblem?.Invoke($"That microphone could not be opened ({error.Message}). Pick another one.");
        }
    }

    /// <summary>Records another output from now on; while recording, the "Others" track continues in the same file.</summary>
    public void SwitchComputerAudio(string deviceId)
    {
        if (ComputerAudio?.Id == deviceId && computerCapture != null) return;
        speakerDeviceId = deviceId;
        WasapiLoopbackCapture? oldCapture;
        MMDevice? oldDevice;
        lock (gate)
        {
            oldCapture = computerCapture;
            oldDevice = speakerDevice;
            computerCapture = null;
            speakerDevice = null;
        }
        StopQuietly(oldCapture);
        oldDevice?.Dispose();
        Volatile.Write(ref computerLevel, 0f);
        try
        {
            OpenComputerAudio();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ComputerAudioError = error.Message;
            ComputerAudio = null;
            DeviceProblem?.Invoke($"That output could not be recorded ({error.Message}). Pick another one.");
        }
    }

    private static void StopQuietly(WasapiCapture? capture)
    {
        if (capture == null) return;
        try
        {
            capture.StopRecording();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Already stopped or the device is gone; disposing is all that is left.
        }
        try
        {
            capture.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Nothing more can be done with a capture that fails to close.
        }
    }

    /// <summary>The requested device if it is still connected and active; null means "use the Windows default".</summary>
    private MMDevice? OpenActiveDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        try
        {
            deviceEnumerator ??= new MMDeviceEnumerator();
            var device = deviceEnumerator.GetDevice(deviceId);
            if (device.State == DeviceState.Active) return device;
            device.Dispose();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Unplugged since the call started; the default device is the best remaining choice.
        }
        return null;
    }

    /// <summary>The Windows default device, the same one NAudio would pick (throws when there is none).</summary>
    private MMDevice DefaultDevice(DataFlow flow, Role role)
    {
        deviceEnumerator ??= new MMDeviceEnumerator();
        return deviceEnumerator.GetDefaultAudioEndpoint(flow, role);
    }

    public void StartRecording(string folder)
    {
        if (IsRecording) return;
        Directory.CreateDirectory(folder);
        AppHealth.EnsureFreeSpace(folder, MinimumFreeBytesToRecord);
        StartMonitoring();

        lock (gate)
        {
            recordingFolder = folder;
            clock = Stopwatch.StartNew();
            if (microphoneCapture != null)
            {
                microphoneWriter = new TrackWriter(folder, "microphone", microphoneCapture.WaveFormat, clock);
            }
            if (computerCapture != null)
            {
                computerWriter = new TrackWriter(folder, "computer-audio", computerCapture.WaveFormat, clock);
            }
            IsRecording = true;
        }
    }

    public RecordingResult StopRecording()
    {
        lock (gate)
        {
            finalDuration = clock?.Elapsed ?? TimeSpan.Zero;
            var microphonePaths = FinishSafely(microphoneWriter);
            var computerPaths = FinishSafely(computerWriter);
            microphoneWriter = null;
            computerWriter = null;
            recordingFolder = null;
            IsRecording = false;
            clock?.Stop();
            return new RecordingResult
            {
                MicrophonePaths = microphonePaths,
                ComputerAudioPaths = computerPaths,
                Duration = finalDuration,
            };
        }
    }

    private void OnMicrophoneData(WasapiCapture source, WaveInEventArgs data)
    {
        if (!ReferenceEquals(source, microphoneCapture)) return;
        float peak = AudioLevels.Peak(data.Buffer, data.BytesRecorded, source.WaveFormat);
        Volatile.Write(ref microphoneLevel, peak);
        RaisePeak(ref microphonePeakSinceRead, peak);
        WriteSafely(source, data, isMicrophone: true);
    }

    private void OnComputerData(WasapiCapture source, WaveInEventArgs data)
    {
        if (!ReferenceEquals(source, computerCapture)) return;
        float peak = AudioLevels.Peak(data.Buffer, data.BytesRecorded, source.WaveFormat);
        Volatile.Write(ref computerLevel, peak);
        RaisePeak(ref computerPeakSinceRead, peak);
        WriteSafely(source, data, isMicrophone: false);
    }

    private static void RaisePeak(ref float peakSinceRead, float peak)
    {
        float seen = Volatile.Read(ref peakSinceRead);
        while (peak > seen)
        {
            float before = Interlocked.CompareExchange(ref peakSinceRead, peak, seen);
            if (before == seen) break;
            seen = before;
        }
    }

    /// <summary>
    /// Runs on the capture thread, where an exception would close the whole app and lose the meeting.
    /// A failing track is closed and reported; the other track keeps recording.
    /// </summary>
    private void WriteSafely(WasapiCapture source, WaveInEventArgs data, bool isMicrophone)
    {
        string? problem = null;
        lock (gate)
        {
            // A buffer from a device that was just switched away from belongs to neither track.
            if (!ReferenceEquals(source, isMicrophone ? microphoneCapture : computerCapture)) return;
            var writer = isMicrophone ? microphoneWriter : computerWriter;
            if (writer == null) return;
            try
            {
                writer.Write(data.Buffer, data.BytesRecorded);
            }
            // ArgumentException is NAudio's "WAV file too large"; parts make it unreachable, but it must never end the app.
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException or ArgumentException)
            {
                try
                {
                    writer.Dispose();
                }
                catch (Exception disposeError) when (disposeError is IOException or ObjectDisposedException)
                {
                    // The file is already unusable; nothing more to do.
                }
                if (isMicrophone) microphoneWriter = null; else computerWriter = null;
                string trackName = isMicrophone ? "microphone" : "computer sound";
                problem = $"Saving the {trackName} failed ({error.Message}). Is the drive full? "
                          + "That part of the recording stopped; the rest continues.";
            }
        }
        if (problem != null) DeviceProblem?.Invoke(problem);
    }

    private void OnCaptureStopped(Exception? error, bool isMicrophone)
    {
        // A normal stop has no exception; an exception means the device went away (unplugged, disabled, driver reset).
        if (error == null || isDisposed) return;
        string deviceName = isMicrophone ? "microphone" : "computer sound";
        if (isMicrophone)
        {
            MicrophoneError = error.Message;
            Volatile.Write(ref microphoneLevel, 0f);
        }
        else
        {
            ComputerAudioError = error.Message;
            Volatile.Write(ref computerLevel, 0f);
        }
        DeviceProblem?.Invoke($"The {deviceName} stopped working ({error.Message}). It may have been unplugged or switched off. "
                              + "Pick another one; anything else keeps recording.");
    }

    private static IReadOnlyList<string>? FinishSafely(TrackWriter? writer)
    {
        if (writer == null) return null;
        try
        {
            return writer.Finish();
        }
        catch (Exception error) when (error is IOException or ArgumentException or ObjectDisposedException)
        {
            return writer.Paths; // Keep whatever reached the disk rather than losing the files.
        }
    }

    public void Dispose()
    {
        isDisposed = true;
        if (IsRecording)
        {
            StopRecording();
        }
        WasapiCapture? oldMicrophone;
        WasapiCapture? oldComputer;
        lock (gate)
        {
            oldMicrophone = microphoneCapture;
            oldComputer = computerCapture;
            microphoneCapture = null;
            computerCapture = null;
        }
        StopQuietly(oldMicrophone);
        StopQuietly(oldComputer);
        microphoneDevice?.Dispose();
        speakerDevice?.Dispose();
        deviceEnumerator?.Dispose();
        microphoneDevice = null;
        speakerDevice = null;
        deviceEnumerator = null;
    }
}

internal static class AudioLevels
{
    /// <summary>Peak sample magnitude (0..1) in a captured buffer. Windows shared mode delivers 32-bit float or 16-bit PCM.</summary>
    public static float Peak(byte[] buffer, int byteCount, WaveFormat format)
    {
        float peak = 0f;
        if (format.BitsPerSample == 32)
        {
            for (int offset = 0; offset + 4 <= byteCount; offset += 4)
            {
                float magnitude = Math.Abs(BitConverter.ToSingle(buffer, offset));
                if (magnitude > peak) peak = magnitude;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int offset = 0; offset + 2 <= byteCount; offset += 2)
            {
                float magnitude = Math.Abs(BitConverter.ToInt16(buffer, offset) / 32768f);
                if (magnitude > peak) peak = magnitude;
            }
        }
        return Math.Min(peak, 1f);
    }
}

/// <summary>
/// Writes one track and keeps it on the shared wall clock: Windows sends no data while nothing is playing, so gaps
/// are filled with silence to keep both tracks the same length and aligned in time.
/// Audio is stored as 16-bit mono at the first device's rate (speech needs no more), which is up to 8x smaller than
/// the 32-bit float stereo Windows delivers, and a new part file is started at 3 GB so a WAV file never hits 4 GB.
/// A device switched to mid-recording that runs at another rate is resampled to the track's rate.
/// </summary>
internal sealed class TrackWriter : IDisposable
{
    internal const long MaxPartBytes = 3L * 1024 * 1024 * 1024;
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private static readonly byte[] Silence = new byte[64 * 1024];

    private readonly string folder;
    private readonly string stem;
    private const int ResampleChunk = 4096;

    private WaveFormat inputFormat;
    private readonly WaveFormat outputFormat;
    private bool inputIsFloat;
    private readonly Stopwatch clock;
    private readonly List<string> paths = new();
    private WaveFileWriter writer;
    private long totalBytesWritten;
    private long partBytesWritten;
    private TimeSpan lastFlush;
    private byte[] converted = Array.Empty<byte>();
    private float[] monoSamples = Array.Empty<float>();
    private BufferedWaveProvider? resampleInput;
    private WdlResamplingSampleProvider? resampler;
    private byte[] resampleBytes = Array.Empty<byte>();
    private float[] resampledSamples = Array.Empty<float>();

    public TrackWriter(string folder, string stem, WaveFormat inputFormat, Stopwatch clock)
    {
        this.folder = folder;
        this.stem = stem;
        this.inputFormat = inputFormat;
        this.clock = clock;
        outputFormat = new WaveFormat(inputFormat.SampleRate, 16, 1);
        inputIsFloat = IsFloat(inputFormat);
        writer = OpenPart(1);
    }

    private static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);

    /// <summary>The device behind this track changed: read its format from now on, resampling if its rate differs.</summary>
    public void ChangeInput(WaveFormat newFormat)
    {
        inputFormat = newFormat;
        inputIsFloat = IsFloat(newFormat);
        if (newFormat.SampleRate == outputFormat.SampleRate)
        {
            resampleInput = null;
            resampler = null;
            return;
        }
        resampleInput = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(newFormat.SampleRate, 1))
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5),
        };
        resampler = new WdlResamplingSampleProvider(resampleInput.ToSampleProvider(), outputFormat.SampleRate);
    }

    public IReadOnlyList<string> Paths => paths;

    public void Write(byte[] buffer, int count)
    {
        int convertedBytes = ConvertToMono16(buffer, count);
        PadWithSilence(ExpectedBytesNow() - convertedBytes, minimumGapSeconds: 0.25);
        WriteBytes(converted, convertedBytes);

        // Flushing rewrites the WAV header, so a crash or power cut loses seconds of audio instead of the whole file.
        if (clock.Elapsed - lastFlush > FlushInterval)
        {
            writer.Flush();
            lastFlush = clock.Elapsed;
        }
    }

    public IReadOnlyList<string> Finish()
    {
        PadWithSilence(ExpectedBytesNow(), minimumGapSeconds: 0.0);
        Dispose();
        return paths;
    }

    public void Dispose() => writer.Dispose();

    private WaveFileWriter OpenPart(int partNumber)
    {
        string path = RecordingRecovery.PartPath(folder, stem, partNumber);
        paths.Add(path);
        partBytesWritten = 0;
        return new WaveFileWriter(path, outputFormat);
    }

    private void WriteBytes(byte[] data, int count)
    {
        int offset = 0;
        while (count > 0)
        {
            if (partBytesWritten >= MaxPartBytes)
            {
                writer.Dispose();
                writer = OpenPart(paths.Count + 1);
            }
            int chunk = (int)Math.Min(count, MaxPartBytes - partBytesWritten);
            writer.Write(data, offset, chunk);
            offset += chunk;
            count -= chunk;
            partBytesWritten += chunk;
            totalBytesWritten += chunk;
        }
    }

    /// <summary>Averages the channels of each frame into one 16-bit sample. Returns the number of bytes produced.</summary>
    private int ConvertToMono16(byte[] buffer, int count)
    {
        int frameCount = MixToMono(buffer, count);
        float[] samples = monoSamples;
        int sampleCount = frameCount;
        if (resampler != null)
        {
            sampleCount = Resample(frameCount);
            samples = resampledSamples;
        }

        int neededBytes = sampleCount * 2;
        if (converted.Length < neededBytes) converted = new byte[neededBytes];
        for (int index = 0; index < sampleCount; index++)
        {
            short value = (short)Math.Clamp(samples[index] * 32767f, -32768f, 32767f);
            converted[index * 2] = (byte)value;
            converted[index * 2 + 1] = (byte)(value >> 8);
        }
        return neededBytes;
    }

    /// <summary>Fills <see cref="monoSamples"/> with one sample per frame. Returns the number of frames.</summary>
    private int MixToMono(byte[] buffer, int count)
    {
        int channelCount = Math.Max(1, inputFormat.Channels);
        int bytesPerSample = inputFormat.BitsPerSample / 8;
        if (bytesPerSample == 0) return 0;
        int frameBytes = bytesPerSample * channelCount;
        int frameCount = count / frameBytes;
        if (monoSamples.Length < frameCount) monoSamples = new float[frameCount];

        for (int frame = 0; frame < frameCount; frame++)
        {
            float sum = 0f;
            for (int channel = 0; channel < channelCount; channel++)
            {
                sum += ReadSample(buffer, frame * frameBytes + channel * bytesPerSample, bytesPerSample);
            }
            monoSamples[frame] = sum / channelCount;
        }
        return frameCount;
    }

    /// <summary>Feeds the mono samples through the resampler into <see cref="resampledSamples"/>. Returns how many came out.</summary>
    private int Resample(int frameCount)
    {
        int byteCount = frameCount * sizeof(float);
        if (resampleBytes.Length < byteCount) resampleBytes = new byte[byteCount];
        Buffer.BlockCopy(monoSamples, 0, resampleBytes, 0, byteCount);
        resampleInput!.AddSamples(resampleBytes, 0, byteCount);

        int produced = 0;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            if (resampledSamples.Length < produced + ResampleChunk) Array.Resize(ref resampledSamples, produced + ResampleChunk);
            int read = resampler!.Read(resampledSamples, produced, ResampleChunk);
            if (read <= 0) break;
            produced += read;
        }
        return produced;
    }

    private float ReadSample(byte[] buffer, int offset, int bytesPerSample) => bytesPerSample switch
    {
        4 when inputIsFloat => BitConverter.ToSingle(buffer, offset),
        4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
        3 => (((buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16)) << 8) >> 8) / 8388608f,
        2 => BitConverter.ToInt16(buffer, offset) / 32768f,
        1 => (buffer[offset] - 128) / 128f,
        _ => 0f,
    };

    private long ExpectedBytesNow()
    {
        long bytes = (long)(clock.Elapsed.TotalSeconds * outputFormat.AverageBytesPerSecond);
        return bytes - (bytes % outputFormat.BlockAlign);
    }

    private void PadWithSilence(long targetBytes, double minimumGapSeconds)
    {
        long gap = targetBytes - totalBytesWritten;
        if (gap <= outputFormat.AverageBytesPerSecond * minimumGapSeconds) return;
        gap -= gap % outputFormat.BlockAlign;
        while (gap > 0)
        {
            int chunk = (int)Math.Min(gap, Silence.Length);
            WriteBytes(Silence, chunk);
            gap -= chunk;
        }
    }
}
