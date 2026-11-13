using System.Text.RegularExpressions;

namespace SqlBackupManager.Utilities;

/// <summary>
/// Generates unique, filesystem-safe file names for backup, ZIP, and log files
/// using a Run ID and UTC timestamp.
/// </summary>
public static class FileNameHelper
{
    /// <summary>
    /// Characters that are invalid in Windows file names, used for sanitization.
    /// </summary>
    private static readonly char[] InvalidFileNameChars =
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    };

    /// <summary>
    /// Generates a short, unique Run ID (timestamp + random suffix) suitable for file naming.
    /// Format: yyyyMMdd_HHmmss_XXXX where XXXX is a 4-char hex suffix.
    /// </summary>
    public static string GenerateRunId(DateTime timestampUtc)
    {
        var datePart = timestampUtc.ToString("yyyyMMdd_HHmmss");
        var randomSuffix = Random.Shared.Next(0x10000, 0xFFFFF).ToString("X4");
        return $"{datePart}_{randomSuffix}";
    }

    /// <summary>
    /// Sanitizes a database name for safe use in file names by replacing
    /// invalid characters with underscores.
    /// </summary>
    public static string SanitizeDatabaseName(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            return "unknown";

        var sanitized = databaseName.Trim();
        foreach (var c in InvalidFileNameChars)
            sanitized = sanitized.Replace(c, '_');

        // Replace any control characters
        sanitized = Regex.Replace(sanitized, @"[\x00-\x1F]", "_");
        return sanitized;
    }

    /// <summary>
    /// Generates the .bak file name for a database: <c>{SanitizedDbName}_{RunId}.bak</c>.
    /// </summary>
    public static string GetBakFileName(string databaseName, string runId)
    {
        var safeName = SanitizeDatabaseName(databaseName);
        return $"{safeName}_{runId}.bak";
    }

    /// <summary>
    /// Generates the ZIP file name for a database: <c>{SanitizedDbName}_{RunId}.zip</c>.
    /// </summary>
    public static string GetZipFileName(string databaseName, string runId)
    {
        var safeName = SanitizeDatabaseName(databaseName);
        return $"{safeName}_{runId}.zip";
    }

    /// <summary>
    /// Generates the per-run log file name: <c>backup_{RunId}.log</c>.
    /// </summary>
    public static string GetLogFileName(string runId)
    {
        return $"backup_{runId}.log";
    }

    /// <summary>
    /// Generates the temporary remote file name for SFTP upload: <c>{FileName}.tmp</c>.
    /// </summary>
    public static string GetTempRemoteFileName(string finalFileName)
    {
        return $"{finalFileName}.tmp";
    }
}
