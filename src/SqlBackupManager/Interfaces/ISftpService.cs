using SqlBackupManager.Models;

namespace SqlBackupManager.Interfaces;

/// <summary>
/// Uploads a ZIP file to a configured SFTP remote directory with host-key verification.
/// </summary>
public interface ISftpService
{
    /// <summary>
    /// Uploads the specified local ZIP file to the configured SFTP remote directory.
    /// Uses a temporary remote name during upload, verifies size, then renames to final name.
    /// </summary>
    /// <param name="localFilePath">Full local path to the ZIP file to upload.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The result containing the remote path, or a failure with error message.</returns>
    Task<DatabaseBackupResult> UploadAsync(
        string databaseName,
        string localFilePath,
        CancellationToken cancellationToken = default);
}
