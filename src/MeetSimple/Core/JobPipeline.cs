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
                report(new StepUpdate("summary", StepState.Skipped, "", null));
            }
        }

        string tempFolder = Path.Combine(Path.GetTempPath(), "MeetSimple", Guid.NewGuid().ToString("N"));
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
                        track.SourcePath, Path.Combine(tempFolder, $"track{index}.wav"), trackProgress, cancellationToken);
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
                    perTrack.Add(await TranscriptionService.TranscribeAsync(
                        modelPath, audible[index].Audio.WavPath, audible[index].Speaker,
                        new TranscriptionOptions(request.Language, request.Glossary, request.BestAccuracy),
                        audible[index].Audio.Duration, trackProgress, cancellationToken));
                }
                var merged = TextProcessing.MergeTracks(perTrack);
                return (merged, $"{merged.Count} passages");
            });

            Directory.CreateDirectory(request.OutputFolder);
            string safeBaseName = AppHealth.UniqueBaseName(request.OutputFolder, InputValidation.SafeFileName(request.BaseName));
            string transcriptPath = Path.Combine(request.OutputFolder, safeBaseName + "_transcript.txt");
            await File.WriteAllTextAsync(transcriptPath, TextProcessing.RenderTranscript(segments), Encoding.UTF8, cancellationToken);

            var result = new JobResult { TranscriptPath = transcriptPath, SummaryError = summaryError };

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
                    result.SummaryPath = Path.Combine(request.OutputFolder, safeBaseName + "_summary.txt");
                    await File.WriteAllTextAsync(result.SummaryPath, result.SummaryText + Environment.NewLine, Encoding.UTF8, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    result.SummaryError = error.Message;
                }
            }

            result.Elapsed = stopwatch.Elapsed;
            return result;
        }
        finally
        {
            try { Directory.Delete(tempFolder, recursive: true); } catch (Exception) { }
        }
    }

    private static string DescribeProgress(double fraction, TimeSpan elapsed)
    {
        string percent = $"{(int)(fraction * 100)}%";
        if (fraction < 0.03) return percent;
        double minutesLeft = elapsed.TotalMinutes * (1 - fraction) / fraction;
        return $"{percent}  -  about {Math.Max(1, (int)Math.Round(minutesLeft))} min left";
    }
}
