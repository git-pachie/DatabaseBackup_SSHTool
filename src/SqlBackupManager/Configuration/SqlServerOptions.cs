namespace SqlBackupManager.Configuration;

/// <summary>
/// SQL Server connection and backup settings.
/// </summary>
public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    /// <summary>
    /// Connection string used to connect to SQL Server. Typically targets the master database
    /// for backup commands. Use Integrated Security when possible.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// List of database names to back up, in processing order.
    /// </summary>
    public List<string> Databases { get; set; } = [];

    /// <summary>
    /// Local path on the SQL Server host where BACKUP DATABASE writes the .bak file.
    /// The SQL Server service account must have write access.
    /// </summary>
    public string BackupDirectoryOnSqlServer { get; set; } = string.Empty;

    /// <summary>
    /// Path the application uses to read the same physical folder. A UNC share when SQL Server
    /// is on another machine (\\server\share\...), or a local path (D:\...) when it runs on this machine.
    /// The application's Windows account must have read access.
    /// </summary>
    public string BackupDirectoryFromApp { get; set; } = string.Empty;

    /// <summary>
    /// When true, adds COMPRESSION to the BACKUP DATABASE statement.
    /// Defaults to false (ZIP-only) since native compression plus ZIP yields little gain.
    /// </summary>
    public bool UseNativeCompression { get; set; } = false;

    /// <summary>
    /// SQL command timeout in seconds for backup and verify operations.
    /// Default is 3600 (1 hour) since backups can be long-running.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// Optional: SQL username for SQL authentication. When null, uses Integrated Security.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Optional: SQL password for SQL authentication. Stored in user secrets.
    /// </summary>
    public string? Password { get; set; }
}
