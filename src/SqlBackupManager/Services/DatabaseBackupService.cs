using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlBackupManager.Configuration;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;
using SqlBackupManager.Utilities;

namespace SqlBackupManager.Services;

/// <summary>
/// Executes full SQL Server database backups using Microsoft.Data.SqlClient.
/// Writes .bak files to the SQL Server host and returns both the server-side path
/// and the application-accessible UNC path.
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    private readonly SqlServerOptions _options;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly BackupErrorHandler _errorHandler;

    public DatabaseBackupService(
        IOptions<SqlServerOptions> options,
        ILogger<DatabaseBackupService> logger,
        BackupErrorHandler errorHandler)
    {
        _options = options.Value;
        _logger = logger;
        _errorHandler = errorHandler;
    }

    public async Task<DatabaseBackupResult> BackupDatabaseAsync(
        string databaseName,
        string runId,
        DateTime timestampUtc,
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var result = new DatabaseBackupResult
        {
            DatabaseName = databaseName,
            Success = false,
            Stage = BackupStage.Validation,
            StartedAtUtc = startedAtUtc
        };

        // Validate database name — only allow alphanumeric, underscore, hyphen, dot, and brackets
        if (!IsValidDatabaseName(databaseName))
        {
            result.Stage = BackupStage.Validation;
            result.ErrorMessage = $"Invalid database name: '{databaseName}'. Only alphanumeric characters, underscores, hyphens, and dots are allowed.";
            result.EndedAtUtc = DateTime.UtcNow;
            _logger.LogError("Database {DatabaseName} failed validation: invalid name", databaseName);
            return result;
        }

        // Build file paths
        var bakFileName = FileNameHelper.GetBakFileName(databaseName, runId);
        var bakPathOnServer = Path.Combine(_options.BackupDirectoryOnSqlServer, bakFileName);
        var bakPathFromApp = Path.Combine(_options.BackupDirectoryFromApp, bakFileName);

        result.BakFilePathOnServer = bakPathOnServer;
        result.BakFilePathFromApp = bakPathFromApp;

        // Validate the database exists before attempting backup
        if (!await DatabaseExistsAsync(databaseName, cancellationToken))
        {
            result.Stage = BackupStage.Validation;
            result.ErrorMessage = $"Database '{databaseName}' does not exist or is not accessible.";
            result.EndedAtUtc = DateTime.UtcNow;
            _logger.LogError("Database {DatabaseName} does not exist or is not accessible", databaseName);
            return result;
        }

        // Execute the backup
        result.Stage = BackupStage.SqlBackup;
        try
        {
            var sql = BuildBackupSql(databaseName, bakPathOnServer);
            _logger.LogInformation(
                "Starting BACKUP DATABASE for {DatabaseName} to {Path}",
                databaseName,
                bakPathOnServer);

            await ExecuteSqlAsync(sql, cancellationToken);

            _logger.LogInformation(
                "BACKUP DATABASE completed for {DatabaseName}",
                databaseName);
        }
        catch (Exception ex)
        {
            return _errorHandler.CreateFailureResult(result, BackupStage.SqlBackup, ex);
        }

        // Verify backup file is accessible and nonempty
        result.Stage = BackupStage.VerifyBackup;
        try
        {
            if (!await VerifyBackupFileAsync(databaseName, bakPathFromApp, cancellationToken))
            {
                return _errorHandler.CreateFailureResult(
                    result,
                    BackupStage.VerifyBackup,
                    $"Backup file not accessible or empty at: {bakPathFromApp}");
            }

            // Attempt RESTORE VERIFYONLY as an additional integrity check
            try
            {
                await ExecuteVerifyOnlyAsync(databaseName, bakPathOnServer, cancellationToken);
                _logger.LogInformation(
                    "RESTORE VERIFYONLY succeeded for {DatabaseName}",
                    databaseName);
            }
            catch (Exception ex)
            {
                // VerifyOnly failure is logged as a warning but does not fail the backup,
                // since the file exists and is nonempty. Note: this is not a test restore.
                _logger.LogWarning(
                    "RESTORE VERIFYONLY failed for {DatabaseName}: {Message}. Backup file exists and is nonempty; verifyonly is a secondary check, not a test restore.",
                    databaseName,
                    BackupErrorHandler.RedactSensitiveData(ex.Message));
            }

            // Record file size
            try
            {
                var fileInfo = new FileInfo(bakPathFromApp);
                result.BakFileSizeBytes = fileInfo.Length;
            }
            catch
            {
                // Size is informational; don't fail on it
            }
        }
        catch (Exception ex)
        {
            return _errorHandler.CreateFailureResult(result, BackupStage.VerifyBackup, ex);
        }

        result.Success = true;
        result.Stage = BackupStage.Completed;
        result.EndedAtUtc = DateTime.UtcNow;
        _logger.LogInformation(
            "Database {DatabaseName} backup verified: {SizeBytes} bytes",
            databaseName,
            result.BakFileSizeBytes);

        return result;
    }

    /// <summary>
    /// Validates that a database name contains only safe characters.
    /// SQL identifiers cannot be parameterized, so we must validate before use.
    /// </summary>
    private static bool IsValidDatabaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Allow alphanumeric, underscore, hyphen, dot — typical SQL database names
        // Reject anything that could be used for SQL injection in an identifier
        return Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+$");
    }

    /// <summary>
    /// Escapes a SQL identifier by wrapping in brackets and doubling any closing brackets.
    /// This is the standard SQL Server escaping for identifiers when they cannot be parameterized.
    /// </summary>
    private static string EscapeSqlIdentifier(string identifier)
    {
        // Replace ] with ]] to escape closing brackets, then wrap in [ ]
        var escaped = identifier.Replace("]", "]]");
        return $"[{escaped}]";
    }

    /// <summary>
    /// Escapes a backup file path for use in a SQL string literal.
    /// Single quotes in paths are doubled to prevent SQL injection.
    /// </summary>
    private static string EscapeSqlStringLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    /// <summary>
    /// Builds the BACKUP DATABASE SQL statement with optional COMPRESSION and CHECKSUM.
    /// </summary>
    private string BuildBackupSql(string databaseName, string bakPath)
    {
        var escapedDb = EscapeSqlIdentifier(databaseName);
        var escapedPath = EscapeSqlStringLiteral(bakPath);

        var sb = new StringBuilder();
        sb.Append("BACKUP DATABASE ").Append(escapedDb);
        sb.Append(" TO DISK = '").Append(escapedPath).Append('\'');

        sb.Append(" WITH CHECKSUM");

        if (_options.UseNativeCompression)
        {
            sb.Append(", COMPRESSION");
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Builds the RESTORE VERIFYONLY SQL statement.
    /// </summary>
    private string BuildVerifyOnlySql(string databaseName, string bakPath)
    {
        var escapedDb = EscapeSqlIdentifier(databaseName);
        var escapedPath = EscapeSqlStringLiteral(bakPath);

        return $"RESTORE VERIFYONLY FROM DISK = '{escapedPath}' WITH CHECKSUM;";
    }

    /// <summary>
    /// Builds a connection string targeting the specified database, replacing the initial catalog.
    /// </summary>
    private string GetConnectionStringForDatabase(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(_options.ConnectionString)
        {
            InitialCatalog = databaseName
        };

        // If SQL credentials are configured, use them instead of integrated security
        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            builder.UserID = _options.Username;

            var password = Environment.GetEnvironmentVariable("Sql__Password");
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "SQL authentication is configured (Username is set) but Sql__Password environment variable is not set.");
            }

            builder.Password = password;
            builder.IntegratedSecurity = false;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Checks whether the specified database exists on the SQL Server.
    /// </summary>
    private async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sys.databases WHERE name = @dbName AND state = 0";
            command.Parameters.AddWithValue("@dbName", databaseName);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is not null && result != DBNull.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to check existence of database {DatabaseName}",
                databaseName);
            return false;
        }
    }

    /// <summary>
    /// Executes a non-query SQL command (BACKUP DATABASE or RESTORE VERIFYONLY).
    /// Uses the master database context for backup operations.
    /// </summary>
    private async Task ExecuteSqlAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.CommandTimeoutSeconds;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Executes RESTORE VERIFYONLY for the specified backup file.
    /// </summary>
    private async Task ExecuteVerifyOnlyAsync(
        string databaseName,
        string bakPath,
        CancellationToken cancellationToken)
    {
        var sql = BuildVerifyOnlySql(databaseName, bakPath);
        await ExecuteSqlAsync(sql, cancellationToken);
    }

    /// <summary>
    /// Verifies that the backup file exists at the application-accessible path and is nonempty.
    /// </summary>
    private async Task<bool> VerifyBackupFileAsync(
        string databaseName,
        string bakPathFromApp,
        CancellationToken cancellationToken)
    {
        // Give the OS a moment for the file to appear on the UNC share
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (File.Exists(bakPathFromApp))
                break;

            _logger.LogDebug(
                "Waiting for backup file to appear: {Path} (attempt {Attempt})",
                bakPathFromApp,
                attempt + 1);
            await Task.Delay(1000, cancellationToken);
        }

        if (!File.Exists(bakPathFromApp))
        {
            _logger.LogError(
                "Backup file not found at application path: {Path}",
                bakPathFromApp);
            return false;
        }

        var fileInfo = new FileInfo(bakPathFromApp);
        if (fileInfo.Length == 0)
        {
            _logger.LogError(
                "Backup file is empty: {Path}",
                bakPathFromApp);
            return false;
        }

        return true;
    }
}
