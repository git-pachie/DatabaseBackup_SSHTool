namespace SqlBackupManager.Models;

/// <summary>
/// Aggregate result for an entire backup run across all configured databases.
/// </summary>
public sealed class BackupRunResult
{
    /// <summary>
    /// Unique identifier for this run.
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the run started.
    /// </summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the run ended.
    /// </summary>
    public DateTime EndedAtUtc { get; set; }

    /// <summary>
    /// Overall elapsed time for the entire run.
    /// </summary>
    public TimeSpan Elapsed => EndedAtUtc > StartedAtUtc ? EndedAtUtc - StartedAtUtc : TimeSpan.Zero;

    /// <summary>
    /// Per-database results in processing order.
    /// </summary>
    public List<DatabaseBackupResult> DatabaseResults { get; set; } = [];

    /// <summary>
    /// Number of databases that completed successfully.
    /// </summary>
    public int SuccessCount => DatabaseResults.Count(r => r.Success);

    /// <summary>
    /// Number of databases that failed.
    /// </summary>
    public int FailureCount => DatabaseResults.Count(r => !r.Success);

    /// <summary>
    /// Total number of configured databases processed.
    /// </summary>
    public int TotalCount => DatabaseResults.Count;

    /// <summary>
    /// True when every database succeeded.
    /// </summary>
    public bool AllSucceeded => FailureCount == 0 && TotalCount > 0;

    /// <summary>
    /// True when at least one database succeeded and at least one failed.
    /// </summary>
    public bool IsPartial => SuccessCount > 0 && FailureCount > 0;

    /// <summary>
    /// True when all databases failed (or none were configured).
    /// </summary>
    public bool AllFailed => SuccessCount == 0 && TotalCount > 0;

    /// <summary>
    /// Whether the email summary was sent successfully.
    /// </summary>
    public bool EmailSent { get; set; }

    /// <summary>
    /// Error message if email sending failed, null otherwise.
    /// </summary>
    public string? EmailError { get; set; }

    /// <summary>
    /// Path to the per-run log file.
    /// </summary>
    public string? LogFilePath { get; set; }

    /// <summary>
    /// Overall status label for the run: "SUCCESS", "PARTIAL", or "FAILED".
    /// Returns "FAILED" when no databases were configured.
    /// </summary>
    public string StatusLabel => TotalCount == 0
        ? "FAILED"
        : AllSucceeded ? "SUCCESS" : IsPartial ? "PARTIAL" : "FAILED";
}
