using SqlBackupManager.Models;

namespace SqlBackupManager.Interfaces;

/// <summary>
/// Performs a full SQL Server database backup and returns the resulting .bak file paths.
/// </summary>
public interface IDatabaseBackupService
{
    /// <summary>
    /// Validates that the specified database exists and is accessible, then performs
    /// a full BACKUP DATABASE to a uniquely named .bak file.
    /// </summary>
    /// <param name="databaseName">Name of the database to back up.</param>
    /// <param name="runId">Unique run identifier used in the backup file name.</param>
    /// <param name="timestampUtc">UTC timestamp used in the backup file name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The result containing the .bak path on the SQL Server host and the UNC path for the app, or a failure with stage and error message.</returns>
    Task<DatabaseBackupResult> BackupDatabaseAsync(
        string databaseName,
        string runId,
        DateTime timestampUtc,
        CancellationToken cancellationToken = default);
}
