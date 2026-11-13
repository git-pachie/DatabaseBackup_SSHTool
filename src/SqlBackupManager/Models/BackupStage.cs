namespace SqlBackupManager.Models;

/// <summary>
/// Represents the stage in the backup pipeline where a database result currently sits
/// or where it failed.
/// </summary>
public enum BackupStage
{
    /// <summary>
    /// Initial validation of database name and availability.
    /// </summary>
    Validation,

    /// <summary>
    /// Executing BACKUP DATABASE on SQL Server.
    /// </summary>
    SqlBackup,

    /// <summary>
    /// Verifying the backup file is accessible and nonempty.
    /// </summary>
    VerifyBackup,

    /// <summary>
    /// Creating the ZIP archive from the .bak file.
    /// </summary>
    Zip,

    /// <summary>
    /// Verifying ZIP archive integrity.
    /// </summary>
    VerifyZip,

    /// <summary>
    /// Uploading the ZIP to the SFTP server.
    /// </summary>
    SftpUpload,

    /// <summary>
    /// Completed all stages successfully.
    /// </summary>
    Completed
}
