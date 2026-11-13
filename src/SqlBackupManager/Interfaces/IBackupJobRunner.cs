using SqlBackupManager.Models;

namespace SqlBackupManager.Interfaces;

/// <summary>
/// Orchestrates the full backup run: sequentially backs up each configured database,
/// creates ZIPs, uploads via SFTP, and aggregates results.
/// </summary>
public interface IBackupJobRunner
{
    /// <summary>
    /// Runs the complete backup workflow for all configured databases.
    /// Continues with remaining databases after an individual failure.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the entire run.</param>
    /// <returns>The aggregate result for the entire run.</returns>
    Task<BackupRunResult> RunAsync(CancellationToken cancellationToken = default);
}
