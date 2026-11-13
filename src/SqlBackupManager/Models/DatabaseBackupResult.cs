namespace SqlBackupManager.Models;

/// <summary>
/// Result of processing a single database through the backup pipeline.
/// </summary>
public sealed class DatabaseBackupResult
{
    /// <summary>
    /// Name of the database that was processed.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the database completed all stages successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The stage where processing ended (Completed on success, or the failed stage).
    /// </summary>
    public BackupStage Stage { get; set; }

    /// <summary>
    /// Error message when the database failed; null on success.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Full path to the .bak file on the SQL Server host (used in BACKUP DATABASE TO DISK).
    /// </summary>
    public string? BakFilePathOnServer { get; set; }

    /// <summary>
    /// UNC/local path used by the application to read the .bak file.
    /// </summary>
    public string? BakFilePathFromApp { get; set; }

    /// <summary>
    /// Full path to the local ZIP file created by the application.
    /// </summary>
    public string? ZipFilePath { get; set; }

    /// <summary>
    /// Size of the .bak file in bytes, if available.
    /// </summary>
    public long? BakFileSizeBytes { get; set; }

    /// <summary>
    /// Size of the ZIP file in bytes, if available.
    /// </summary>
    public long? ZipFileSizeBytes { get; set; }

    /// <summary>
    /// Remote SFTP path where the ZIP was uploaded, if successful.
    /// </summary>
    public string? RemoteZipPath { get; set; }

    /// <summary>
    /// When processing started for this database.
    /// </summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// When processing ended for this database.
    /// </summary>
    public DateTime EndedAtUtc { get; set; }

    /// <summary>
    /// Elapsed time for processing this database.
    /// </summary>
    public TimeSpan Elapsed => EndedAtUtc > StartedAtUtc ? EndedAtUtc - StartedAtUtc : TimeSpan.Zero;
}
