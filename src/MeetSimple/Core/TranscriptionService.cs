using Whisper.net;

namespace MeetSimple.Core;

public static class TranscriptionService
{
    /// <summary>Transcribes a 16 kHz mono WAV. The graphics card (Vulkan) is used automatically when available.</summary>
    public static async Task<List<TranscriptSegment>> TranscribeAsync(
        string modelPath, string wavPath, string? speaker, TranscriptionOptions options, TimeSpan totalDuration,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var segments = new List<TranscriptSegment>();
            double totalSeconds = Math.Max(totalDuration.TotalSeconds, 1.0);

            using var factory = WhisperFactory.FromPath(modelPath);
            using var processor = BuildProcessor(factory, options);

            await using var wavStream = File.OpenRead(wavPath);
            await foreach (var result in processor.ProcessAsync(wavStream, cancellationToken))
            {
                string text = result.Text.Trim();
                if (!TextProcessing.IsNoiseMarker(text))
                {
                    segments.Add(new TranscriptSegment(result.Start, result.End, speaker, text));
                }
                progress.Report(Math.Min(result.End.TotalSeconds / totalSeconds, 1.0));
            }

            return TextProcessing.RemoveRepetitionLoops(segments);
        }, cancellationToken);
    }

    /// <summary>
    /// Matches what the faster-whisper command-line script does: beam search, temperature fallback and the usual
    /// "is this actually speech" thresholds, plus an optional glossary prompt for names and jargon.
    /// </summary>
    private static WhisperProcessor BuildProcessor(WhisperFactory factory, TranscriptionOptions options)
    {
        var builder = factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language.Trim())
            .WithTemperature(0f)
            .WithTemperatureInc(0.2f)
            .WithEntropyThreshold(2.4f)
            .WithLogProbThreshold(-1f)
            .WithNoSpeechThreshold(0.6f);

        string glossary = InputValidation.CleanGlossary(options.Glossary);
        if (glossary.Length > 0)
        {
            builder = builder.WithPrompt("Glossary: " + glossary + ".");
        }

        if (options.BestAccuracy)
        {
            var sampling = (BeamSearchSamplingStrategyBuilder)builder.WithBeamSearchSamplingStrategy();
            builder = sampling.WithBeamSize(5).WithPatience(1f).ParentBuilder;
        }
        return builder.Build();
    }
}
