using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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
/// Monitoring (level meters only) can run without recording.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const long MinimumFreeBytesToRecord = 2L * 1024 * 1024 * 1024;

    private readonly object gate = new();
    private WasapiCapture? microphoneCapture;
    private WasapiLoopbackCapture? computerCapture;
    private TrackWriter? microphoneWriter;
    private TrackWriter? computerWriter;
    private Stopwatch? clock;
    private float microphoneLevel;
    private float computerLevel;
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

    public void StartMonitoring()
    {
        if (microphoneCapture != null || computerCapture != null) return;

        try
        {
            var capture = new WasapiCapture();
            capture.DataAvailable += OnMicrophoneData;
            capture.RecordingStopped += (_, stopped) => OnCaptureStopped(stopped.Exception, isMicrophone: true);
            capture.StartRecording();
            microphoneCapture = capture;
        }
        catch (Exception error)
        {
            MicrophoneError = error.Message;
        }

        try
        {
            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnComputerData;
            capture.RecordingStopped += (_, stopped) => OnCaptureStopped(stopped.Exception, isMicrophone: false);
            capture.StartRecording();
            computerCapture = capture;
        }
        catch (Exception error)
        {
            ComputerAudioError = error.Message;
        }
    }

    public void StartRecording(string folder)
    {
        if (IsRecording) return;
        Directory.CreateDirectory(folder);
        AppHealth.EnsureFreeSpace(folder, MinimumFreeBytesToRecord);
        StartMonitoring();

        lock (gate)
        {
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

    private void OnMicrophoneData(object? sender, WaveInEventArgs data)
    {
        var format = microphoneCapture?.WaveFormat;
        if (format != null)
        {
            Volatile.Write(ref microphoneLevel, AudioLevels.Peak(data.Buffer, data.BytesRecorded, format));
        }
        WriteSafely(ref microphoneWriter, data, "microphone");
    }

    private void OnComputerData(object? sender, WaveInEventArgs data)
    {
        var format = computerCapture?.WaveFormat;
        if (format != null)
        {
            Volatile.Write(ref computerLevel, AudioLevels.Peak(data.Buffer, data.BytesRecorded, format));
        }
        WriteSafely(ref computerWriter, data, "computer sound");
    }

    /// <summary>
    /// Runs on the capture thread, where an exception would close the whole app and lose the meeting.
    /// A failing track is closed and reported; the other track keeps recording.
    /// </summary>
    private void WriteSafely(ref TrackWriter? writer, WaveInEventArgs data, string trackName)
    {
        string? problem = null;
        lock (gate)
        {
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
                writer = null;
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
                              + "Anything else keeps recording.");
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
        try { microphoneCapture?.StopRecording(); } catch (Exception) { }
        try { computerCapture?.StopRecording(); } catch (Exception) { }
        microphoneCapture?.Dispose();
        computerCapture?.Dispose();
        microphoneCapture = null;
        computerCapture = null;
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
/// Audio is stored as 16-bit mono at the device's own rate (speech needs no more), which is up to 8x smaller than
/// the 32-bit float stereo Windows delivers, and a new part file is started at 3 GB so a WAV file never hits 4 GB.
/// </summary>
internal sealed class TrackWriter : IDisposable
{
    internal const long MaxPartBytes = 3L * 1024 * 1024 * 1024;
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private static readonly byte[] Silence = new byte[64 * 1024];

    private readonly string folder;
    private readonly string stem;
    private readonly WaveFormat inputFormat;
    private readonly WaveFormat outputFormat;
    private readonly bool inputIsFloat;
    private readonly Stopwatch clock;
    private readonly List<string> paths = new();
    private WaveFileWriter writer;
    private long totalBytesWritten;
    private long partBytesWritten;
    private TimeSpan lastFlush;
    private byte[] converted = Array.Empty<byte>();

    public TrackWriter(string folder, string stem, WaveFormat inputFormat, Stopwatch clock)
    {
        this.folder = folder;
        this.stem = stem;
        this.inputFormat = inputFormat;
        this.clock = clock;
        outputFormat = new WaveFormat(inputFormat.SampleRate, 16, 1);
        inputIsFloat = inputFormat.Encoding == WaveFormatEncoding.IeeeFloat
                       || (inputFormat is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);
        writer = OpenPart(1);
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
        int channelCount = Math.Max(1, inputFormat.Channels);
        int bytesPerSample = inputFormat.BitsPerSample / 8;
        if (bytesPerSample == 0) return 0;
        int frameBytes = bytesPerSample * channelCount;
        int frameCount = count / frameBytes;
        int neededBytes = frameCount * 2;
        if (converted.Length < neededBytes) converted = new byte[neededBytes];

        for (int frame = 0; frame < frameCount; frame++)
        {
            float sum = 0f;
            for (int channel = 0; channel < channelCount; channel++)
            {
                sum += ReadSample(buffer, frame * frameBytes + channel * bytesPerSample, bytesPerSample);
            }
            short value = (short)Math.Clamp(sum / channelCount * 32767f, -32768f, 32767f);
            converted[frame * 2] = (byte)value;
            converted[frame * 2 + 1] = (byte)(value >> 8);
        }
        return neededBytes;
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
