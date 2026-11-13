using SqlBackupManager.Models;

namespace SqlBackupManager.Interfaces;

/// <summary>
/// Creates a ZIP archive from a single .bak file and verifies archive integrity.
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// Creates a ZIP file containing exactly the specified .bak file, using streaming
    /// to avoid loading the entire file into memory. Verifies archive integrity after creation.
    /// </summary>
    /// <param name="bakFilePath">Full path to the source .bak file (read via UNC or local path).</param>
    /// <param name="zipFilePath">Full path to the ZIP file to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The result containing the ZIP file path and size, or a failure with error message.</returns>
    Task<DatabaseBackupResult> CreateZipAsync(
        string databaseName,
        string bakFilePath,
        string zipFilePath,
        CancellationToken cancellationToken = default);
}
