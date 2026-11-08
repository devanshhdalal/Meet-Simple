namespace MeetSimple.Core;

/// <summary>
/// Cancels an operation that has gone quiet, rather than one that is merely long. A summary of a two-hour meeting
/// can legitimately run for many minutes; what signals a hang is no new data for a while. Call Kick on every sign of life.
/// </summary>
internal sealed class IdleWatchdog : IDisposable
{
    private readonly CancellationToken outerToken;
    private readonly CancellationTokenSource source;

    public IdleWatchdog(TimeSpan firstTimeout, CancellationToken outerToken)
    {
        this.outerToken = outerToken;
        source = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        source.CancelAfter(firstTimeout);
    }

    public CancellationToken Token => source.Token;

    /// <summary>True when the watchdog (not the user) stopped the operation.</summary>
    public bool HasFired => source.IsCancellationRequested && !outerToken.IsCancellationRequested;

    public void Kick(TimeSpan nextTimeout)
    {
        if (!source.IsCancellationRequested) source.CancelAfter(nextTimeout);
    }

    public void Dispose() => source.Dispose();
}
