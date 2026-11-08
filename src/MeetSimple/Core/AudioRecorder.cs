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
    private readonly object gate = new();
    private WasapiCapture? microphoneCapture;
    private WasapiLoopbackCapture? computerCapture;
    private TrackWriter? microphoneWriter;
    private TrackWriter? computerWriter;
    private Stopwatch? clock;
    private float microphoneLevel;
    private float computerLevel;
    private TimeSpan finalDuration;

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
            string? microphonePath = microphoneWriter?.Finish();
            string? computerPath = computerWriter?.Finish();
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
        lock (gate)
        {
            microphoneWriter?.Write(data.Buffer, data.BytesRecorded);
        }
    }

    private void OnComputerData(object? sender, WaveInEventArgs data)
    {
        var format = computerCapture?.WaveFormat;
        if (format != null)
        {
            Volatile.Write(ref computerLevel, AudioLevels.Peak(data.Buffer, data.BytesRecorded, format));
        }
        lock (gate)
        {
            computerWriter?.Write(data.Buffer, data.BytesRecorded);
        }
    }

    public void Dispose()
    {
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
