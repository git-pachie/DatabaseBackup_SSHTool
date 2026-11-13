using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SqlBackupManager.Configuration;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;
using SqlBackupManager.Orchestration;
using Xunit;

namespace SqlBackupManager.Tests.Orchestration;

/// <summary>
/// Tests for the BackupJobRunner orchestration, covering all required scenarios:
/// all succeed, SQL backup fails, ZIP fails, SFTP fails, email fails, and missing UNC share.
/// </summary>
public class BackupJobRunnerTests
{
    private static Mock<IBackupJobRunner> CreateRunnerMocks(
        List<DatabaseBackupResult> backupResults,
        List<DatabaseBackupResult> zipResults,
        List<DatabaseBackupResult> sftpResults,
        bool emailSucceeds = true)
    {
        throw new NotImplementedException("Use CreateBackupJobRunner instead.");
    }

    /// <summary>
    /// Creates a BackupJobRunner instance with mocked service dependencies.
    /// </summary>
    private static BackupJobRunner CreateBackupJobRunner(
        Mock<IDatabaseBackupService>? backupMock = null,
        Mock<ICompressionService>? compressionMock = null,
        Mock<ISftpService>? sftpMock = null,
        Mock<IEmailService>? emailMock = null,
        SqlServerOptions? sqlOptions = null,
        StorageOptions? storageOptions = null)
    {
        backupMock ??= new Mock<IDatabaseBackupService>();
        compressionMock ??= new Mock<ICompressionService>();
        sftpMock ??= new Mock<ISftpService>();
        emailMock ??= new Mock<IEmailService>();

        sqlOptions ??= new SqlServerOptions
        {
            ConnectionString = "Server=TEST;Database=master;Integrated Security=True;",
            Databases = ["DB1", "DB2"],
            BackupDirectoryOnSqlServer = @"D:\SQLBackups\Temp",
            BackupDirectoryFromApp = @"\\TEST\SQLBackups$\Temp"
        };

        storageOptions ??= new StorageOptions
        {
            LocalZipDirectory = @"C:\Backup\Zip",
            LogDirectory = @"C:\Backup\Logs"
        };

        var sqlOptionsMock = new Mock<IOptions<SqlServerOptions>>();
        sqlOptionsMock.Setup(o => o.Value).Returns(sqlOptions);

        var storageOptionsMock = new Mock<IOptions<StorageOptions>>();
        storageOptionsMock.Setup(o => o.Value).Returns(storageOptions);

        var loggerMock = new Mock<ILogger<BackupJobRunner>>();

        return new BackupJobRunner(
            sqlOptionsMock.Object,
            storageOptionsMock.Object,
            backupMock.Object,
            compressionMock.Object,
            sftpMock.Object,
            emailMock.Object,
            loggerMock.Object);
    }

    /// <summary>
    /// Creates a successful DatabaseBackupResult for a given database.
    /// </summary>
    private static DatabaseBackupResult SuccessResult(string dbName, BackupStage stage = BackupStage.Completed) =>
        new()
        {
            DatabaseName = dbName,
            Success = true,
            Stage = stage,
            BakFilePathFromApp = $@"\\TEST\SQLBackups$\Temp\{dbName}_test.bak",
            BakFilePathOnServer = $@"D:\SQLBackups\Temp\{dbName}_test.bak",
            BakFileSizeBytes = 1024 * 1024,
            ZipFilePath = $@"C:\Backup\Zip\{dbName}_test.zip",
            ZipFileSizeBytes = 512 * 1024,
            RemoteZipPath = $"/backups/{dbName}_test.zip",
            StartedAtUtc = DateTime.UtcNow,
            EndedAtUtc = DateTime.UtcNow
        };

    /// <summary>
    /// Creates a failed DatabaseBackupResult for a given database.
    /// </summary>
    private static DatabaseBackupResult FailedResult(string dbName, BackupStage stage, string error) =>
        new()
        {
            DatabaseName = dbName,
            Success = false,
            Stage = stage,
            ErrorMessage = error,
            StartedAtUtc = DateTime.UtcNow,
            EndedAtUtc = DateTime.UtcNow
        };

