namespace MeetSimple.Core;

/// <summary>
/// Where a track actually has a voice, judged from its loudness in 30 ms slices against the track's own background
/// noise. Whisper writes text for silence and noise too ("Closed Captioning by ...", sentences repeated from earlier),
/// because its training subtitles have credits over silent stretches; any passage with almost no voice under it is
/// that kind of invention. Measured from the audio itself, so it works for every language and every phrase.
/// </summary>
public sealed class SpeechMap
{
    public const int FrameSamples = 480; // 30 ms at 16 kHz
    private const double FrameSeconds = 0.03;

    /// <summary>Speech must stand this far above the track's background noise.</summary>
    private const double AboveNoiseDecibels = 12.0;

    /// <summary>Never call anything quieter than this a voice, even on a perfectly silent track...</summary>
    private const double QuietestVoiceDecibels = -50.0;

    /// <summary>...and never demand more than normal speech level, even on a noisy one.</summary>
    private const double LoudestThresholdDecibels = -28.0;

    /// <summary>Speech has short gaps between words and Whisper's timestamps are rough, so voice is spread by 150 ms.</summary>
    private const int SpreadFrames = 5;

    private readonly bool[] voiced;
    private readonly bool[] loud; // above the threshold itself, before spreading

    public SpeechMap(IReadOnlyList<float> frameDecibels)
    {
        voiced = new bool[frameDecibels.Count];
        loud = new bool[frameDecibels.Count];
        if (frameDecibels.Count == 0) return;

        // The quietest tenth of a recording is its background: room tone, line noise, or digital silence.
        var sorted = frameDecibels.ToArray();
        Array.Sort(sorted);
        double noiseFloor = sorted[(int)(sorted.Length * 0.1)];
        ThresholdDecibels = Math.Clamp(noiseFloor + AboveNoiseDecibels, QuietestVoiceDecibels, LoudestThresholdDecibels);

        for (int frame = 0; frame < frameDecibels.Count; frame++)
        {
            if (frameDecibels[frame] < ThresholdDecibels) continue;
            loud[frame] = true;
            TotalVoicedSeconds += FrameSeconds;
            int first = Math.Max(0, frame - SpreadFrames);
            int last = Math.Min(voiced.Length - 1, frame + SpreadFrames);
            for (int spread = first; spread <= last; spread++) voiced[spread] = true;
        }
    }

    public double ThresholdDecibels { get; }

    /// <summary>
    /// How much sound the whole track has above its background. A call app's beeps (join, mute, unmute) add up to a
    /// fraction of a second; even a single "hello" is about half a second.
    /// </summary>
    public double TotalVoicedSeconds { get; }

    /// <summary>Seconds of actual sound (not counting the spreading between words) inside a stretch of the track.</summary>
    public double LoudSeconds(TimeSpan start, TimeSpan end)
    {
        if (loud.Length == 0) return end.TotalSeconds - start.TotalSeconds;
        int first = Math.Clamp((int)Math.Floor(start.TotalSeconds / FrameSeconds), 0, loud.Length - 1);
        int last = Math.Clamp((int)Math.Ceiling(end.TotalSeconds / FrameSeconds), first + 1, loud.Length);
        int loudFrames = 0;
        for (int frame = first; frame < last; frame++)
        {
            if (loud[frame]) loudFrames++;
        }
        return loudFrames * FrameSeconds;
    }

    /// <summary>The share (0..1) of a stretch of the track that has a voice in it.</summary>
    public double VoicedFraction(TimeSpan start, TimeSpan end)
    {
        if (voiced.Length == 0) return 1.0; // Nothing measured: do not judge.
        int first = Math.Clamp((int)Math.Floor(start.TotalSeconds / FrameSeconds), 0, voiced.Length - 1);
        int last = Math.Clamp((int)Math.Ceiling(end.TotalSeconds / FrameSeconds), first + 1, voiced.Length);
        int voicedFrames = 0;
        for (int frame = first; frame < last; frame++)
        {
            if (voiced[frame]) voicedFrames++;
        }
        return (double)voicedFrames / (last - first);
    }
}
