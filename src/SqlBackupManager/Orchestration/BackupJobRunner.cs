using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlBackupManager.Configuration;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;
using SqlBackupManager.Utilities;

namespace SqlBackupManager.Orchestration;

/// <summary>
/// Coordinates the per-database backup pipeline: backup → ZIP → SFTP upload.
/// Continues with remaining databases after an individual failure.
/// Aggregates results and sends one consolidated email summary.
/// </summary>
public sealed class BackupJobRunner : IBackupJobRunner
{
    private readonly SqlServerOptions _sqlOptions;
    private readonly StorageOptions _storageOptions;
    private readonly IDatabaseBackupService _backupService;
    private readonly ICompressionService _compressionService;
    private readonly ISftpService _sftpService;
    private readonly IEmailService _emailService;
    private readonly ILogger<BackupJobRunner> _logger;

    public BackupJobRunner(
        IOptions<SqlServerOptions> sqlOptions,
        IOptions<StorageOptions> storageOptions,
        IDatabaseBackupService backupService,
        ICompressionService compressionService,
        ISftpService sftpService,
        IEmailService emailService,
        ILogger<BackupJobRunner> logger)
    {
        _sqlOptions = sqlOptions.Value;
        _storageOptions = storageOptions.Value;
        _backupService = backupService;
        _compressionService = compressionService;
        _sftpService = sftpService;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<BackupRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var runId = FileNameHelper.GenerateRunId(startedAtUtc);

        _logger.LogInformation(
            "=== Backup Run {RunId} started at {Timestamp:O} ===",
            runId,
            startedAtUtc);

        var runResult = new BackupRunResult
        {
            RunId = runId,
            StartedAtUtc = startedAtUtc
        };

        // Validate that databases are configured
        if (_sqlOptions.Databases.Count == 0)
        {
            _logger.LogError("No databases configured for backup. Aborting run.");
            runResult.EndedAtUtc = DateTime.UtcNow;
            runResult.EmailError = "No databases were configured for backup.";
            return runResult;
        }

        // Process each database sequentially
        foreach (var databaseName in _sqlOptions.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "--- Processing database: {DatabaseName} ---",
                databaseName);

            var dbResult = await ProcessDatabaseAsync(databaseName, runId, startedAtUtc, cancellationToken);

            runResult.DatabaseResults.Add(dbResult);

            if (dbResult.Success)
            {
                _logger.LogInformation(
                    "Database {DatabaseName} completed successfully in {Elapsed}",
                    databaseName,
                    dbResult.Elapsed);
            }
            else
            {
                _logger.LogWarning(
                    "Database {DatabaseName} failed at stage {Stage}: {Error}. Continuing to next database.",
                    databaseName,
                    dbResult.Stage,
                    dbResult.ErrorMessage);
            }
        }

        runResult.EndedAtUtc = DateTime.UtcNow;

        _logger.LogInformation(
            "=== Backup Run {RunId} completed: {Success} succeeded, {Failed} failed, elapsed {Elapsed} ===",
            runId,
            runResult.SuccessCount,
            runResult.FailureCount,
            runResult.Elapsed);

        // Send consolidated email summary (failures here don't erase backup results)
        try
        {
            runResult.EmailSent = await _emailService.SendSummaryEmailAsync(runResult, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email sending threw an unexpected exception: {Message}", ex.Message);
            runResult.EmailSent = false;
            runResult.EmailError = ex.Message;
        }

        return runResult;
    }

    /// <summary>
    /// Processes a single database through the full pipeline: backup → ZIP → SFTP upload.
    /// Continues on the next database if this one fails at any stage.
    /// </summary>
    private async Task<DatabaseBackupResult> ProcessDatabaseAsync(
        string databaseName,
        string runId,
        DateTime runTimestampUtc,
        CancellationToken cancellationToken)
    {
        var dbResult = new DatabaseBackupResult
        {
            DatabaseName = databaseName,
            Success = false,
            Stage = BackupStage.Validation,
            StartedAtUtc = DateTime.UtcNow
        };

        // Stage 1: SQL Server backup
        var backupResult = await _backupService.BackupDatabaseAsync(
            databaseName, runId, runTimestampUtc, cancellationToken);

        if (!backupResult.Success)
        {
            dbResult.Stage = backupResult.Stage;
            dbResult.ErrorMessage = backupResult.ErrorMessage;
            dbResult.BakFilePathOnServer = backupResult.BakFilePathOnServer;
            dbResult.BakFilePathFromApp = backupResult.BakFilePathFromApp;
            dbResult.EndedAtUtc = DateTime.UtcNow;
            return dbResult;
        }

        // Copy data from backup result to our tracking result
        dbResult.BakFilePathOnServer = backupResult.BakFilePathOnServer;
        dbResult.BakFilePathFromApp = backupResult.BakFilePathFromApp;
        dbResult.BakFileSizeBytes = backupResult.BakFileSizeBytes;
        dbResult.Stage = BackupStage.Zip;

        // Stage 2: Create ZIP
        var zipFileName = FileNameHelper.GetZipFileName(databaseName, runId);
        var zipFilePath = Path.Combine(_storageOptions.LocalZipDirectory, zipFileName);

        var zipResult = await _compressionService.CreateZipAsync(
            databaseName, backupResult.BakFilePathFromApp!, zipFilePath, cancellationToken);

        if (!zipResult.Success)
        {
            dbResult.Stage = zipResult.Stage;
            dbResult.ErrorMessage = zipResult.ErrorMessage;
            dbResult.ZipFilePath = zipResult.ZipFilePath;
            dbResult.EndedAtUtc = DateTime.UtcNow;
            return dbResult;
        }

        dbResult.ZipFilePath = zipResult.ZipFilePath;
        dbResult.ZipFileSizeBytes = zipResult.ZipFileSizeBytes;
        dbResult.Stage = BackupStage.SftpUpload;

        // Stage 3: SFTP upload
        var sftpResult = await _sftpService.UploadAsync(
            databaseName, zipFilePath, cancellationToken);

        if (!sftpResult.Success)
        {
            dbResult.Stage = sftpResult.Stage;
            dbResult.ErrorMessage = sftpResult.ErrorMessage;
            dbResult.RemoteZipPath = sftpResult.RemoteZipPath;
            dbResult.EndedAtUtc = DateTime.UtcNow;
            return dbResult;
        }

        // All stages completed successfully
        dbResult.RemoteZipPath = sftpResult.RemoteZipPath;
        dbResult.Success = true;
        dbResult.Stage = BackupStage.Completed;
        dbResult.EndedAtUtc = DateTime.UtcNow;

        return dbResult;
    }
}
