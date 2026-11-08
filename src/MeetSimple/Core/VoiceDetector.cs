using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MeetSimple.Core;

/// <summary>
/// Maps a time in the speech-only audio back to the original recording. The speech-only audio is the recording's
/// speech stretches laid end to end with a short pause between them.
/// </summary>
public sealed class SpeechTimeline
{
    private readonly List<(double SpeechStart, double OriginalStart, double Length)> pieces;

    public SpeechTimeline(List<(double SpeechStart, double OriginalStart, double Length)> pieces) => this.pieces = pieces;

    public TimeSpan ToOriginal(TimeSpan speechTime)
    {
        double seconds = speechTime.TotalSeconds;
        var piece = pieces[0];
        foreach (var candidate in pieces)
        {
            if (candidate.SpeechStart > seconds) break;
            piece = candidate;
        }
        // A time in the pause after a piece belongs to that piece's end.
        double offset = Math.Clamp(seconds - piece.SpeechStart, 0.0, piece.Length);
        return TimeSpan.FromSeconds(piece.OriginalStart + offset);
    }
}

/// <summary>The recording's speech only, ready for Whisper, and how to map its times back.</summary>
public sealed record SpeechOnlyAudio(string WavPath, TimeSpan Duration, SpeechTimeline Timeline);

/// <summary>
/// Finds where people speak with Silero VAD - the voice-activity model faster-whisper, WhisperX and whisper.cpp use -
/// and builds a copy of the recording that holds only those stretches. Whisper then never hears silence (where it
/// invents subtitle credits and repeats itself) or a call app's beeps and chimes (which Silero does not count as voice).
///
/// Silero ships inside the whisper.cpp engine Whisper.net already loads; Whisper.net simply does not expose it, so the
/// engine's own exported functions are called directly. Anything going wrong returns null and the caller transcribes
/// the full recording as before, so this can only improve a transcript, never lose one.
/// </summary>
public static class VoiceDetector
{
    private const int SampleRate = 16000;
    private const int ChunkSamples = SampleRate * 60 * 10; // judged in 10-minute pieces to keep memory flat
    private const double MergeGapSeconds = 0.8;           // pauses shorter than this stay inside one stretch
    private const double PauseBetweenStretchesSeconds = 0.4;

