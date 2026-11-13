using SqlBackupManager.Models;
using Xunit;

namespace SqlBackupManager.Tests.Models;

/// <summary>
/// Tests for BackupRunResult aggregation and status label logic.
/// </summary>
public class BackupRunResultTests
{
    private static DatabaseBackupResult SuccessResult(string dbName) => new()
    {
        DatabaseName = dbName,
        Success = true,
        Stage = BackupStage.Completed,
        StartedAtUtc = DateTime.UtcNow,
        EndedAtUtc = DateTime.UtcNow
    };

    private static DatabaseBackupResult FailedResult(string dbName, BackupStage stage) => new()
    {
        DatabaseName = dbName,
        Success = false,
        Stage = stage,
        ErrorMessage = "Test error",
        StartedAtUtc = DateTime.UtcNow,
        EndedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public void AllSucceeded_WhenAllResultsSuccessful_ReturnsTrue()
    {
        var result = new BackupRunResult
        {
            RunId = "test123",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            EndedAtUtc = DateTime.UtcNow,
            DatabaseResults = [SuccessResult("DB1"), SuccessResult("DB2")]
        };

        Assert.True(result.AllSucceeded);
        Assert.False(result.IsPartial);
        Assert.False(result.AllFailed);
        Assert.Equal("SUCCESS", result.StatusLabel);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
    }

    [Fact]
    public void IsPartial_WhenMixedResults_ReturnsTrue()
    {
        var result = new BackupRunResult
        {
            RunId = "test123",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            EndedAtUtc = DateTime.UtcNow,
            DatabaseResults = [SuccessResult("DB1"), FailedResult("DB2", BackupStage.SqlBackup)]
        };

        Assert.False(result.AllSucceeded);
        Assert.True(result.IsPartial);
        Assert.False(result.AllFailed);
        Assert.Equal("PARTIAL", result.StatusLabel);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
    }

    [Fact]
    public void AllFailed_WhenAllResultsFailed_ReturnsTrue()
    {
        var result = new BackupRunResult
        {
            RunId = "test123",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            EndedAtUtc = DateTime.UtcNow,
            DatabaseResults = [FailedResult("DB1", BackupStage.SqlBackup), FailedResult("DB2", BackupStage.Zip)]
        };

        Assert.False(result.AllSucceeded);
        Assert.False(result.IsPartial);
        Assert.True(result.AllFailed);
        Assert.Equal("FAILED", result.StatusLabel);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(2, result.FailureCount);
    }

    [Fact]
    public void StatusLabel_WhenNoDatabases_ReturnsFailed()
    {
        var result = new BackupRunResult
        {
            RunId = "test123",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            EndedAtUtc = DateTime.UtcNow,
            DatabaseResults = []
        };

        Assert.Equal("FAILED", result.StatusLabel);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
    }

    [Fact]
    public void Elapsed_ReturnsCorrectTimeSpan()
    {
        var started = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
        var ended = new DateTime(2026, 11, 13, 14, 5, 30, DateTimeKind.Utc);

        var result = new BackupRunResult
        {
            StartedAtUtc = started,
            EndedAtUtc = ended
        };

        Assert.Equal(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), result.Elapsed);
    }

    [Fact]
    public void TotalCount_ReturnsDatabaseResultsCount()
    {
        var result = new BackupRunResult
        {
            DatabaseResults = [SuccessResult("DB1"), SuccessResult("DB2"), SuccessResult("DB3")]
        };

        Assert.Equal(3, result.TotalCount);
    }
}
