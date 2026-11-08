using System.Diagnostics;
using System.Text;

namespace MeetSimple.Core;

/// <summary>
/// One job from start to finish. Setup (speech model, Ollama, summary AI) happens here, on demand,
/// and only for the steps the chosen mode actually needs.
/// </summary>
public sealed class JobPipeline
{
    private const float SilenceThreshold = 0.003f;

    private readonly JobRequest request;
    private readonly Action<StepUpdate> report;
    private readonly HashSet<string> startedSteps = new();

    public JobPipeline(JobRequest request, Action<StepUpdate> report)
    {
        this.request = request;
        this.report = report;
    }

    /// <summary>A copy of the transcript written passage by passage while transcribing. Kept if the job fails.</summary>
    public string? PartialTranscriptPath { get; private set; }

    /// <summary>Where results go when the chosen folder cannot be written to (read-only share, removed USB drive).</summary>
    public static string FallbackFolder => Path.Combine(AppSettings.RecordingsRoot, "Transcripts");

    public static IReadOnlyList<(string Id, string Title)> StepsFor(JobMode mode)
    {
        var steps = new List<(string, string)> { ("speech_model", "Prepare speech recognition") };
        if (mode == JobMode.TranscribeAndSummarize)
        {
            steps.Add(("ollama", "Set up the summary engine"));
            steps.Add(("ai_model", "Get the summary AI"));
        }
        steps.Add(("audio", "Read the recording"));
        steps.Add(("transcribe", "Transcribe"));
        if (mode == JobMode.TranscribeAndSummarize)
        {
            steps.Add(("summary", "Write the summary"));
        }
        return steps;
    }

    private async Task<T> RunStepAsync<T>(
        string stepId, Func<IProgress<ProgressInfo>, Task<(T Value, string DoneDetail)>> work)
    {
        startedSteps.Add(stepId);
        report(new StepUpdate(stepId, StepState.Active, "", null));
        var progress = new SyncProgress<ProgressInfo>(info =>
            report(new StepUpdate(stepId, StepState.Active, info.Detail, info.Fraction)));
        try
        {
            var (value, doneDetail) = await work(progress);
            report(new StepUpdate(stepId, StepState.Done, doneDetail, 1.0));
            return value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            report(new StepUpdate(stepId, StepState.Failed, error.Message, null));
            throw;
        }
    }

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        bool summarize = request.Mode == JobMode.TranscribeAndSummarize;

        // Decide where the results go before hours of work, not after.
        var (outputFolder, folderNotice) = ChooseOutputFolder(request.OutputFolder);

        string modelPath = await RunStepAsync<string>("speech_model", async progress =>
        {
            var (path, alreadyPresent) = await SpeechModelStore.EnsureAsync(request.SpeechModel, progress, cancellationToken);
            return (path, alreadyPresent ? "Ready" : "Downloaded");
        });

