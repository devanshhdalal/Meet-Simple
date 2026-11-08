using System.Diagnostics;

namespace MeetSimple.Core;

public enum JobStatus { Running, Succeeded, Failed, Cancelled }

/// <summary>
/// A running job that lives independently of any screen, so people can look around the app while it works.
/// Screens (RunView, the activity island) attach and detach; the job keeps its own state for whoever looks next.
/// Events are raised on the thread that created the session (the window thread), in the order they happened.
/// </summary>
public sealed class JobSession : IDisposable
{
    private readonly SynchronizationContext context;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Stopwatch stopwatch = new();
    private readonly Dictionary<string, StepUpdate> latestUpdates = new();
    private bool started;

    public JobSession(JobRequest request)
    {
        context = SynchronizationContext.Current ?? new SynchronizationContext();
        Request = request;
        Steps = JobPipeline.StepsFor(request.Mode);
    }

    public JobRequest Request { get; }
    public IReadOnlyList<(string Id, string Title)> Steps { get; }
    public JobStatus Status { get; private set; } = JobStatus.Running;
    public JobResult? Result { get; private set; }
    public Exception? Error { get; private set; }
    public string? PartialTranscriptPath { get; private set; }

    /// <summary>0..1 across all steps (every step counts the same).</summary>
    public double OverallFraction { get; private set; }

    /// <summary>A short line for the current moment, such as "42%  -  about 3 min left".</summary>
    public string StatusLabel { get; private set; } = "Getting ready…";

    /// <summary>The title of the step that is running now, such as "Transcribe".</summary>
    public string ActiveStepTitle { get; private set; } = "Getting ready";

    public TimeSpan Elapsed => stopwatch.Elapsed;

    public event Action<StepUpdate>? StepChanged;

    /// <summary>Raised once, after the last step update, when the job succeeded, failed or was cancelled.</summary>
    public event Action? Finished;

    public StepUpdate? LatestFor(string stepId) => latestUpdates.TryGetValue(stepId, out var update) ? update : null;

    public void Start()
    {
        if (started) return;
        started = true;
        stopwatch.Start();
        var pipeline = new JobPipeline(Request, update => context.Post(_ => Apply(update), null));
        _ = RunAsync(pipeline);
    }

    public void Cancel()
    {
        if (Status == JobStatus.Running) cancellation.Cancel();
    }

    public void Dispose()
    {
        // A pipeline still running may yet touch the token, so it is only released once the job has ended.
        if (Status == JobStatus.Running)
        {
            Cancel();
            return;
        }
        cancellation.Dispose();
    }

    private async Task RunAsync(JobPipeline pipeline)
    {
        JobStatus finalStatus;
        try
        {
            Result = await Task.Run(() => pipeline.RunAsync(cancellation.Token));
            finalStatus = JobStatus.Succeeded;
            if (Request.RecoveryFolder != null) RecordingRecovery.Clear(Request.RecoveryFolder);
        }
        catch (OperationCanceledException)
        {
            finalStatus = JobStatus.Cancelled;
        }
        catch (Exception error)
        {
            Error = error;
            PartialTranscriptPath = pipeline.PartialTranscriptPath;
            AppHealth.LogError(error, "Job failed");
            finalStatus = JobStatus.Failed;
        }
        stopwatch.Stop();

        // Posted, so every step update queued before this lands first and screens show the final state.
        context.Post(_ =>
        {
            Status = finalStatus;
            if (finalStatus == JobStatus.Succeeded)
            {
                OverallFraction = 1.0;
                StatusLabel = "Done";
            }
            Finished?.Invoke();
        }, null);
    }

    private void Apply(StepUpdate update)
    {
        latestUpdates[update.StepId] = update;

        double total = 0.0;
        foreach (var (id, _) in Steps)
        {
            if (!latestUpdates.TryGetValue(id, out var latest)) continue;
            total += latest.State switch
            {
                StepState.Done or StepState.Skipped => 1.0,
                StepState.Active => Math.Clamp(latest.Fraction ?? 0.0, 0.0, 1.0),
                _ => 0.0,
            };
        }
        OverallFraction = Steps.Count == 0 ? 0.0 : total / Steps.Count;

        string stepTitle = Steps.FirstOrDefault(step => step.Id == update.StepId).Title ?? "";
        if (update.State == StepState.Active)
        {
            ActiveStepTitle = stepTitle;
            StatusLabel = string.IsNullOrWhiteSpace(update.Detail) ? stepTitle : update.Detail;
        }
        else if (update.State == StepState.Done)
        {
            StatusLabel = "Finishing up…";
        }
        else if (update.State == StepState.Failed)
        {
            StatusLabel = "Stopped";
        }

        StepChanged?.Invoke(update);
    }
}