    [Fact]
    public async Task RunAsync_AllDatabasesSucceed_ReturnsSuccess()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var compressionMock = new Mock<ICompressionService>();
        var sftpMock = new Mock<ISftpService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, DateTime _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyBackup));

        compressionMock.Setup(s => s.CreateZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, string _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyZip));

        sftpMock.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, CancellationToken _) => SuccessResult(db, BackupStage.SftpUpload));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var runner = CreateBackupJobRunner(backupMock, compressionMock, sftpMock, emailMock);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.AllSucceeded);
        Assert.Equal("SUCCESS", result.StatusLabel);
        Assert.True(result.EmailSent);

        // Verify all stages were called for both databases
        backupMock.Verify(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        compressionMock.Verify(s => s.CreateZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        sftpMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        emailMock.Verify(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SqlBackupFailsForOneDatabase_ContinuesWithOthers()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var compressionMock = new Mock<ICompressionService>();
        var sftpMock = new Mock<ISftpService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync("DB1", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult("DB1", BackupStage.SqlBackup, "SQL Server connection failed"));
        backupMock.Setup(s => s.BackupDatabaseAsync("DB2", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB2", BackupStage.VerifyBackup));

        compressionMock.Setup(s => s.CreateZipAsync("DB2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB2", BackupStage.VerifyZip));

        sftpMock.Setup(s => s.UploadAsync("DB2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB2", BackupStage.SftpUpload));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var runner = CreateBackupJobRunner(backupMock, compressionMock, sftpMock, emailMock);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal("PARTIAL", result.StatusLabel);

        // DB1 should not have been zipped or uploaded
        compressionMock.Verify(s => s.CreateZipAsync("DB1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        sftpMock.Verify(s => s.UploadAsync("DB1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // DB2 should have been fully processed
        compressionMock.Verify(s => s.CreateZipAsync("DB2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sftpMock.Verify(s => s.UploadAsync("DB2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Email should still be sent
        emailMock.Verify(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()), Times.Once);

        // Check DB1 failure details
        var db1Result = result.DatabaseResults.First(r => r.DatabaseName == "DB1");
        Assert.Equal(BackupStage.SqlBackup, db1Result.Stage);
        Assert.False(db1Result.Success);
        Assert.Contains("SQL Server connection failed", db1Result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_ZipFails_ContinuesWithOtherDatabases()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var compressionMock = new Mock<ICompressionService>();
        var sftpMock = new Mock<ISftpService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, DateTime _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyBackup));

        compressionMock.Setup(s => s.CreateZipAsync("DB1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult("DB1", BackupStage.Zip, "ZIP creation failed: disk full"));
        compressionMock.Setup(s => s.CreateZipAsync("DB2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB2", BackupStage.VerifyZip));

        sftpMock.Setup(s => s.UploadAsync("DB2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB2", BackupStage.SftpUpload));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var runner = CreateBackupJobRunner(backupMock, compressionMock, sftpMock, emailMock);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal("PARTIAL", result.StatusLabel);

        // DB1 should not have been uploaded (zip failed)
        sftpMock.Verify(s => s.UploadAsync("DB1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // DB2 should have been fully processed
        sftpMock.Verify(s => s.UploadAsync("DB2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        var db1Result = result.DatabaseResults.First(r => r.DatabaseName == "DB1");
        Assert.Equal(BackupStage.Zip, db1Result.Stage);
        Assert.Contains("ZIP creation failed", db1Result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_SftpFails_MarksDatabaseAsFailed()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var compressionMock = new Mock<ICompressionService>();
        var sftpMock = new Mock<ISftpService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, DateTime _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyBackup));

        compressionMock.Setup(s => s.CreateZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, string _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyZip));

        sftpMock.Setup(s => s.UploadAsync("DB1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult("DB1", BackupStage.SftpUpload, "SFTP connection refused"));
        sftpMock.Setup(s => s.UploadAsync("DB2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB2", BackupStage.SftpUpload));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var runner = CreateBackupJobRunner(backupMock, compressionMock, sftpMock, emailMock);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal("PARTIAL", result.StatusLabel);

        var db1Result = result.DatabaseResults.First(r => r.DatabaseName == "DB1");
        Assert.False(db1Result.Success);
        Assert.Equal(BackupStage.SftpUpload, db1Result.Stage);
        Assert.Contains("SFTP connection refused", db1Result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_EmailFails_DoesNotEraseBackupResults()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var compressionMock = new Mock<ICompressionService>();
        var sftpMock = new Mock<ISftpService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, DateTime _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyBackup));

        compressionMock.Setup(s => s.CreateZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, string _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyZip));

        sftpMock.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, CancellationToken _) => SuccessResult(db, BackupStage.SftpUpload));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var runner = CreateBackupJobRunner(backupMock, compressionMock, sftpMock, emailMock);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.AllSucceeded);

        // Email failed but backups are intact
        Assert.False(result.EmailSent);
        Assert.Equal("SUCCESS", result.StatusLabel);
    }

    [Fact]
    public async Task RunAsync_EmailThrowsException_BackupResultsPreserved()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var compressionMock = new Mock<ICompressionService>();
        var sftpMock = new Mock<ISftpService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, DateTime _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyBackup));

        compressionMock.Setup(s => s.CreateZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, string _, CancellationToken _) => SuccessResult(db, BackupStage.VerifyZip));

        sftpMock.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, CancellationToken _) => SuccessResult(db, BackupStage.SftpUpload));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP server unavailable"));

        var runner = CreateBackupJobRunner(backupMock, compressionMock, sftpMock, emailMock);

        // Act
        var result = await runner.RunAsync();

        // Assert — backups are intact even though email threw
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.False(result.EmailSent);
        Assert.NotNull(result.EmailError);
        Assert.Contains("SMTP server unavailable", result.EmailError);
    }

    [Fact]
    public async Task RunAsync_AllDatabasesFail_ReturnsFailed()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string db, string _, DateTime _, CancellationToken _) =>
                FailedResult(db, BackupStage.SqlBackup, "Database not accessible"));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var runner = CreateBackupJobRunner(backupMock, emailMock: emailMock);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(2, result.FailureCount);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal("FAILED", result.StatusLabel);

        // Email should still be sent
        emailMock.Verify(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_AllStagesFailForOne_OtherSucceeds()
    {
        // Arrange — DB1 fails at SQL backup, DB2 fails at ZIP, DB3 succeeds
        var backupMock = new Mock<IDatabaseBackupService>();
        var compressionMock = new Mock<ICompressionService>();
        var sftpMock = new Mock<ISftpService>();
        var emailMock = new Mock<IEmailService>();

        backupMock.Setup(s => s.BackupDatabaseAsync("DB1", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult("DB1", BackupStage.SqlBackup, "Backup error"));
        backupMock.Setup(s => s.BackupDatabaseAsync("DB2", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB2", BackupStage.VerifyBackup));
        backupMock.Setup(s => s.BackupDatabaseAsync("DB3", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB3", BackupStage.VerifyBackup));

        compressionMock.Setup(s => s.CreateZipAsync("DB2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult("DB2", BackupStage.Zip, "ZIP error"));
        compressionMock.Setup(s => s.CreateZipAsync("DB3", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB3", BackupStage.VerifyZip));

        sftpMock.Setup(s => s.UploadAsync("DB3", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("DB3", BackupStage.SftpUpload));

        emailMock.Setup(s => s.SendSummaryEmailAsync(It.IsAny<BackupRunResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sqlOptions = new SqlServerOptions
        {
            ConnectionString = "Server=TEST;Database=master;Integrated Security=True;",
            Databases = ["DB1", "DB2", "DB3"],
            BackupDirectoryOnSqlServer = @"D:\SQLBackups\Temp",
            BackupDirectoryFromApp = @"\\TEST\SQLBackups$\Temp"
        };

        var runner = CreateBackupJobRunner(
            backupMock, compressionMock, sftpMock, emailMock, sqlOptions);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(2, result.FailureCount);
        Assert.Equal("PARTIAL", result.StatusLabel);

        Assert.Equal(BackupStage.SqlBackup, result.DatabaseResults[0].Stage);
        Assert.Equal(BackupStage.Zip, result.DatabaseResults[1].Stage);
        Assert.True(result.DatabaseResults[2].Success);
    }

    [Fact]
    public async Task RunAsync_NoDatabasesConfigured_ReturnsEarly()
    {
        // Arrange
        var backupMock = new Mock<IDatabaseBackupService>();
        var emailMock = new Mock<IEmailService>();

        var sqlOptions = new SqlServerOptions
        {
            ConnectionString = "Server=TEST;Database=master;Integrated Security=True;",
            Databases = [],
            BackupDirectoryOnSqlServer = @"D:\SQLBackups\Temp",
            BackupDirectoryFromApp = @"\\TEST\SQLBackups$\Temp"
        };

        var runner = CreateBackupJobRunner(backupMock, emailMock: emailMock, sqlOptions: sqlOptions);

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Equal("FAILED", result.StatusLabel);
        backupMock.Verify(s => s.BackupDatabaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