        string summaryError = "";
        if (summarize)
        {
            try
            {
                await RunStepAsync<bool>("ollama", async progress =>
                {
                    bool alreadyRunning = await OllamaSetup.EnsureReadyAsync(progress, cancellationToken);
                    return (true, alreadyRunning ? "Ready" : "Installed and running");
                });
                await RunStepAsync<bool>("ai_model", async progress =>
                {
                    var installed = await OllamaClient.TryListModelsAsync(cancellationToken) ?? new List<string>();
                    if (OllamaClient.IsInstalled(installed, request.SummaryModel))
                    {
                        return (true, "Already installed");
                    }
                    await OllamaClient.PullModelAsync(request.SummaryModel, progress, cancellationToken);
                    return (true, "Downloaded");
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                // The transcript is still worth delivering even if the summary engine could not be set up.
                summaryError = error.Message;
                summarize = false;
                foreach (var stepId in new[] { "ollama", "ai_model", "summary" })
                {
                    if (!startedSteps.Contains(stepId))
                    {
                        report(new StepUpdate(stepId, StepState.Skipped, "", null));
                    }
                }
            }
        }

        string tempFolder = Path.Combine(AppHealth.TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        try
        {
            var preparedTracks = await RunStepAsync<List<(string? Speaker, PreparedAudio Audio)>>("audio", async progress =>
            {
                var prepared = new List<(string? Speaker, PreparedAudio Audio)>();
                for (int index = 0; index < request.Tracks.Count; index++)
                {
                    var track = request.Tracks[index];
                    int trackIndex = index;
                    var trackProgress = new SyncProgress<double>(fraction =>
                        progress.Report(new ProgressInfo((trackIndex + fraction) / request.Tracks.Count, "Reading audio...")));
                    var audio = await AudioPreparer.PrepareAsync(
                        track.SourcePaths, Path.Combine(tempFolder, $"track{index}.wav"), trackProgress, cancellationToken);
                    prepared.Add((track.Speaker, audio));
                }
                var longest = prepared.Max(item => item.Audio.Duration);
                return (prepared, $"{longest.TotalMinutes:0} min of audio");
            });

            var segments = await RunStepAsync<List<TranscriptSegment>>("transcribe", async progress =>
            {
                var audible = preparedTracks.Where(item => item.Audio.Peak >= SilenceThreshold).ToList();
                if (audible.Count == 0)
                {
                    return (new List<TranscriptSegment>(), "No speech detected");
                }

                progress.Report(new ProgressInfo(null, "Loading the speech model..."));
                using var engine = await TranscriptionEngine.LoadAsync(modelPath, cancellationToken);
                using var partialTranscript = OpenPartialTranscript();
                var options = new TranscriptionOptions(request.Language, request.Glossary, request.BestAccuracy);

                var transcribeClock = Stopwatch.StartNew();
                var perTrack = new List<IReadOnlyList<TranscriptSegment>>();
                for (int index = 0; index < audible.Count; index++)
                {
                    int trackIndex = index;
                    var trackProgress = new SyncProgress<double>(fraction =>
                    {
                        double overall = (trackIndex + fraction) / audible.Count;
                        progress.Report(new ProgressInfo(overall, DescribeProgress(overall, transcribeClock.Elapsed)));
                    });
                    perTrack.Add(await engine.TranscribeAsync(
                        audible[index].Audio.WavPath, audible[index].Speaker, options, audible[index].Audio.Duration,
                        trackProgress, partialTranscript.Append, cancellationToken));
                }
                var merged = TextProcessing.MergeTracks(perTrack);
                return (merged, $"{merged.Count} passages");
            });

            string transcriptText = TextProcessing.RenderTranscript(segments);
            var (transcriptPath, finalFolder, writeNotice) = await WriteTranscriptAsync(outputFolder, transcriptText, cancellationToken);
            string baseName = Path.GetFileName(transcriptPath)[..^"_transcript.txt".Length];
            var result = new JobResult
            {
                TranscriptPath = transcriptPath,
                SummaryError = summaryError,
                Notice = writeNotice ?? folderNotice,
            };
            DeletePartialTranscript();

            if (summarize)
            {
                try
                {
                    result.SummaryText = await RunStepAsync<string>("summary", async progress =>
                    {
                        string text = await SummaryService.SummarizeAsync(
                            segments, request.SummaryModel, progress, cancellationToken);
                        return (text, "Done");
                    });
                    result.SummaryPath = Path.Combine(finalFolder, baseName + "_summary.txt");
                    await File.WriteAllTextAsync(result.SummaryPath, result.SummaryText + Environment.NewLine, Encoding.UTF8, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    result.SummaryPath = null;
                    result.SummaryError = error.Message;
                }
            }

            result.Elapsed = stopwatch.Elapsed;
            return result;
        }
        catch (OperationCanceledException)
        {
            DeletePartialTranscript();
            throw;
        }
        finally
        {
            try { Directory.Delete(tempFolder, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Writes the transcript next to the source; if that fails at the last moment, into the fallback folder.</summary>
    private async Task<(string Path, string Folder, string? Notice)> WriteTranscriptAsync(
        string folder, string transcriptText, CancellationToken cancellationToken)
    {
        string safeBaseName = InputValidation.SafeFileName(request.BaseName);
        try
        {
            string path = Path.Combine(folder, AppHealth.UniqueBaseName(folder, safeBaseName) + "_transcript.txt");
            await File.WriteAllTextAsync(path, transcriptText, Encoding.UTF8, cancellationToken);
            return (path, folder, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException && folder != FallbackFolder)
        {
            Directory.CreateDirectory(FallbackFolder);
            string path = Path.Combine(FallbackFolder, AppHealth.UniqueBaseName(FallbackFolder, safeBaseName) + "_transcript.txt");
            await File.WriteAllTextAsync(path, transcriptText, Encoding.UTF8, cancellationToken);
            return (path, FallbackFolder,
                $"The original folder could not be written to ({error.Message}), so the results were saved in {FallbackFolder}.");
        }
    }

    private static (string Folder, string? Notice) ChooseOutputFolder(string preferredFolder)
    {
        if (CanWriteTo(preferredFolder)) return (preferredFolder, null);
        Directory.CreateDirectory(FallbackFolder);
        return (FallbackFolder, $"The original folder could not be written to, so the results were saved in {FallbackFolder}.");
    }

    /// <summary>The only reliable test for write access on Windows (ACLs, read-only shares, locked drives) is to try.</summary>
    private static bool CanWriteTo(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probePath = Path.Combine(folder, $".meetsimple-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private PartialTranscript OpenPartialTranscript()
    {
        string folder = Path.Combine(AppHealth.DataFolder, "in-progress");
        string path = Path.Combine(folder,
            $"{InputValidation.SafeFileName(request.BaseName)} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.txt");
        var partial = PartialTranscript.TryOpen(path);
        if (partial.IsOpen) PartialTranscriptPath = path;
        return partial;
    }

    private void DeletePartialTranscript()
    {
        if (PartialTranscriptPath == null) return;
        try { File.Delete(PartialTranscriptPath); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        PartialTranscriptPath = null;
    }

    private static string DescribeProgress(double fraction, TimeSpan elapsed)
    {
        string percent = $"{(int)(fraction * 100)}%";
        if (fraction < 0.03) return percent;
        double minutesLeft = elapsed.TotalMinutes * (1 - fraction) / fraction;
        return $"{percent}  -  about {Math.Max(1, (int)Math.Round(minutesLeft))} min left";
    }

    /// <summary>Appends each passage as it is recognised, so a crash or failure hours in still leaves the text on disk.</summary>
    private sealed class PartialTranscript : IDisposable
    {
        private readonly StreamWriter? writer;
        private readonly object gate = new();

        private PartialTranscript(StreamWriter? writer) => this.writer = writer;

        public bool IsOpen => writer != null;

        public static PartialTranscript TryOpen(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine("Unfinished transcript (safety copy written while Meet Simple was transcribing)");
                writer.WriteLine();
                return new PartialTranscript(writer);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new PartialTranscript(null);
            }
        }

        public void Append(TranscriptSegment segment)
        {
            if (writer == null) return;
            lock (gate)
            {
                try
                {
                    string speaker = segment.Speaker != null ? segment.Speaker + ": " : "";
                    writer.WriteLine($"[{TextProcessing.FormatTimestamp(segment.Start)}] {speaker}{segment.Text}");
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The safety copy is best effort; the real transcript does not depend on it.
                }
            }
        }

        public void Dispose()
        {
            lock (gate) writer?.Dispose();
        }
    }
}
