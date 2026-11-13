using SqlBackupManager.Models;

namespace SqlBackupManager.Interfaces;

/// <summary>
/// Sends one consolidated email summary after processing all databases.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends a consolidated HTML/plain-text summary email reporting per-database
    /// successes and failures. Optionally attaches the run log file.
    /// </summary>
    /// <param name="runResult">Aggregate result from the entire backup run.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the email was sent successfully; false if sending failed or was skipped.</returns>
    Task<bool> SendSummaryEmailAsync(BackupRunResult runResult, CancellationToken cancellationToken = default);
}
