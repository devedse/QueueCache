namespace QueueCache.Developer.Verification;

/// <summary>The scored window stays short; an explicit drain can hold its last
/// outstanding I/O after scoring ends. Observe that completion without changing
/// the score duration, coverage guard or independent restoration deadline.</summary>
public sealed record DrainDecisionTiming
{
    private DrainDecisionTiming(int scoreSeconds) => ScoreSeconds = scoreSeconds;
    public int ScoreSeconds { get; }
    public int CompletionAllowanceSeconds => 300;
    public int ProcessDeadlineSeconds => ScoreSeconds + CompletionAllowanceSeconds;
    public int FlushDeadlineSeconds => ProcessDeadlineSeconds;
    // Readiness can consume up to 45 seconds before the process starts.
    public int TelemetrySeconds => ProcessDeadlineSeconds + 60;
    public int TelemetryDeadlineSeconds => TelemetrySeconds + 10;

    public static DrainDecisionTiming ForScore(int scoreSeconds)
    {
        if (scoreSeconds is < 5 or > 60) throw new ArgumentOutOfRangeException(nameof(scoreSeconds));
        return new(scoreSeconds);
    }
}
