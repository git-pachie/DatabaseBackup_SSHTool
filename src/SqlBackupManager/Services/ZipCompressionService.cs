using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlBackupManager.Configuration;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;

namespace SqlBackupManager.Services;

/// <summary>
/// Creates password-protected (AES-256) ZIP archives from .bak files using streaming
/// to avoid loading entire files into memory. Verifies archive integrity after creation
/// by reopening it with the password and testing every entry.
/// </summary>
public sealed class ZipCompressionService : ICompressionService
{
    private const int BufferSize = 65536;

    private readonly StorageOptions _options;
    private readonly ILogger<ZipCompressionService> _logger;
    private readonly BackupErrorHandler _errorHandler;

    public ZipCompressionService(
        IOptions<StorageOptions> options,
        ILogger<ZipCompressionService> logger,
        BackupErrorHandler errorHandler)
    {
        _options = options.Value;
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

        var password = _options.ZipPassword;
        if (string.IsNullOrEmpty(password))
        {
            return _errorHandler.CreateFailureResult(
                result,
                BackupStage.Zip,
                "ZIP password is not configured. Set Storage:ZipPassword.");
        }

        // Verify source file exists
        if (!File.Exists(bakFilePath))
        {
            return _errorHandler.CreateFailureResult(
                result,
                BackupStage.Zip,
                $"Source .bak file not found: {bakFilePath}");
        }

        var entryName = Path.GetFileName(bakFilePath);

        // Create ZIP using streaming (no full file load into memory)
        try
        {
            _logger.LogInformation(
                "Creating password-protected ZIP for {DatabaseName}: {BakPath} -> {ZipPath}",
                databaseName,
                bakFilePath,
                zipFilePath);

            var zipDir = Path.GetDirectoryName(zipFilePath);
            if (!string.IsNullOrEmpty(zipDir) && !Directory.Exists(zipDir))
            {
                Directory.CreateDirectory(zipDir);
            }

            var sourceFileInfo = new FileInfo(bakFilePath);

            await using (var sourceStream = new FileStream(
                bakFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: BufferSize,
                useAsync: true))
            await using (var zipStream = new FileStream(
                zipFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: BufferSize,
                useAsync: true))
            {
                using var zipOut = new ZipOutputStream(zipStream)
                {
                    Password = password,
                    // Backups can exceed 4 GB; always write Zip64 structures
                    UseZip64 = UseZip64.On,
                    IsStreamOwner = false
                };
                zipOut.SetLevel(6);

                var entry = new ZipEntry(entryName)
                {
                    DateTime = DateTime.Now,
                    AESKeySize = 256
                };

                zipOut.PutNextEntry(entry);
                await sourceStream.CopyToAsync(zipOut, BufferSize, cancellationToken);
                zipOut.CloseEntry();
                zipOut.Finish();
            }

            result.ZipFilePath = zipFilePath;
            result.BakFileSizeBytes = sourceFileInfo.Length;
            result.ZipFileSizeBytes = new FileInfo(zipFilePath).Length;

            _logger.LogInformation(
                "ZIP created for {DatabaseName}: {SizeBytes} bytes (from {BakSize} byte .bak)",
                databaseName,
                result.ZipFileSizeBytes,
                result.BakFileSizeBytes);
        }
        catch (Exception ex)
        {
            // Remove a partial ZIP so it cannot be mistaken for a valid one
            if (File.Exists(zipFilePath))
            {
                try { File.Delete(zipFilePath); }
                catch { /* best effort cleanup */ }
            }

            return _errorHandler.CreateFailureResult(result, BackupStage.Zip, ex);
        }

        // Verify ZIP integrity: reopen with the password, check the entry, and test every CRC
        result.Stage = BackupStage.VerifyZip;
        try
        {
            using var zipFile = new ZipFile(zipFilePath) { Password = password };

            var entry = zipFile.GetEntry(entryName);
            if (entry is null)
            {
                return _errorHandler.CreateFailureResult(
                    result,
                    BackupStage.VerifyZip,
                    $"ZIP verification failed: expected entry '{entryName}' not found in archive.");
            }

            if (entry.Size == 0)
            {
                return _errorHandler.CreateFailureResult(
                    result,
                    BackupStage.VerifyZip,
                    $"ZIP verification failed: entry '{entryName}' is empty.");
            }

            if (!entry.IsCrypted || entry.AESKeySize != 256)
            {
                return _errorHandler.CreateFailureResult(
                    result,
                    BackupStage.VerifyZip,
                    $"ZIP verification failed: entry '{entryName}' is not AES-256 encrypted.");
            }

            if (!zipFile.TestArchive(testData: true))
            {
                return _errorHandler.CreateFailureResult(
                    result,
                    BackupStage.VerifyZip,
                    "ZIP verification failed: archive data test did not pass (wrong password or corrupt data).");
            }

            _logger.LogInformation(
                "ZIP verified for {DatabaseName}: entry {Entry} is {SizeBytes} bytes, AES-256 encrypted",
                databaseName,
                entryName,
                entry.Size);
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
