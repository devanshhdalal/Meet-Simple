namespace MeetSimple.Core;

public enum JobMode { TranscribeOnly, TranscribeAndSummarize }

public enum StepState { Pending, Active, Done, Failed, Skipped }

public sealed record ProgressInfo(double? Fraction, string Detail);

public sealed record StepUpdate(string StepId, StepState State, string Detail, double? Fraction);

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string? Speaker, string Text);

/// <summary>Everything that tunes how the speech model listens.</summary>
public sealed record TranscriptionOptions(string? Language, string? Glossary, bool BestAccuracy);

/// <summary>One audio file to transcribe. Speaker is "You" / "Others" for recorded meetings, null for imported files.</summary>
public sealed record AudioTrack(string? Speaker, string SourcePath);

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
}

public sealed class JobResult
{
    public required string TranscriptPath { get; init; }
    public string? SummaryPath { get; set; }
    public string SummaryText { get; set; } = "";
    public string SummaryError { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
}

/// <summary>An IProgress that runs its handler immediately on the calling thread (keeps updates in order).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> handler;
    public SyncProgress(Action<T> handler) => this.handler = handler;
    public void Report(T value) => handler(value);
}
