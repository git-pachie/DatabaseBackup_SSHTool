using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SqlBackupManager.Configuration;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Models;
using SqlBackupManager.Services;
using Xunit;

namespace SqlBackupManager.Tests.Services;

/// <summary>
/// Tests for ZipCompressionService: AES-256 password-protected ZIP creation,
/// integrity verification, and failure handling.
/// </summary>
public class ZipCompressionServiceTests
{
    private const string TestPassword = "Test-Zip-Password-123!";

    private static ZipCompressionService CreateService(string? zipPassword = TestPassword)
    {
        var storage = new StorageOptions { ZipPassword = zipPassword ?? string.Empty };
        var optionsMock = new Mock<IOptions<StorageOptions>>();
        optionsMock.Setup(o => o.Value).Returns(storage);

        var loggerMock = new Mock<ILogger<ZipCompressionService>>();
        var errorHandlerLoggerMock = new Mock<ILogger<BackupErrorHandler>>();
        var errorHandler = new BackupErrorHandler(errorHandlerLoggerMock.Object);
        return new ZipCompressionService(optionsMock.Object, loggerMock.Object, errorHandler);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ziptest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Reads the first entry of a ZIP using the given password.</summary>
    private static byte[] ReadEntry(string zipPath, string? password)
    {
        using var zip = new ZipFile(zipPath);
        if (password is not null)
            zip.Password = password;

        var entry = zip.GetEntry(zip[0].Name)!;
        using var stream = zip.GetInputStream(entry);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task CreateZipAsync_ValidBakFile_CreatesEncryptedZipAndVerifies()
    {
        var tempDir = NewTempDir();
        var bakPath = Path.Combine(tempDir, "test_db.bak");
        var zipPath = Path.Combine(tempDir, "test_db.zip");

        var testData = new byte[10 * 1024];
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(bakPath, testData);

        try
        {
            var result = await CreateService().CreateZipAsync("test_db", bakPath, zipPath);

            Assert.True(result.Success);
            Assert.Equal(BackupStage.Completed, result.Stage);
            Assert.Equal(zipPath, result.ZipFilePath);
            Assert.True(result.ZipFileSizeBytes > 0);
            Assert.True(File.Exists(zipPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateZipAsync_ZipIsAes256EncryptedAndContainsOnlyTheBak()
    {
        var tempDir = NewTempDir();
        var bakPath = Path.Combine(tempDir, "MyDatabase_20261113.bak");
        var zipPath = Path.Combine(tempDir, "MyDatabase_20261113.zip");

        var testData = new byte[50 * 1024];
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(bakPath, testData);

        try
        {
            var result = await CreateService().CreateZipAsync("MyDatabase", bakPath, zipPath);
            Assert.True(result.Success);

            using var zip = new ZipFile(zipPath);
            Assert.Equal(1, zip.Count);

            var entry = zip[0];
            Assert.Equal("MyDatabase_20261113.bak", entry.Name);
            Assert.Equal(testData.Length, entry.Size);
            Assert.True(entry.IsCrypted);
            Assert.Equal(256, entry.AESKeySize);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateZipAsync_CorrectPassword_ReturnsOriginalContent()
    {
        var tempDir = NewTempDir();
        var bakPath = Path.Combine(tempDir, "db.bak");
        var zipPath = Path.Combine(tempDir, "db.zip");

        var testData = new byte[20 * 1024];
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(bakPath, testData);

        try
        {
            var result = await CreateService().CreateZipAsync("db", bakPath, zipPath);
            Assert.True(result.Success);

            Assert.Equal(testData, ReadEntry(zipPath, TestPassword));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateZipAsync_WrongOrMissingPassword_CannotReadContent()
    {
        var tempDir = NewTempDir();
        var bakPath = Path.Combine(tempDir, "db.bak");
        var zipPath = Path.Combine(tempDir, "db.zip");

        var testData = new byte[20 * 1024];
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(bakPath, testData);

        try
        {
            var result = await CreateService().CreateZipAsync("db", bakPath, zipPath);
            Assert.True(result.Success);

            Assert.ThrowsAny<Exception>(() => ReadEntry(zipPath, "not-the-password"));
            Assert.ThrowsAny<Exception>(() => ReadEntry(zipPath, null));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateZipAsync_NoPasswordConfigured_FailsAndCreatesNoZip()
    {
        var tempDir = NewTempDir();
        var bakPath = Path.Combine(tempDir, "db.bak");
        var zipPath = Path.Combine(tempDir, "db.zip");
        await File.WriteAllBytesAsync(bakPath, new byte[1024]);

        try
        {
            var result = await CreateService(zipPassword: "").CreateZipAsync("db", bakPath, zipPath);

            Assert.False(result.Success);
            Assert.Equal(BackupStage.Zip, result.Stage);
            Assert.Contains("password", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(zipPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateZipAsync_ErrorMessageNeverContainsPassword()
    {
        var bakPath = Path.Combine(Path.GetTempPath(), $"nonexist_{Guid.NewGuid():N}.bak");
        var zipPath = Path.Combine(Path.GetTempPath(), $"nonexist_{Guid.NewGuid():N}.zip");

        var result = await CreateService().CreateZipAsync("db", bakPath, zipPath);

        Assert.False(result.Success);
        Assert.DoesNotContain(TestPassword, result.ErrorMessage);
    }

    [Fact]
    public async Task CreateZipAsync_MissingBakFile_ReturnsFailure()
    {
        var bakPath = Path.Combine(Path.GetTempPath(), $"nonexist_{Guid.NewGuid():N}.bak");
        var zipPath = Path.Combine(Path.GetTempPath(), $"nonexist_{Guid.NewGuid():N}.zip");

        var result = await CreateService().CreateZipAsync("nonexist_db", bakPath, zipPath);

        Assert.False(result.Success);
        Assert.Equal(BackupStage.Zip, result.Stage);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not found", result.ErrorMessage);
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public async Task CreateZipAsync_EmptyBakFile_FailsZipVerification()
    {
        var tempDir = NewTempDir();
        var bakPath = Path.Combine(tempDir, "empty_db.bak");
        var zipPath = Path.Combine(tempDir, "empty_db.zip");
        await File.WriteAllBytesAsync(bakPath, []);

        try
        {
            var result = await CreateService().CreateZipAsync("empty_db", bakPath, zipPath);

            // The ZIP is written, but verification rejects a 0-byte entry
            Assert.False(result.Success);
            Assert.Equal(BackupStage.VerifyZip, result.Stage);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
