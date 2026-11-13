using SqlBackupManager.Utilities;
using Xunit;

namespace SqlBackupManager.Tests.Utilities;

/// <summary>
/// Tests for FileNameHelper file name generation and sanitization.
/// </summary>
public class FileNameHelperTests
{
    [Fact]
    public void GenerateRunId_ReturnsNonEmptyString()
    {
        // Act
        var runId = FileNameHelper.GenerateRunId(DateTime.UtcNow);

        // Assert
        Assert.False(string.IsNullOrEmpty(runId));
        Assert.Matches(@"^\d{8}_\d{6}_[0-9A-F]{4,5}$", runId);
    }

    [Fact]
    public void GenerateRunId_ReturnsUniqueValues()
    {
        // Arrange
        var timestamp = DateTime.UtcNow;

        // Act
        var id1 = FileNameHelper.GenerateRunId(timestamp);
        var id2 = FileNameHelper.GenerateRunId(timestamp);

        // Assert — random suffix makes them unique even for the same timestamp
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void SanitizeDatabaseName_ReplacesInvalidCharacters()
    {
        // Act
        var result = FileNameHelper.SanitizeDatabaseName("DB<>:*?");

        // Assert
        Assert.Equal("DB_____", result);
    }

    [Fact]
    public void SanitizeDatabaseName_PreservesValidName()
    {
        // Act
        var result = FileNameHelper.SanitizeDatabaseName("BSS_Prod-2024.db");

        // Assert
        Assert.Equal("BSS_Prod-2024.db", result);
    }

    [Fact]
    public void SanitizeDatabaseName_HandlesEmptyName()
    {
        // Act
        var result = FileNameHelper.SanitizeDatabaseName("");

        // Assert
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void SanitizeDatabaseName_HandlesNull()
    {
        // Act
        var result = FileNameHelper.SanitizeDatabaseName(null!);

        // Assert
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void SanitizeDatabaseName_HandlesWhitespace()
    {
        // Act
        var result = FileNameHelper.SanitizeDatabaseName("   ");

        // Assert
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void GetBakFileName_ReturnsCorrectFormat()
    {
        // Act
        var result = FileNameHelper.GetBakFileName("BSS", "20261113_142200_ABCD");

        // Assert
        Assert.Equal("BSS_20261113_142200_ABCD.bak", result);
    }

    [Fact]
    public void GetBakFileName_SanitizesDatabaseName()
    {
        // Act
        var result = FileNameHelper.GetBakFileName("DB:Bad", "20261113_142200_ABCD");

        // Assert
        Assert.Equal("DB_Bad_20261113_142200_ABCD.bak", result);
    }

    [Fact]
    public void GetZipFileName_ReturnsCorrectFormat()
    {
        // Act
        var result = FileNameHelper.GetZipFileName("OPS_DB", "20261113_142200_ABCD");

        // Assert
        Assert.Equal("OPS_DB_20261113_142200_ABCD.zip", result);
    }

    [Fact]
    public void GetLogFileName_ReturnsCorrectFormat()
    {
        // Act
        var result = FileNameHelper.GetLogFileName("20261113_142200_ABCD");

        // Assert
        Assert.Equal("backup_20261113_142200_ABCD.log", result);
    }

    [Fact]
    public void GetTempRemoteFileName_AppendsTmpExtension()
    {
        // Act
        var result = FileNameHelper.GetTempRemoteFileName("BSS_20261113_142200_ABCD.zip");

        // Assert
        Assert.Equal("BSS_20261113_142200_ABCD.zip.tmp", result);
    }

    [Fact]
    public void GenerateRunId_IncludesCorrectDate()
    {
        // Arrange
        var timestamp = new DateTime(2026, 11, 13, 14, 22, 0, DateTimeKind.Utc);

        // Act
        var runId = FileNameHelper.GenerateRunId(timestamp);

        // Assert
        Assert.StartsWith("20261113_142200_", runId);
    }
}
