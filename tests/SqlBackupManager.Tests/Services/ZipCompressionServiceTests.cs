using Microsoft.Extensions.Logging;
using Moq;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Models;
using SqlBackupManager.Services;
using Xunit;

namespace SqlBackupManager.Tests.Services;

/// <summary>
/// Tests for ZipCompressionService — verifies ZIP creation, integrity check,
/// and failure handling for missing source files.
/// </summary>
public class ZipCompressionServiceTests
{
    private static ZipCompressionService CreateService()
    {
        var loggerMock = new Mock<ILogger<ZipCompressionService>>();
        var errorHandlerLoggerMock = new Mock<ILogger<BackupErrorHandler>>();
        var errorHandler = new BackupErrorHandler(errorHandlerLoggerMock.Object);
        return new ZipCompressionService(loggerMock.Object, errorHandler);
    }

    [Fact]
    public async Task CreateZipAsync_ValidBakFile_CreatesZipAndVerifies()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"ziptest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bakPath = Path.Combine(tempDir, "test_db.bak");
        var zipPath = Path.Combine(tempDir, "test_db.zip");

        // Create a mock .bak file
        var testData = new byte[10 * 1024]; // 10KB
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(bakPath, testData);

        var service = CreateService();

        try
        {
            // Act
            var result = await service.CreateZipAsync("test_db", bakPath, zipPath);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(BackupStage.Completed, result.Stage);
            Assert.Equal(zipPath, result.ZipFilePath);
            Assert.NotNull(result.ZipFileSizeBytes);
            Assert.True(result.ZipFileSizeBytes > 0);
            Assert.True(File.Exists(zipPath));

            // ZIP should be smaller than or equal to the bak for this data
            // (random data may not compress well, but the archive should exist)
            Assert.True(result.ZipFileSizeBytes > 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateZipAsync_MissingBakFile_ReturnsFailure()
    {
        // Arrange
        var service = CreateService();
        var bakPath = Path.Combine(Path.GetTempPath(), $"nonexist_{Guid.NewGuid():N}.bak");
        var zipPath = Path.Combine(Path.GetTempPath(), $"nonexist_{Guid.NewGuid():N}.zip");

        // Act
        var result = await service.CreateZipAsync("nonexist_db", bakPath, zipPath);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(BackupStage.Zip, result.Stage);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not found", result.ErrorMessage);
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public async Task CreateZipAsync_ZipContainsCorrectEntry()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"zipverify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bakPath = Path.Combine(tempDir, "MyDatabase_20261113.bak");
        var zipPath = Path.Combine(tempDir, "MyDatabase_20261113.zip");

        var testData = new byte[1024 * 50]; // 50KB
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(bakPath, testData);

        var service = CreateService();

        try
        {
            // Act
            var result = await service.CreateZipAsync("MyDatabase", bakPath, zipPath);

            // Assert
            Assert.True(result.Success);

            // Verify the ZIP contains exactly one entry with the correct name
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entries = archive.Entries.ToList();
            Assert.Single(entries);
            Assert.Equal("MyDatabase_20261113.bak", entries[0].Name);
            Assert.Equal(testData.Length, entries[0].Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateZipAsync_EmptyBakFile_FailsZipVerification()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"zipempty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var bakPath = Path.Combine(tempDir, "empty_db.bak");
        var zipPath = Path.Combine(tempDir, "empty_db.zip");

        await File.WriteAllBytesAsync(bakPath, []); // 0 bytes

        var service = CreateService();

        try
        {
            // Act
            var result = await service.CreateZipAsync("empty_db", bakPath, zipPath);

            // Assert — ZIP creation succeeds but verification fails because the
            // entry is 0 bytes (matching the empty .bak source).
            Assert.False(result.Success);
            Assert.Equal(BackupStage.VerifyZip, result.Stage);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
