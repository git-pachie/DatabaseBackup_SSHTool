using Microsoft.Extensions.Logging;
using Moq;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Models;
using Xunit;

namespace SqlBackupManager.Tests.ErrorHandling;

/// <summary>
/// Tests for the BackupErrorHandler, including sensitive data redaction
/// and failure result creation.
/// </summary>
public class BackupErrorHandlerTests
{
    private static BackupErrorHandler CreateHandler()
    {
        var loggerMock = new Mock<ILogger<BackupErrorHandler>>();
        return new BackupErrorHandler(loggerMock.Object);
    }

    [Fact]
    public void RedactSensitiveData_RemovesPasswordsFromConnectionString()
    {
        // Arrange
        var input = "Server=SQL01;User ID=user;Password=SecretPass123;Encrypt=True;";
        var expected = "Server=SQL01;User ID=user;Password=***REDACTED***;Encrypt=True;";

        // Act
        var result = BackupErrorHandler.RedactSensitiveData(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RedactSensitiveData_RemovesPwdFromConnectionString()
    {
        // Arrange
        var input = "Server=SQL01;Pwd=MyPassword;Encrypt=True;";

        // Act
        var result = BackupErrorHandler.RedactSensitiveData(input);

        // Assert
        Assert.DoesNotContain("MyPassword", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    public void RedactSensitiveData_RemovesPemKeyBlocks()
    {
        // Arrange
        var input = "Error reading key: -----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA...\n-----END RSA PRIVATE KEY-----";

        // Act
        var result = BackupErrorHandler.RedactSensitiveData(input);

        // Assert
        Assert.Contains("***PEM KEY REDACTED***", result);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA", result);
    }

    [Fact]
    public void RedactSensitiveData_RemovesTokens()
    {
        // Arrange
        var input = "Request failed with token=abc123def456; Retry needed";

        // Act
        var result = BackupErrorHandler.RedactSensitiveData(input);

        // Assert
        Assert.DoesNotContain("abc123def456", result);
    }

    [Fact]
    public void RedactSensitiveData_PreservesNonSensitiveData()
    {
        // Arrange
        var input = "Database BSS failed at stage Zip with error: File not found at D:\\backups\\BSS.bak";

        // Act
        var result = BackupErrorHandler.RedactSensitiveData(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void RedactSensitiveData_EmptyInput_ReturnsEmpty()
    {
        // Act
        var result = BackupErrorHandler.RedactSensitiveData("");
        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void RedactSensitiveData_NullInput_ReturnsEmpty()
    {
        // Act
        var result = BackupErrorHandler.RedactSensitiveData(null);
        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void CreateFailureResult_FromException_LogsAndReturnsFailure()
    {
        // Arrange
        var handler = CreateHandler();
        var ex = new InvalidOperationException("Test error message");

        // Act
        var result = handler.CreateFailureResult("DB1", BackupStage.SqlBackup, ex);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("DB1", result.DatabaseName);
        Assert.Equal(BackupStage.SqlBackup, result.Stage);
        Assert.Equal("Test error message", result.ErrorMessage);
    }

    [Fact]
    public void CreateFailureResult_FromMessage_LogsAndReturnsFailure()
    {
        // Arrange
        var handler = CreateHandler();
        var message = "Database does not exist";

        // Act
        var result = handler.CreateFailureResult("DB2", BackupStage.Validation, message);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("DB2", result.DatabaseName);
        Assert.Equal(BackupStage.Validation, result.Stage);
        Assert.Equal(message, result.ErrorMessage);
    }

    [Fact]
    public void CreateFailureResult_PreservesInProgressResultData()
    {
        // Arrange
        var handler = CreateHandler();
        var inProgress = new DatabaseBackupResult
        {
            DatabaseName = "DB3",
            Success = true,
            Stage = BackupStage.SqlBackup,
            BakFilePathFromApp = @"\\server\share\DB3.bak",
            BakFilePathOnServer = @"D:\backups\DB3.bak",
            BakFileSizeBytes = 1024,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        var ex = new IOException("Disk full");

        // Act
        var result = handler.CreateFailureResult(inProgress, BackupStage.VerifyBackup, ex);

        // Assert
        Assert.Same(inProgress, result);
        Assert.False(result.Success);
        Assert.Equal(BackupStage.VerifyBackup, result.Stage);
        Assert.Equal("Disk full", result.ErrorMessage);
        Assert.Equal(@"\\server\share\DB3.bak", result.BakFilePathFromApp);
    }

    [Fact]
    public void CreateFailureResult_RedactsSensitiveDataInErrorMessage()
    {
        // Arrange
        var handler = CreateHandler();
        var ex = new Exception("Connection failed: Password=SuperSecret123; timeout");

        // Act
        var result = handler.CreateFailureResult("DB1", BackupStage.SqlBackup, ex);

        // Assert
        Assert.DoesNotContain("SuperSecret123", result.ErrorMessage);
        Assert.Contains("***REDACTED***", result.ErrorMessage);
    }

    [Fact]
    public void FormatException_RedactsSensitiveData()
    {
        // Arrange
        var ex = new InvalidOperationException("Error: Password=hidden123; Connection refused");

        // Act
        var result = BackupErrorHandler.FormatException(ex);

        // Assert
        Assert.DoesNotContain("hidden123", result);
        Assert.Contains("***REDACTED***", result);
    }
}
