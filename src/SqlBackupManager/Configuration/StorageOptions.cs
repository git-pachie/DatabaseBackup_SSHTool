namespace SqlBackupManager.Configuration;

/// <summary>
/// Local storage paths for ZIP output and run logs.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Local directory where per-database ZIP files are written.
    /// </summary>
    public string LocalZipDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Local directory where per-run Serilog log files are written.
    /// </summary>
    public string LogDirectory { get; set; } = string.Empty;
}
