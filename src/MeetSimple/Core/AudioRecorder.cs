using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetSimple.Core;

public sealed class RecordingResult
{
    public string? MicrophonePath { get; init; }
    public string? ComputerAudioPath { get; init; }
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
                microphoneWriter = new TrackWriter(Path.Combine(folder, "microphone.wav"), microphoneCapture.WaveFormat, clock);
            }
            if (computerCapture != null)
            {
                computerWriter = new TrackWriter(Path.Combine(folder, "computer-audio.wav"), computerCapture.WaveFormat, clock);
            }
            IsRecording = true;
        }
    }

    public RecordingResult StopRecording()
    {
        lock (gate)
        {
            finalDuration = clock?.Elapsed ?? TimeSpan.Zero;
            string? microphonePath = FinishSafely(microphoneWriter);
            string? computerPath = FinishSafely(computerWriter);
            microphoneWriter = null;
            computerWriter = null;
            IsRecording = false;
            clock?.Stop();
            return new RecordingResult
            {
                MicrophonePath = microphonePath,
                ComputerAudioPath = computerPath,
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
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
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

    private static string? FinishSafely(TrackWriter? writer)
    {
        if (writer == null) return null;
        try
        {
            return writer.Finish();
        }
        catch (IOException)
        {
            return writer.FilePath; // Keep whatever reached the disk rather than losing the path.
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
/// Writes one WAV track and keeps it on the shared wall clock: Windows sends no data while nothing is playing,
/// so gaps are filled with silence to keep both tracks the same length and aligned in time.
/// </summary>
internal sealed class TrackWriter : IDisposable
{
    private readonly WaveFileWriter writer;
    private readonly Stopwatch clock;
    private readonly WaveFormat format;
    private long bytesWritten;
    private TimeSpan lastFlush;

    // Flushing refreshes the WAV header, so a crash or power cut loses seconds of audio instead of the whole file.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    public TrackWriter(string filePath, WaveFormat format, Stopwatch clock)
    {
        FilePath = filePath;
        this.format = format;
        this.clock = clock;
        writer = new WaveFileWriter(filePath, format);
    }

    public string FilePath { get; }

    public void Write(byte[] buffer, int count)
    {
        long expectedBytesBeforeChunk = ExpectedBytesNow() - count;
        PadWithSilence(expectedBytesBeforeChunk, minimumGapSeconds: 0.25);
        writer.Write(buffer, 0, count);
        bytesWritten += count;
        if (clock.Elapsed - lastFlush > FlushInterval)
        {
            writer.Flush();
            lastFlush = clock.Elapsed;
        }
    }

    public string Finish()
    {
        PadWithSilence(ExpectedBytesNow(), minimumGapSeconds: 0.0);
        Dispose();
        return FilePath;
    }

    public void Dispose() => writer.Dispose();

    private long ExpectedBytesNow()
    {
        long bytes = (long)(clock.Elapsed.TotalSeconds * format.AverageBytesPerSecond);
        return bytes - (bytes % format.BlockAlign);
    }

    private void PadWithSilence(long targetBytes, double minimumGapSeconds)
    {
        long gap = targetBytes - bytesWritten;
        if (gap <= format.AverageBytesPerSecond * minimumGapSeconds) return;
        gap -= gap % format.BlockAlign;

        var silence = new byte[64 * 1024 - (64 * 1024 % format.BlockAlign)];
        while (gap > 0)
        {
            int chunk = (int)Math.Min(gap, silence.Length);
            writer.Write(silence, 0, chunk);
            bytesWritten += chunk;
            gap -= chunk;
        }
    }
}
