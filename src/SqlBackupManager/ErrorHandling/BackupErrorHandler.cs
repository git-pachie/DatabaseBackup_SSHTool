using System.Text;
using Microsoft.Extensions.Logging;
using SqlBackupManager.Models;

namespace SqlBackupManager.ErrorHandling;

/// <summary>
/// Centralizes safe exception formatting and structured error logging.
/// Ensures credentials, passwords, private keys, and connection strings are never
/// included in log messages or error output.
/// </summary>
public sealed class BackupErrorHandler
{
    private readonly ILogger<BackupErrorHandler> _logger;

    /// <summary>
    /// Substrings that must be redacted from any error message before logging or reporting.
    /// Only include forms with a delimiter (e.g., "Password=") to avoid double-redaction.
    /// PEM key blocks are handled separately by regex.
    /// </summary>
    private static readonly string[] SensitiveSubstrings =
    [
        "Password=",
        "Pwd=",
        "token=",
        "Token=",
        "secret=",
        "Secret="
    ];

    public BackupErrorHandler(ILogger<BackupErrorHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a failure <see cref="DatabaseBackupResult"/> from an exception, logging
    /// the full (redacted) details once at the specified stage.
    /// </summary>
    /// <param name="databaseName">Database being processed when the error occurred.</param>
    /// <param name="stage">Stage where the failure occurred.</param>
    /// <param name="ex">Exception that caused the failure.</param>
    /// <param name="context">Optional additional context (paths, etc.) to include in the result.</param>
    /// <returns>A populated failure result.</returns>
    public DatabaseBackupResult CreateFailureResult(
        string databaseName,
        BackupStage stage,
        Exception ex,
        string? context = null)
    {
        var safeMessage = RedactSensitiveData(ex.Message);
        var safeDetails = ex.ToString();

        // Log full exception details (redacted) exactly once
        _logger.LogError(ex,
            "Database {DatabaseName} failed at stage {Stage}: {Message}{Context}",
            databaseName,
            stage,
            safeMessage,
            context is not null ? $" | Context: {context}" : string.Empty);

        var result = new DatabaseBackupResult
        {
            DatabaseName = databaseName,
            Success = false,
            Stage = stage,
            ErrorMessage = safeMessage,
            StartedAtUtc = DateTime.UtcNow,
            EndedAtUtc = DateTime.UtcNow
        };

        return result;
    }

    /// <summary>
    /// Creates a failure result from a plain message (no exception), logging it once.
    /// </summary>
    public DatabaseBackupResult CreateFailureResult(
        string databaseName,
        BackupStage stage,
        string message)
    {
        var safeMessage = RedactSensitiveData(message);

        _logger.LogError(
            "Database {DatabaseName} failed at stage {Stage}: {Message}",
            databaseName,
            stage,
            safeMessage);

        return new DatabaseBackupResult
        {
            DatabaseName = databaseName,
            Success = false,
            Stage = stage,
            ErrorMessage = safeMessage,
            StartedAtUtc = DateTime.UtcNow,
            EndedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a failure result from a plain message (no exception), preserving timing
    /// information from an in-progress result. Logs the message once.
    /// </summary>
    public DatabaseBackupResult CreateFailureResult(
        DatabaseBackupResult inProgress,
        BackupStage stage,
        string message)
    {
        var safeMessage = RedactSensitiveData(message);

        _logger.LogError(
            "Database {DatabaseName} failed at stage {Stage}: {Message}",
            inProgress.DatabaseName,
            stage,
            safeMessage);

        inProgress.Success = false;
        inProgress.Stage = stage;
        inProgress.ErrorMessage = safeMessage;
        inProgress.EndedAtUtc = DateTime.UtcNow;

        return inProgress;
    }

    /// <summary>
    /// Creates a failure result that preserves timing information from an in-progress result,
    /// logging the exception once at the specified stage.
    /// </summary>
    public DatabaseBackupResult CreateFailureResult(
        DatabaseBackupResult inProgress,
        BackupStage stage,
        Exception ex,
        string? context = null)
    {
        var safeMessage = RedactSensitiveData(ex.Message);

        _logger.LogError(ex,
            "Database {DatabaseName} failed at stage {Stage}: {Message}{Context}",
            inProgress.DatabaseName,
            stage,
            safeMessage,
            context is not null ? $" | Context: {context}" : string.Empty);

        inProgress.Success = false;
        inProgress.Stage = stage;
        inProgress.ErrorMessage = safeMessage;
        inProgress.EndedAtUtc = DateTime.UtcNow;

        return inProgress;
    }

    /// <summary>
    /// Redacts sensitive data (passwords, private keys, tokens, connection string secrets)
    /// from a message string to ensure it's safe for logs and email summaries.
    /// </summary>
    public static string RedactSensitiveData(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var result = new StringBuilder(input);

        foreach (var sensitive in SensitiveSubstrings)
        {
            // Redact the value following a sensitive key (e.g., Password=secret -> Password=***)
            var idx = 0;
            while ((idx = result.ToString().IndexOf(sensitive, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var valueStart = idx + sensitive.Length;
                // Find the end of the value (next semicolon, space, quote, or end of string)
                var valueEnd = valueStart;
                while (valueEnd < result.Length &&
                       result[valueEnd] != ';' &&
                       result[valueEnd] != ' ' &&
                       result[valueEnd] != '"' &&
                       result[valueEnd] != '\r' &&
                       result[valueEnd] != '\n')
                {
                    valueEnd++;
                }

                if (valueEnd > valueStart)
                {
                    result.Remove(valueStart, valueEnd - valueStart);
                    result.Insert(valueStart, "***REDACTED***");
                }

                idx = valueStart + "***REDACTED***".Length;
            }
        }

        // Also redact anything that looks like a PEM key block
        var pemPattern = new System.Text.RegularExpressions.Regex(
            @"-----BEGIN[A-Z\s]+-----[\s\S]*?-----END[A-Z\s]+-----",
            System.Text.RegularExpressions.RegexOptions.None);

        var redacted = pemPattern.Replace(result.ToString(), "***PEM KEY REDACTED***");

        return redacted;
    }

    /// <summary>
    /// Formats an exception for safe display, redacting sensitive data.
    /// </summary>
    public static string FormatException(Exception ex)
    {
        return RedactSensitiveData(ex.ToString());
    }
}
