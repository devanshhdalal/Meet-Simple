using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetSimple.Core;

public sealed record PreparedAudio(string WavPath, TimeSpan Duration, float Peak);

/// <summary>Turns any audio/video file (MP4, MOV, MP3, WAV...) into the 16 kHz mono WAV that Whisper needs.</summary>
public static class AudioPreparer
{
    private const int WhisperSampleRate = 16000;

    public static Task<PreparedAudio> PrepareAsync(
        string sourcePath, string wavOutputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => Prepare(sourcePath, wavOutputPath, progress, cancellationToken), cancellationToken);
    }

    private static PreparedAudio Prepare(
        string sourcePath, string wavOutputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(sourcePath);
        ISampleProvider monoProvider = new MonoDownmixSampleProvider(reader);
        ISampleProvider whisperProvider = reader.WaveFormat.SampleRate == WhisperSampleRate
            ? monoProvider
            : new WdlResamplingSampleProvider(monoProvider, WhisperSampleRate);

        using var writer = new WaveFileWriter(wavOutputPath, new WaveFormat(WhisperSampleRate, 16, 1));
        var floatBuffer = new float[WhisperSampleRate * 4];
        var shortBuffer = new short[floatBuffer.Length];
        float peak = 0f;
        long totalSamples = 0;
        double totalSeconds = Math.Max(reader.TotalTime.TotalSeconds, 1.0);

        int samplesRead;
        while ((samplesRead = whisperProvider.Read(floatBuffer, 0, floatBuffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = 0; index < samplesRead; index++)
            {
                float sample = floatBuffer[index];
                float magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
                shortBuffer[index] = (short)Math.Clamp(sample * 32767f, -32768f, 32767f);
            }
            writer.WriteSamples(shortBuffer, 0, samplesRead);
            totalSamples += samplesRead;
            progress?.Report(Math.Min(reader.CurrentTime.TotalSeconds / totalSeconds, 1.0));
        }

        if (totalSamples == 0)
        {
            throw new InvalidOperationException("No audio could be read from this file.");
        }
        return new PreparedAudio(wavOutputPath, TimeSpan.FromSeconds((double)totalSamples / WhisperSampleRate), peak);
    }

    /// <summary>Averages all channels into one.</summary>
    private sealed class MonoDownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int channelCount;
        private float[] scratch = Array.Empty<float>();

        public MonoDownmixSampleProvider(ISampleProvider source)
        {
            this.source = source;
            channelCount = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesNeeded = count * channelCount;
            if (scratch.Length < samplesNeeded) scratch = new float[samplesNeeded];
            int samplesRead = source.Read(scratch, 0, samplesNeeded);
            int frames = samplesRead / channelCount;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0f;
                for (int channel = 0; channel < channelCount; channel++)
                {
                    sum += scratch[frame * channelCount + channel];
                }
                buffer[offset + frame] = sum / channelCount;
            }
            return frames;
        }
    }
}
