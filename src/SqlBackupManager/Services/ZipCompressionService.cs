using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlBackupManager.Configuration;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;

namespace SqlBackupManager.Services;

/// <summary>
/// Creates ZIP archives from .bak files using streaming to avoid loading
/// entire files into memory. Verifies archive integrity after creation.
/// </summary>
public sealed class ZipCompressionService : ICompressionService
{
    private readonly ILogger<ZipCompressionService> _logger;
    private readonly BackupErrorHandler _errorHandler;

    public ZipCompressionService(
        ILogger<ZipCompressionService> logger,
        BackupErrorHandler errorHandler)
    {
        _logger = logger;
        _errorHandler = errorHandler;
    }

    public async Task<DatabaseBackupResult> CreateZipAsync(
        string databaseName,
        string bakFilePath,
        string zipFilePath,
        CancellationToken cancellationToken = default)
    {
        var result = new DatabaseBackupResult
        {
            DatabaseName = databaseName,
            Success = false,
            Stage = BackupStage.Zip,
            StartedAtUtc = DateTime.UtcNow
        };

        // Verify source file exists
        if (!File.Exists(bakFilePath))
        {
            return _errorHandler.CreateFailureResult(
                result,
                BackupStage.Zip,
                $"Source .bak file not found: {bakFilePath}");
        }

        // Create ZIP using streaming (no full file load into memory)
        try
        {
            _logger.LogInformation(
                "Creating ZIP for {DatabaseName}: {BakPath} -> {ZipPath}",
                databaseName,
                bakFilePath,
                zipFilePath);

            // Ensure output directory exists
            var zipDir = Path.GetDirectoryName(zipFilePath);
            if (!string.IsNullOrEmpty(zipDir) && !Directory.Exists(zipDir))
            {
                Directory.CreateDirectory(zipDir);
            }

            var sourceFileInfo = new FileInfo(bakFilePath);
            var entryName = Path.GetFileName(bakFilePath);

            // Use FileStream for both source and output to stream without loading into memory
            await using (var sourceStream = new FileStream(
                bakFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true))
            await using (var zipStream = new FileStream(
                zipFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                useAsync: true))
            {
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                await using var entryStream = entry.Open();
                await sourceStream.CopyToAsync(entryStream, 65536, cancellationToken);
            }

            result.ZipFilePath = zipFilePath;
            result.BakFileSizeBytes = sourceFileInfo.Length;

            var zipFileInfo = new FileInfo(zipFilePath);
            result.ZipFileSizeBytes = zipFileInfo.Length;

            _logger.LogInformation(
                "ZIP created for {DatabaseName}: {SizeBytes} bytes (from {BakSize} byte .bak)",
                databaseName,
                result.ZipFileSizeBytes,
                result.BakFileSizeBytes);
        }
        catch (Exception ex)
        {
            // Clean up partial ZIP if creation failed
            if (File.Exists(zipFilePath))
            {
                try { File.Delete(zipFilePath); }
                catch { /* best effort cleanup */ }
            }

            return _errorHandler.CreateFailureResult(result, BackupStage.Zip, ex);
        }

        // Verify ZIP integrity by reopening the archive and checking the entry
        result.Stage = BackupStage.VerifyZip;
        try
        {
            await using var verifyStream = new FileStream(
                zipFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true);

            using var archive = new ZipArchive(verifyStream, ZipArchiveMode.Read);
            var expectedEntryName = Path.GetFileName(bakFilePath);
            var entry = archive.GetEntry(expectedEntryName);

            if (entry is null)
            {
                return _errorHandler.CreateFailureResult(
                    result,
                    BackupStage.VerifyZip,
                    $"ZIP verification failed: expected entry '{expectedEntryName}' not found in archive.");
            }

            if (entry.Length == 0)
            {
                return _errorHandler.CreateFailureResult(
                    result,
                    BackupStage.VerifyZip,
                    $"ZIP verification failed: entry '{expectedEntryName}' is empty.");
            }

            _logger.LogInformation(
                "ZIP verified for {DatabaseName}: entry {Entry} is {SizeBytes} bytes",
                databaseName,
                expectedEntryName,
                entry.Length);
        }
        catch (Exception ex)
        {
            return _errorHandler.CreateFailureResult(result, BackupStage.VerifyZip, ex);
        }

        result.Success = true;
        result.Stage = BackupStage.Completed;
        result.EndedAtUtc = DateTime.UtcNow;
        return result;
    }
}
