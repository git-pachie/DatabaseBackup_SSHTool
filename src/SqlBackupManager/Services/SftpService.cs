using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using SqlBackupManager.Configuration;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;
using SqlBackupManager.Utilities;

namespace SqlBackupManager.Services;

/// <summary>
/// Uploads ZIP files to a configured SFTP server using SSH.NET.
/// Verifies the SSH host key fingerprint via the HostKeyReceived event,
/// uses a temp name during upload, checks uploaded size, and renames to the final name.
/// </summary>
public sealed class SftpService : ISftpService
{
    private readonly SftpOptions _options;
    private readonly ILogger<SftpService> _logger;
    private readonly BackupErrorHandler _errorHandler;

    public SftpService(
        IOptions<SftpOptions> options,
        ILogger<SftpService> logger,
        BackupErrorHandler errorHandler)
    {
        _options = options.Value;
        _logger = logger;
        _errorHandler = errorHandler;
    }

    public async Task<DatabaseBackupResult> UploadAsync(
        string databaseName,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        var result = new DatabaseBackupResult
        {
            DatabaseName = databaseName,
            Success = false,
            Stage = BackupStage.SftpUpload,
            StartedAtUtc = DateTime.UtcNow
        };

        // Validate local file exists
        if (!File.Exists(localFilePath))
        {
            return _errorHandler.CreateFailureResult(
                result,
                BackupStage.SftpUpload,
                $"Local ZIP file not found for SFTP upload: {localFilePath}");
        }

        var localFileSize = new FileInfo(localFilePath).Length;
        result.ZipFilePath = localFilePath;
        result.ZipFileSizeBytes = localFileSize;

        // Validate configuration before connecting
        var validationError = ValidateConfiguration();
        if (validationError is not null)
        {
            return _errorHandler.CreateFailureResult(result, BackupStage.SftpUpload, validationError);
        }

        var finalFileName = Path.GetFileName(localFilePath);
        var tempFileName = FileNameHelper.GetTempRemoteFileName(finalFileName);
        var remoteTempPath = $"{NormalizeRemoteDir(_options.RemoteDirectory)}{tempFileName}";
        var remoteFinalPath = $"{NormalizeRemoteDir(_options.RemoteDirectory)}{finalFileName}";
        result.RemoteZipPath = remoteFinalPath;

        SftpClient? sftp = null;

        try
        {
            sftp = CreateClient();

            // Set up host key verification via the HostKeyReceived event.
            // This fires during Connect() and allows us to accept or reject the key.
            var hostKeyVerified = false;
            sftp.HostKeyReceived += (_, e) =>
            {
                var actualFp = e.FingerPrintSHA256;
                _logger.LogInformation(
                    "SFTP server host key fingerprint (SHA256): {Fingerprint}",
                    actualFp);

                if (IsFingerprintMatch(actualFp, _options.HostKeyFingerprint))
                {
                    hostKeyVerified = true;
                    e.CanTrust = true;
                }
                else
                {
                    hostKeyVerified = false;
                    e.CanTrust = false;
                }
            };

            _logger.LogInformation(
                "Connecting to SFTP {Host}:{Port} as {Username}",
                _options.Host,
                _options.Port,
                _options.Username);

            sftp.Connect();

            if (!hostKeyVerified)
            {
                throw new SshConnectionException(
                    "SSH host key fingerprint mismatch. The server presented a fingerprint that does not match " +
                    "the configured HostKeyFingerprint. Check the log for the server's actual fingerprint (SHA256) " +
                    "and update the 'Sftp:HostKeyFingerprint' secret to match. " +
                    "You can include or omit the 'SHA256:' prefix — both formats are accepted.");
            }

            _logger.LogInformation(
                "SFTP host key verified for {Host}",
                _options.Host);

            // Ensure remote directory exists or fail with a clear message
            EnsureRemoteDirectory(sftp);

            // Upload to temp name
            _logger.LogInformation(
                "Uploading {LocalPath} to {RemotePath} (temp)",
                localFilePath,
                remoteTempPath);

            // UploadFile is synchronous; run on a background thread to allow cancellation
            await Task.Run(() =>
            {
                using var fileStream = new FileStream(
                    localFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 65536,
                    useAsync: false);

                var totalBytes = fileStream.Length;
                var uploadedBytes = 0UL;

                sftp.UploadFile(fileStream, remoteTempPath, uploadCallback: (bytes) =>
                {
                    uploadedBytes += bytes;
                    if (uploadedBytes % (10 * 1024 * 1024) == 0) // Log every ~10MB
                    {
                        _logger.LogDebug(
                            "SFTP upload progress: {Uploaded} / {Total} bytes ({Percent:P1})",
                            uploadedBytes,
                            totalBytes,
                            (double)uploadedBytes / totalBytes);
                    }
                });

                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken);

            // Verify uploaded file size
            if (!sftp.Exists(remoteTempPath))
                throw new InvalidOperationException($"Remote file not found after upload: {remoteTempPath}");

            var remoteSize = sftp.GetAttributes(remoteTempPath).Size;
            if (remoteSize != localFileSize)
            {
                throw new InvalidOperationException(
                    $"SFTP upload size mismatch: local file is {localFileSize} bytes but remote file is {remoteSize} bytes.");
            }

            _logger.LogInformation(
                "SFTP upload verified: {SizeBytes} bytes for {DatabaseName}",
                remoteSize,
                databaseName);

            // Rename temp to final
            // If final file already exists (from a previous run), delete it first
            if (sftp.Exists(remoteFinalPath))
            {
                sftp.DeleteFile(remoteFinalPath);
            }

            sftp.RenameFile(remoteTempPath, remoteFinalPath);

            _logger.LogInformation(
                "SFTP upload completed for {DatabaseName}: {RemotePath}",
                databaseName,
                remoteFinalPath);
        }
        catch (Exception ex)
        {
            // Attempt to clean up the temp file on the remote server
            if (sftp is { IsConnected: true })
            {
                try { sftp.DeleteFile(remoteTempPath); }
                catch { /* best effort cleanup; do not mask original error */ }
            }

            return _errorHandler.CreateFailureResult(result, BackupStage.SftpUpload, ex);
        }
        finally
        {
            sftp?.Dispose();
        }

        result.Success = true;
        result.Stage = BackupStage.Completed;
        result.EndedAtUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// Creates and configures the SftpClient with the appropriate authentication method.
    /// Does not connect — caller must call Connect().
    /// </summary>
    private SftpClient CreateClient()
    {
        var connectionInfo = new ConnectionInfo(
            _options.Host,
            _options.Port,
            _options.Username,
            GetAuthenticationMethods());

        return new SftpClient(connectionInfo);
    }

    /// <summary>
    /// Builds the authentication method based on configuration.
    /// Key-based auth when PrivateKeyPath is set; password-based otherwise.
    /// Does not fall back to password on key failure — selection is config-driven.
    /// </summary>
    private AuthenticationMethod[] GetAuthenticationMethods()
    {
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
        {
            // Key-based authentication
            if (!File.Exists(_options.PrivateKeyPath))
            {
                throw new FileNotFoundException(
                    $"SFTP private key file not found: {_options.PrivateKeyPath}");
            }

            var passphrase = _options.KeyPassphrase;
            var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(_options.PrivateKeyPath)
                : new PrivateKeyFile(_options.PrivateKeyPath, passphrase);

            _logger.LogInformation(
                "Using SSH private key authentication from {KeyPath}",
                _options.PrivateKeyPath);

            return [new PrivateKeyAuthenticationMethod(_options.Username, keyFile)];
        }
        else
        {
            // Password-based authentication
            var password = _options.Password;
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "SFTP password authentication is configured but Sftp:Password is not set in user secrets.");
            }

            _logger.LogInformation("Using SFTP password authentication for {Username}", _options.Username);

            return [new PasswordAuthenticationMethod(_options.Username, password)];
        }
    }

