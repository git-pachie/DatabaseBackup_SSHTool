using SqlBackupManager.Utilities;
using Xunit;

namespace SqlBackupManager.Tests.Utilities;

/// <summary>
/// Tests for PathValidator directory and UNC share validation.
/// </summary>
public class PathValidatorTests
{
    [Fact]
    public void ValidateDirectory_ExistingLocalDir_ReturnsTrue()
    {
        var tempDir = Path.GetTempPath();
        Assert.True(PathValidator.ValidateDirectory(tempDir, "Test temp dir"));
    }

    [Fact]
    public void ValidateDirectory_NonexistentDirWithoutCreate_ReturnsFalse()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"nonexist_{Guid.NewGuid():N}");
        Assert.False(PathValidator.ValidateDirectory(nonExistentDir, "Non-existent dir"));
    }

    [Fact]
    public void ValidateDirectory_NonexistentDirWithCreate_ReturnsTrue()
    {
        var newDir = Path.Combine(Path.GetTempPath(), $"created_{Guid.NewGuid():N}");
        try
        {
            Assert.True(PathValidator.ValidateDirectory(newDir, "New dir", createIfMissing: true));
            Assert.True(Directory.Exists(newDir));
        }
        finally
        {
            if (Directory.Exists(newDir))
                Directory.Delete(newDir, recursive: true);
        }
    }

    [Fact]
    public void ValidateDirectory_EmptyPath_ReturnsFalse()
    {
        Assert.False(PathValidator.ValidateDirectory("", "Empty path"));
        Assert.False(PathValidator.ValidateDirectory(null!, "Null path"));
    }

    [Fact]
    public void EnsureWritableDirectory_CreatesAndVerifiesWriteAccess()
    {
        var newDir = Path.Combine(Path.GetTempPath(), $"writable_{Guid.NewGuid():N}");
        try
        {
            Assert.True(PathValidator.EnsureWritableDirectory(newDir, "Writable dir"));
            Assert.True(Directory.Exists(newDir));
        }
        finally
        {
            if (Directory.Exists(newDir))
                Directory.Delete(newDir, recursive: true);
        }
    }

    [Fact]
    public void EnsureWritableDirectory_EmptyPath_ReturnsFalse()
    {
        Assert.False(PathValidator.EnsureWritableDirectory("", "Empty path"));
    }

    [Fact]
    public void ValidateUncShare_EmptyPath_ReturnsFalse()
    {
        Assert.False(PathValidator.ValidateUncShare("", "Empty UNC"));
    }

    [Fact]
    public void ValidateUncShare_NonUncPath_ReturnsFalse()
    {
        Assert.False(PathValidator.ValidateUncShare(@"C:\LocalPath", "Local path"));
        Assert.False(PathValidator.ValidateUncShare(@"D:\backups", "Local path"));
    }
}
