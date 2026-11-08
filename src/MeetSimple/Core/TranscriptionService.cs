using Whisper.net;
using Whisper.net.LibraryLoader;

namespace MeetSimple.Core;

public static class TranscriptionService
{
    /// <summary>
    /// Whether this run of the app may use the graphics card. Whisper.net loads its native engine once, at first use,
    /// so this is fixed at startup; a change in Settings applies after a restart.
    /// </summary>
    public static bool GraphicsCardEnabledForThisRun { get; private set; } = true;

    public static void ConfigureRuntime(bool useGraphicsCard)
    {
        GraphicsCardEnabledForThisRun = useGraphicsCard;
        if (!useGraphicsCard)
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        }
    }
}

/// <summary>
/// The loaded speech model. Loading the large models takes a while, so one engine is shared by every track of a job
/// (a recorded meeting has two) instead of loading the model again for each one.
/// </summary>
public sealed class TranscriptionEngine : IDisposable
{
    private readonly WhisperFactory factory;
    private readonly bool watchesGraphicsCard;

    private TranscriptionEngine(WhisperFactory factory, bool watchesGraphicsCard)
    {
        this.factory = factory;
        this.watchesGraphicsCard = watchesGraphicsCard;
    }

    public static Task<TranscriptionEngine> LoadAsync(string modelPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // A crash in the native engine cannot be caught, so leave a marker that the next start can find.
            bool watchGraphicsCard = TranscriptionService.GraphicsCardEnabledForThisRun;
            if (watchGraphicsCard) AppHealth.MarkGpuAttempt();
            try
            {
                return new TranscriptionEngine(LoadModel(modelPath), watchGraphicsCard);
            }
            catch
            {
                if (watchGraphicsCard) AppHealth.ClearGpuAttempt();
                throw;
            }
        }, cancellationToken);

    /// <summary>
    /// Transcribes a 16 kHz mono WAV. Each recognised passage is also handed to onSegment as soon as it exists.
    /// Passages with no voice under them (Whisper's inventions over silence) are left out; see TextProcessing.IsInventedText.
    /// </summary>
    public Task<List<TranscriptSegment>> TranscribeAsync(
        string wavPath, string? speaker, TranscriptionOptions options, TimeSpan totalDuration, SpeechMap? speech,
        SpeechTimeline? timeline, IProgress<double> progress, Action<TranscriptSegment>? onSegment, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var segments = new List<TranscriptSegment>();
            double totalSeconds = Math.Max(totalDuration.TotalSeconds, 1.0);

            using var processor = BuildProcessor(options);
            await using var wavStream = File.OpenRead(wavPath);
            await foreach (var result in processor.ProcessAsync(wavStream, cancellationToken))
            {
                string text = result.Text.Trim();
                // Times from the speech-only audio are mapped back onto the original recording.
                TimeSpan start = timeline?.ToOriginal(result.Start) ?? result.Start;
                TimeSpan end = timeline?.ToOriginal(result.End) ?? result.End;
                double voicedFraction = speech?.VoicedFraction(start, end) ?? 1.0;
                double loudSeconds = speech?.LoudSeconds(start, end) ?? double.MaxValue;
                if (!TextProcessing.IsNoiseMarker(text)
                    && !TextProcessing.IsInventedText(text, voicedFraction, loudSeconds, result.NoSpeechProbability))
                {
                    var segment = new TranscriptSegment(start, end, speaker, text, NormalizeLanguage(result.Language));
                    segments.Add(segment);
                    onSegment?.Invoke(segment);
                }
                progress.Report(Math.Min(result.End.TotalSeconds / totalSeconds, 1.0));
            }

            return TextProcessing.RemoveRepetitionLoops(segments);
        }, cancellationToken);
    }

    public void Dispose()
    {
        factory.Dispose();
        if (watchesGraphicsCard) AppHealth.ClearGpuAttempt();
    }

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();

    private static WhisperFactory LoadModel(string modelPath)
    {
        try
        {
            return WhisperFactory.FromPath(modelPath);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The speech model could not be loaded. Try again. If it keeps failing, choose \"Processor only\" under "
                + "Graphics card in Settings, or a smaller speech model.", error);
        }
    }

    /// <summary>
    /// Matches what the faster-whisper command-line script does: beam search, temperature fallback and the usual
    /// "is this actually speech" thresholds, plus an optional glossary prompt for names and jargon.
    /// </summary>
    private WhisperProcessor BuildProcessor(TranscriptionOptions options)
    {
        var builder = factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language.Trim())
            .WithTemperature(0f)
            .WithTemperatureInc(0.2f)
            .WithEntropyThreshold(2.4f)
            .WithLogProbThreshold(-1f)
            .WithNoSpeechThreshold(0.6f)
            // Not continuing from the previous sentence is what stops Whisper repeating itself after a pause
            // (faster-whisper's condition_on_previous_text=False, the most-recommended hallucination fix).
            .WithNoContext();

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