    /// <summary>
    /// Validates that all required SFTP settings are present.
    /// </summary>
    private string? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
            return "SFTP host is not configured.";

        if (string.IsNullOrWhiteSpace(_options.Username))
            return "SFTP username is not configured.";

        if (string.IsNullOrWhiteSpace(_options.HostKeyFingerprint))
            return "SFTP host key fingerprint is not configured. Host-key verification is required and cannot be disabled.";

        if (string.IsNullOrWhiteSpace(_options.RemoteDirectory))
            return "SFTP remote directory is not configured.";

        // Validate that the chosen auth method has its required secrets
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
        {
            if (!File.Exists(_options.PrivateKeyPath))
                return $"SFTP private key file not found: {_options.PrivateKeyPath}";
        }
        else
        {
            if (string.IsNullOrEmpty(_options.Password))
                return "SFTP password not set. Add 'Sftp:Password' to user secrets.";
        }

        return null;
    }

    /// <summary>
    /// Normalizes a remote directory path to ensure it ends with a forward slash.
    /// </summary>
    private static string NormalizeRemoteDir(string dir)
    {
        if (string.IsNullOrEmpty(dir))
            return "/";

        dir = dir.Replace('\\', '/');
        if (!dir.EndsWith('/'))
            dir += '/';

        return dir;
    }

    /// <summary>
    /// Compares the actual host key fingerprint against the expected fingerprint.
    /// Both are normalized by stripping the "SHA256:" prefix (if present) and
    /// compared case-insensitively.
    /// </summary>
    private static bool IsFingerprintMatch(string? actual, string expected)
    {
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected))
            return false;

        // Strip the "SHA256:" prefix from both values — SSH.NET's FingerPrintSHA256
        // does not include it, but users often configure it (e.g. from ssh-keyscan)
        var normalizedActual = NormalizeFingerprint(actual);
        var normalizedExpected = NormalizeFingerprint(expected);

        return string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips the "SHA256:" prefix and trims whitespace from a fingerprint string.
    /// </summary>
    private static string NormalizeFingerprint(string fingerprint)
    {
        var result = fingerprint.Trim();
        if (result.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            result = result["SHA256:".Length..];

        return result;
    }

    /// <summary>
    /// Ensures the remote directory exists. Creates it if possible, or fails with a clear message
    /// if not authorized to create it.
    /// </summary>
    private void EnsureRemoteDirectory(SftpClient sftp)
    {
        var remoteDir = NormalizeRemoteDir(_options.RemoteDirectory);

        if (sftp.Exists(remoteDir))
            return;

        // Attempt to create the directory
        try
        {
            CreateRemoteDirectoryRecursive(sftp, remoteDir);
            _logger.LogInformation("Created remote directory: {RemoteDir}", remoteDir);
        }
        catch (Exception ex)
        {
            throw new SftpPermissionDeniedException(
                $"Remote directory '{remoteDir}' does not exist and could not be created. " +
                $"Ensure the SFTP user has permission to create directories. Error: {BackupErrorHandler.RedactSensitiveData(ex.Message)}",
                ex);
        }
    }

    /// <summary>
    /// Recursively creates remote directories if they don't exist.
    /// </summary>
    private void CreateRemoteDirectoryRecursive(SftpClient sftp, string remoteDir)
    {
        remoteDir = remoteDir.TrimEnd('/');
        if (string.IsNullOrEmpty(remoteDir) || remoteDir == "/")
            return;

        var lastSlash = remoteDir.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var parent = remoteDir[..lastSlash];
            if (!string.IsNullOrEmpty(parent) && parent != "/" && !sftp.Exists(parent))
            {
                CreateRemoteDirectoryRecursive(sftp, parent);
            }
        }

        if (!sftp.Exists(remoteDir))
        {
            sftp.CreateDirectory(remoteDir);
        }
    }
}
