using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SqlBackupManager.Configuration;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;
using SqlBackupManager.Orchestration;
using SqlBackupManager.Services;
using SqlBackupManager.Utilities;

namespace SqlBackupManager;

/// <summary>
/// Entry point for the SQL Backup Manager console application.
/// Loads configuration, validates options, registers DI services, runs the
/// backup orchestrator, and returns an exit code for SQL Server Agent monitoring.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Early configuration loading for Serilog setup
        // User secrets override appsettings.json for sensitive values (SFTP, Email, ConnectionString)
        var configRoot = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
            // Optional secrets file for servers without the .NET SDK (never committed to git)
            .AddJsonFile("appsettings.secrets.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var runTimestampUtc = DateTime.UtcNow;
        var runId = FileNameHelper.GenerateRunId(runTimestampUtc);

        // Load storage options for log directory
        var storageOptions = configRoot.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

        // Set up Serilog with a per-run log file
        var logFileName = FileNameHelper.GetLogFileName(runId);
        var logFilePath = Path.Combine(storageOptions.LogDirectory, logFileName);

        // Ensure log directory exists
        if (!string.IsNullOrEmpty(storageOptions.LogDirectory))
        {
            Directory.CreateDirectory(storageOptions.LogDirectory);
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.WithProperty("RunId", runId)
            .WriteTo.File(
                logFilePath,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Infinite)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("Starting SQL Backup Manager, Run ID: {RunId}", runId);

            // Help diagnose "not configured" errors: show where config is read from
            var secretsFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
            Log.Information("Config directory: {BaseDirectory}", AppContext.BaseDirectory);
            Log.Information(
                File.Exists(secretsFilePath)
                    ? "Secrets file found: {SecretsFile}"
                    : "Secrets file NOT found (optional): {SecretsFile}",
                secretsFilePath);

            // Validate configuration options
            var validationErrors = ValidateConfiguration(configRoot, storageOptions);
            if (validationErrors.Count > 0)
            {
                Log.Fatal("Configuration validation failed with {Count} error(s):", validationErrors.Count);
                foreach (var error in validationErrors)
                {
                    Log.Fatal("  - {Error}", error);
                }
                return 2;
            }

            // Configure DI
            var services = new ServiceCollection();
            ConfigureServices(services, configRoot);

            // Replace default logging with Serilog
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(Log.Logger, dispose: true);
            });

            var serviceProvider = services.BuildServiceProvider();

            var runner = serviceProvider.GetRequiredService<IBackupJobRunner>();

            using var cts = new CancellationTokenSource();

            // Allow Ctrl+C cancellation
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Log.Warning("Cancellation requested, shutting down gracefully...");
                cts.Cancel();
            };

            var runResult = await runner.RunAsync(cts.Token);

            // Determine exit code:
            // 0 = all databases succeeded and email sent (or not required)
            // 1 = one or more databases failed, or email failed
            // 2 = configuration/startup error
            var exitCode = DetermineExitCode(runResult);

            Log.Information(
                "Run {RunId} finished with status {Status}, exit code {ExitCode}",
                runId,
                runResult.StatusLabel,
                exitCode);

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Run {RunId} was cancelled.", runId);
            return 3;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Run {RunId} encountered a fatal error: {Message}", runId, ex.Message);
            return 4;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Validates all configuration sections and directory access before starting the run.
    /// </summary>
    private static List<string> ValidateConfiguration(IConfiguration config, StorageOptions storageOptions)
    {
        var errors = new List<string>();

        // SqlServer validation
        var sqlOptions = config.GetSection(SqlServerOptions.SectionName).Get<SqlServerOptions>() ?? new SqlServerOptions();

        if (string.IsNullOrWhiteSpace(sqlOptions.ConnectionString))
            errors.Add("SqlServer.ConnectionString is not configured.");

        if (sqlOptions.Databases.Count == 0)
            errors.Add("SqlServer.Databases is empty — no databases configured for backup.");

        // BackupDirectoryOnSqlServer is a path on the SQL Server host. This machine may be a
        // different PC, so it cannot be checked locally. SQL Server reports an error at backup time.
        if (string.IsNullOrWhiteSpace(sqlOptions.BackupDirectoryOnSqlServer))
            errors.Add("SqlServer.BackupDirectoryOnSqlServer is not configured.");

        // BackupDirectoryFromApp is how THIS machine reads the same folder: a UNC share when SQL Server
        // is remote, or the same local path when SQL Server runs on this machine.
        if (string.IsNullOrWhiteSpace(sqlOptions.BackupDirectoryFromApp))
            errors.Add("SqlServer.BackupDirectoryFromApp is not configured.");
        else if (!PathValidator.ValidateDirectory(sqlOptions.BackupDirectoryFromApp, "SqlServer.BackupDirectoryFromApp"))
            errors.Add("SqlServer.BackupDirectoryFromApp does not exist or is not accessible from this machine.");

        // Storage validation
        if (string.IsNullOrWhiteSpace(storageOptions.LocalZipDirectory))
            errors.Add("Storage.LocalZipDirectory is not configured.");
        else if (!PathValidator.EnsureWritableDirectory(storageOptions.LocalZipDirectory, "Storage.LocalZipDirectory"))
            errors.Add("Storage.LocalZipDirectory is not writable.");

        if (string.IsNullOrWhiteSpace(storageOptions.LogDirectory))
            errors.Add("Storage.LogDirectory is not configured.");
        else if (!PathValidator.EnsureWritableDirectory(storageOptions.LogDirectory, "Storage.LogDirectory"))
            errors.Add("Storage.LogDirectory is not writable.");

        if (string.IsNullOrWhiteSpace(storageOptions.ZipPassword))
            errors.Add("Storage.ZipPassword is not configured. Every ZIP must be password-protected.");
        else if (storageOptions.ZipPassword == "CHANGE_ME_ZIP_PASSWORD")
            errors.Add("Storage.ZipPassword is still the placeholder value. Set a real password in appsettings.json.");

        // SFTP validation
        var sftpOptions = config.GetSection(SftpOptions.SectionName).Get<SftpOptions>() ?? new SftpOptions();

        if (string.IsNullOrWhiteSpace(sftpOptions.Host))
            errors.Add("Sftp.Host is not configured.");

        if (string.IsNullOrWhiteSpace(sftpOptions.Username))
            errors.Add("Sftp.Username is not configured.");

        if (string.IsNullOrWhiteSpace(sftpOptions.HostKeyFingerprint))
            errors.Add("Sftp.HostKeyFingerprint is not configured. Host-key verification is required and cannot be disabled.");

        if (string.IsNullOrWhiteSpace(sftpOptions.RemoteDirectory))
            errors.Add("Sftp.RemoteDirectory is not configured.");

        // Email validation
        var emailOptions = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();

        if (emailOptions.Enabled)
        {
            if (string.IsNullOrWhiteSpace(emailOptions.SmtpHost))
                errors.Add("Email.SmtpHost is not configured (Email.Enabled is true).");

            if (emailOptions.SmtpPort <= 0)
                errors.Add("Email.SmtpPort is not valid.");

            if (string.IsNullOrWhiteSpace(emailOptions.From))
                errors.Add("Email.From is not configured.");

            if (emailOptions.Recipients.Count == 0)
                errors.Add("Email.Recipients is empty.");
        }

        return errors;
    }

    /// <summary>
    /// Determines the exit code based on run results.
    /// 0 = all databases succeeded; email either succeeded or was not required.
    /// 1 = one or more databases failed, or email failed when it was required.
    /// </summary>
    private static int DetermineExitCode(BackupRunResult runResult)
    {
        if (!runResult.AllSucceeded)
            return 1;

        // All databases succeeded — check email status
        if (runResult.FailureCount > 0 || !runResult.EmailSent)
        {
            // Email was expected but didn't send — this is a warning but not a hard failure
            // Return 0 only if all databases succeeded; email failure is logged
            // but doesn't fail the run per spec ("If email fails, log that failure independently")
            // However, the spec says "return 0 only if every database completed successfully
            // AND required reporting succeeded." So if email was required but failed, return nonzero.
            if (!runResult.EmailSent && runResult.TotalCount > 0)
            {
                Log.Warning("Email was required but not sent successfully. Treating as a reporting failure.");
                return 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Registers all services with the DI container.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Options
        services.Configure<SqlServerOptions>(config.GetSection(SqlServerOptions.SectionName));
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.SectionName));
        services.Configure<SftpOptions>(config.GetSection(SftpOptions.SectionName));
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));

        // Error handling
        services.AddSingleton<BackupErrorHandler>();

        // Services
        services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
        services.AddSingleton<ICompressionService, ZipCompressionService>();
        services.AddSingleton<ISftpService, SftpService>();
        services.AddSingleton<IEmailService, EmailService>();

        // Orchestration
        services.AddSingleton<IBackupJobRunner, BackupJobRunner>();
    }
}
