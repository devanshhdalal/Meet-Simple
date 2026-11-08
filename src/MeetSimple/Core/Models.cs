namespace MeetSimple.Core;

public enum JobMode { TranscribeOnly, TranscribeAndSummarize }

public enum StepState { Pending, Active, Done, Failed, Skipped }

public sealed record ProgressInfo(double? Fraction, string Detail);

public sealed record StepUpdate(string StepId, StepState State, string Detail, double? Fraction);

/// <summary>Language is the code the speech model detected (or was told), for example "en"; null when unknown.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string? Speaker, string Text, string? Language = null);

/// <summary>Everything that tunes how the speech model listens.</summary>
public sealed record TranscriptionOptions(string? Language, string? Glossary, bool BestAccuracy);

/// <summary>
/// One speaker's audio. Speaker is "You" / "Others" for recorded meetings, null for imported files.
/// Very long recordings are split into several files (parts), which are transcribed as one continuous track.
/// </summary>
public sealed record AudioTrack(string? Speaker, IReadOnlyList<string> SourcePaths)
{
    public AudioTrack(string? speaker, string sourcePath) : this(speaker, new[] { sourcePath })
    {
    }
}

public sealed class JobRequest
{
    public required JobMode Mode { get; init; }
    public required IReadOnlyList<AudioTrack> Tracks { get; init; }
    public required string OutputFolder { get; init; }
    public required string BaseName { get; init; }
    public required string SpeechModel { get; init; }
    public required string SummaryModel { get; init; }
    public string? Language { get; init; }
    public string? Glossary { get; init; }
    public bool BestAccuracy { get; init; } = true;

    /// <summary>The recording folder to mark as handled once the transcript exists (see RecordingRecovery).</summary>
    public string? RecoveryFolder { get; init; }
}

public sealed class JobResult
{
    public required string TranscriptPath { get; init; }
    public string? SummaryPath { get; set; }
    public string SummaryText { get; set; } = "";
    public string SummaryError { get; set; } = "";

    /// <summary>Something the user should know about where or how the results were saved.</summary>
    public string? Notice { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>An IProgress that runs its handler immediately on the calling thread (keeps updates in order).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> handler;
    public SyncProgress(Action<T> handler) => this.handler = handler;
    public void Report(T value) => handler(value);
}
