using Whisper.net;
using Whisper.net.LibraryLoader;

namespace MeetSimple.Core;

public static class TranscriptionService
{
    /// <summary>
    /// Chooses processor-only mode when the graphics card is switched off. Whisper.net loads its native engine once,
    /// at first use, so this is called at startup and a change in Settings applies the next time the app opens.
    /// </summary>
    public static void ConfigureRuntime(bool useGraphicsCard)
    {
        if (!useGraphicsCard)
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        }
    }

    /// <summary>Transcribes a 16 kHz mono WAV. The graphics card (Vulkan) is used automatically when available.</summary>
    public static async Task<List<TranscriptSegment>> TranscribeAsync(
        string modelPath, string wavPath, string? speaker, TranscriptionOptions options, TimeSpan totalDuration,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        // A crash in the native engine cannot be caught, so leave a marker that the next start can find.
        bool trackGraphicsCard = AppSettings.Current.UseGraphicsCard;
        if (trackGraphicsCard) AppHealth.MarkGpuAttempt();
        try
        {
            return await Task.Run(async () =>
            {
                var segments = new List<TranscriptSegment>();
                double totalSeconds = Math.Max(totalDuration.TotalSeconds, 1.0);

                using var factory = LoadModel(modelPath);
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
        finally
        {
            if (trackGraphicsCard) AppHealth.ClearGpuAttempt();
        }
    }

    private static WhisperFactory LoadModel(string modelPath)
    {
        try
        {
            return WhisperFactory.FromPath(modelPath);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The speech model could not be loaded. Try again. If it keeps failing, switch off \"Use graphics card\" "
                + "in Settings (and restart the app) or choose a smaller speech model.", error);
        }
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