    public static async Task<SpeechOnlyAudio?> ExtractSpeechAsync(
        string modelPath, string wavPath, string speechWavPath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var stretches = FindSpeech(modelPath, wavPath, cancellationToken);
                if (stretches == null) return null;
                return WriteSpeechOnly(wavPath, speechWavPath, stretches, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                AppHealth.LogError(error, "Voice detection (falling back to the full recording)");
                return null;
            }
        }, cancellationToken);
    }

    /// <summary>Speech stretches in seconds, merged and padded; an empty list means no one speaks at all.</summary>
    private static List<(double Start, double End)>? FindSpeech(string modelPath, string wavPath, CancellationToken cancellationToken)
    {
        var engine = NativeVad.TryBind();
        if (engine == null) return null;

        IntPtr context = engine.InitFromFile(modelPath, new VadContextParams { ThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) });
        if (context == IntPtr.Zero) return null;
        var found = new List<(double Start, double End)>();
        try
        {
            var parameters = new VadParams
            {
                Threshold = 0.5f,            // Silero's own recommended default
                MinSpeechDurationMs = 250,   // shorter blips are not words
                MinSilenceDurationMs = 300,
                MaxSpeechDurationSeconds = float.MaxValue,
                SpeechPadMs = 250,           // keep the soft start and end of words
                SamplesOverlap = 0.1f,
            };

            using var reader = new WaveFileReader(wavPath);
            var provider = reader.ToSampleProvider();
            var buffer = new float[ChunkSamples];
            double chunkStartSeconds = 0.0;
            int read;
            while ((read = ReadFully(provider, buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float[] samples = read == buffer.Length ? buffer : buffer[..read];
                IntPtr segments = engine.SegmentsFromSamples(context, parameters, samples, read);
                if (segments == IntPtr.Zero) return null;
                try
                {
                    int count = engine.SegmentCount(segments);
                    for (int index = 0; index < count; index++)
                    {
                        // whisper.cpp reports these in centiseconds.
                        double start = chunkStartSeconds + engine.SegmentStart(segments, index) / 100.0;
                        double end = chunkStartSeconds + engine.SegmentEnd(segments, index) / 100.0;
                        if (end > start) found.Add((start, end));
                    }
                }
                finally
                {
                    engine.FreeSegments(segments);
                }
                chunkStartSeconds += (double)read / SampleRate;
            }
        }
        finally
        {
            engine.Free(context);
        }

        // Merge stretches separated by short pauses so sentences are not cut apart.
        var merged = new List<(double Start, double End)>();
        foreach (var stretch in found.OrderBy(item => item.Start))
        {
            if (merged.Count > 0 && stretch.Start - merged[^1].End < MergeGapSeconds)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, stretch.End));
            }
            else
            {
                merged.Add(stretch);
            }
        }
        return merged;
    }

    private static SpeechOnlyAudio WriteSpeechOnly(
        string wavPath, string speechWavPath, List<(double Start, double End)> stretches, CancellationToken cancellationToken)
    {
        var pieces = new List<(double SpeechStart, double OriginalStart, double Length)>();
        using var reader = new WaveFileReader(wavPath);
        using var writer = new WaveFileWriter(speechWavPath, reader.WaveFormat);
        int blockAlign = reader.WaveFormat.BlockAlign;
        var pause = new byte[(int)(PauseBetweenStretchesSeconds * SampleRate) * blockAlign];
        var copyBuffer = new byte[SampleRate * blockAlign];
        double writtenSeconds = 0.0;

        foreach (var (start, end) in stretches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long startByte = (long)(start * SampleRate) * blockAlign;
            long endByte = Math.Min((long)(end * SampleRate) * blockAlign, reader.Length);
            if (endByte <= startByte) continue;

            if (pieces.Count > 0)
            {
                writer.Write(pause, 0, pause.Length);
                writtenSeconds += PauseBetweenStretchesSeconds;
            }
            pieces.Add((writtenSeconds, start, (double)(endByte - startByte) / blockAlign / SampleRate));

            reader.Position = startByte;
            long remaining = endByte - startByte;
            while (remaining > 0)
            {
                int bytesRead = reader.Read(copyBuffer, 0, (int)Math.Min(copyBuffer.Length, remaining));
                if (bytesRead <= 0) break;
                writer.Write(copyBuffer, 0, bytesRead);
                remaining -= bytesRead;
            }
            writtenSeconds += (double)(endByte - startByte) / blockAlign / SampleRate;
        }

        if (pieces.Count == 0) pieces.Add((0.0, 0.0, 0.0));
        return new SpeechOnlyAudio(speechWavPath, TimeSpan.FromSeconds(writtenSeconds), new SpeechTimeline(pieces));
    }

    private static int ReadFully(ISampleProvider provider, float[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = provider.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    // ----- whisper.cpp's VAD functions, from the engine library Whisper.net already loaded -----

    [StructLayout(LayoutKind.Sequential)]
    private struct VadParams
    {
        public float Threshold;
        public int MinSpeechDurationMs;
        public int MinSilenceDurationMs;
        public float MaxSpeechDurationSeconds;
        public int SpeechPadMs;
        public float SamplesOverlap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VadContextParams
    {
        public int ThreadCount;
        public byte UseGpu; // C bool
        public int GpuDevice;
    }

    private sealed class NativeVad
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr InitFromFileFunction([MarshalAs(UnmanagedType.LPStr)] string modelPath, VadContextParams parameters);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr SegmentsFromSamplesFunction(IntPtr context, VadParams parameters, float[] samples, int sampleCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SegmentCountFunction(IntPtr segments);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate float SegmentTimeFunction(IntPtr segments, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FreeFunction(IntPtr handle);

        public required InitFromFileFunction InitFromFile { get; init; }
        public required SegmentsFromSamplesFunction SegmentsFromSamples { get; init; }
        public required SegmentCountFunction SegmentCount { get; init; }
        public required SegmentTimeFunction SegmentStart { get; init; }
        public required SegmentTimeFunction SegmentEnd { get; init; }
        public required FreeFunction FreeSegments { get; init; }
        public required FreeFunction Free { get; init; }

        /// <summary>The engine library is loaded by Whisper.net when the speech model loads; null if it is not (yet) there.</summary>
        public static NativeVad? TryBind()
        {
            IntPtr library = GetModuleHandleW("whisper.dll");
            if (library == IntPtr.Zero) return null;
            T? Bind<T>(string name) where T : Delegate =>
                NativeLibrary.TryGetExport(library, name, out IntPtr address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

            var initFromFile = Bind<InitFromFileFunction>("whisper_vad_init_from_file_with_params");
            var segmentsFromSamples = Bind<SegmentsFromSamplesFunction>("whisper_vad_segments_from_samples");
            var segmentCount = Bind<SegmentCountFunction>("whisper_vad_segments_n_segments");
            var segmentStart = Bind<SegmentTimeFunction>("whisper_vad_segments_get_segment_t0");
            var segmentEnd = Bind<SegmentTimeFunction>("whisper_vad_segments_get_segment_t1");
            var freeSegments = Bind<FreeFunction>("whisper_vad_free_segments");
            var free = Bind<FreeFunction>("whisper_vad_free");
            if (initFromFile == null || segmentsFromSamples == null || segmentCount == null || segmentStart == null
                || segmentEnd == null || freeSegments == null || free == null)
            {
                return null;
            }
            return new NativeVad
            {
                InitFromFile = initFromFile,
                SegmentsFromSamples = segmentsFromSamples,
                SegmentCount = segmentCount,
                SegmentStart = segmentStart,
                SegmentEnd = segmentEnd,
                FreeSegments = freeSegments,
                Free = free,
            };
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
    }
}
