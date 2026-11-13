# SQL Backup Manager — Implementation Specification for Coding Agents

> Copy this Markdown file into Kiro, Codex, or another coding assistant as the implementation brief. Build a maintainable C# .NET console application, not a monolithic script.

## Goal

**Scope restriction:** Keep this app straightforward: backup → ZIP → SFTP → logs → one email summary. No file cleanup, retention policies, automatic deletion, or retries. Cleanup will be handled by a separate application/job.

Create a configurable **.NET 9 C# console application** that backs up a selected list of Microsoft SQL Server databases, creates **one ZIP per database**, uploads each ZIP to a configurable **SFTP remote directory**, writes a **separate log file per run**, and sends **one consolidated SMTP email** reporting successes and failures. **No automatic retries.**

The application may run on a separate Windows 10/11 PC, not necessarily the SQL Server machine. Support scheduling through **SQL Server Agent** (CmdExec job step) or optionally Windows Task Scheduler. Do not implement an internal scheduler.

## Architecture

Use dependency injection, strongly typed options, testable interfaces, and clear separation of concerns.

```text
SqlBackupManager/
  src/SqlBackupManager/
    Program.cs
    appsettings.json
    Configuration/
      SqlServerOptions.cs
      StorageOptions.cs
      SftpOptions.cs
      EmailOptions.cs
    Models/
      DatabaseBackupResult.cs
      BackupRunResult.cs
      BackupStage.cs
    Interfaces/
      IDatabaseBackupService.cs
      ICompressionService.cs
      ISftpService.cs
      IEmailService.cs
      IBackupJobRunner.cs
    Services/
      DatabaseBackupService.cs
      ZipCompressionService.cs
      SftpService.cs
      EmailService.cs
    ErrorHandling/
      BackupErrorHandler.cs
    Orchestration/
      BackupJobRunner.cs
    Utilities/
      FileNameHelper.cs
      PathValidator.cs
  tests/SqlBackupManager.Tests/
  README.md
```

**Responsibilities**

- `DatabaseBackupService`: execute full `BACKUP DATABASE` for a single configured database and return the exact resulting `.bak` path. Use `Microsoft.Data.SqlClient`. Quote/escape SQL identifiers safely; validate database existence and permissions. SQL parameters cannot substitute for identifiers or backup file paths: construct those carefully using validated/escaped values.
- `ZipCompressionService`: create a ZIP containing exactly the `.bak` produced for that database in the current run. Use `System.IO.Compression`; avoid reading the entire file into memory.
- `SftpService`: connect, authenticate using SSH private key when configured or username/password when the key is null, verify the configured SSH host key, ensure remote destination exists (or fail with a clear message if not authorized), upload ZIP to a temporary remote name, check uploaded size, then rename to final name. Use SSH.NET. Never silently disable host-key verification.
- `EmailService`: send one consolidated HTML/plain-text summary email after processing all databases, using MailKit and configurable SMTP host, port, TLS mode, sender and recipients.
- `BackupJobRunner`: coordinate per-database backup → ZIP → SFTP; record stage status, duration, file sizes and errors. Continue with other databases after an individual failure. No retry. Send summary once, even after partial failures.
- Logging: Serilog file sink with a distinct log file per execution, using UTC timestamp and unique Run ID. Log errors without passwords, tokens, private keys or full connection strings.
- `Program.cs`: load config, validate options, register DI, run orchestrator, return exit code.

## Example appsettings.json

```json
{
  "SqlServer": {
    "ConnectionString": "Server=SQLSERVER01;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;",
    "Databases": ["BSS", "OPS_DB", "TRM"],
    "BackupDirectoryOnSqlServer": "D:\\SQLBackups\\Temp",
    "BackupDirectoryFromApp": "\\\\SQLSERVER01\\SQLBackups$\\Temp",
    "UseNativeCompression": false
  },
  "Storage": {
    "LocalZipDirectory": "C:\\SqlBackupManager\\Zip",
    "LogDirectory": "C:\\SqlBackupManager\\Logs"
  },
  "Sftp": {
    "Host": "sftp.example.com",
    "Port": 22,
    "Username": "backupuser",
    "Authentication": "Auto",
    "PrivateKeyPath": null,
    "HostKeyFingerprint": "SHA256:REPLACE_WITH_VERIFIED_FINGERPRINT",
    "RemoteDirectory": "/database-backups/sqlserver/"
  },
  "Email": {
    "Enabled": true,
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "Security": "StartTls",
    "From": "sqlbackup@example.com",
    "Recipients": ["dba@example.com", "it-support@example.com"],
    "SendOnSuccess": true,
    "SendOnFailure": true,
    "AttachLogFile": true
  }
}
```

All values above are **examples**, not real credentials. SFTP password/private-key passphrase and SMTP password must come from environment variables or an OS-protected secret store.

### SFTP authentication selection

- `PrivateKeyPath` non-null and non-empty: authenticate using SSH private key plus username. Support optional key passphrase from secret storage.
- `PrivateKeyPath` null or empty: authenticate using username and password, with password read from secret storage (e.g. `Sftp__Password` environment variable).
- Do not automatically fall back to password after a configured private-key authentication failure; report the error. The selection is based on configuration, not failed login attempts.
- Both modes must verify the SFTP server SSH host key fingerprint. This fingerprint is **not** the client's private key and remains required in password mode.
- Do not log either password or private-key contents. Validate that the chosen authentication method has all required settings.
 Support integrated SQL authentication and optional secure SQL credentials. Never commit secrets.

## Critical cross-machine file handling

`BACKUP DATABASE ... TO DISK` writes on the **SQL Server host**, even when the command is initiated remotely. The SQL Server service account must be able to write to `BackupDirectoryOnSqlServer`. The Windows account running this application must be able to read the **same physical folder** via `BackupDirectoryFromApp` (UNC share).

Example mapping:

- SQL Server writes: `D:\SQLBackups\Temp\BSS_20261113_142200_<RunId>.bak`
- Application reads: `\\SQLSERVER01\SQLBackups$\Temp\BSS_20261113_142200_<RunId>.bak`
- Application writes ZIP locally: `C:\SqlBackupManager\Zip\BSS_20261113_142200_<RunId>.zip`
- SFTP destination: `/database-backups/sqlserver/BSS_20261113_142200_<RunId>.zip`

Do **not** assume that a local path on the app PC is accessible to SQL Server. Do not scan the temp folder and zip old `.bak` files: track the exact file paths created by the current run. For a local SQL Server installation, both paths may resolve locally.

## Workflow

1. Generate a unique Run ID and timestamp; create a new per-run log file.
2. Validate configuration, directory access, database list, SSH host key settings and required secrets. Fail early with a clear error if global configuration is invalid.
3. For each configured database, sequentially:
   - Validate database name and availability.
   - Perform **full** SQL Server backup to a unique `.bak` file. Use `WITH CHECKSUM` when supported; optionally `COMPRESSION` according to config. Avoid `COPY_ONLY` unless explicitly configured, and document impact on backup strategy.
   - Verify backup command succeeded and backup file is accessible and nonempty. Prefer `RESTORE VERIFYONLY WITH CHECKSUM` when feasible; note this is not equivalent to a test restore.
   - ZIP only this `.bak` into its own uniquely named ZIP.
   - Verify ZIP integrity by reopening the archive and checking the expected entry.
   - Upload ZIP to configured SFTP remote directory; verify remote size and successful final rename.
   - Mark database **successful only after verified SFTP upload**.
   - Keep both `.bak` and `.zip` files; do not delete them in this application.
   - On failure: capture stage, error message, timestamps and file paths; continue to next database. **No automatic retry**.
4. Produce run summary with success count, failure count, per-database statuses, elapsed times, backup/ZIP sizes, and failed stage/reason.
5. Send exactly one consolidated email if enabled, subject such as `[SQL Backup] SUCCESS | PARTIAL | FAILED - SQLSERVER01 - <RunId>`. If email fails, log that failure independently.
6. Return `0` only if every configured database completed successfully **and required reporting succeeded**; otherwise return nonzero for SQL Server Agent monitoring.
7. Do not implement cleanup, deletion, or retention. A separate job will handle these tasks.

## Error handling across classes

- Each service catches expected operation-specific exceptions, records a structured failure with service, database, stage, exception message, and timestamp, and logs the exception through the injected Serilog-compatible `ILogger<T>`.
- A shared `BackupErrorHandler` centralizes safe exception formatting and structured error logging. Do not duplicate exception formatting logic across services.
- Return typed operation results to `BackupJobRunner` for expected per-database failures. Preserve exception details in logs while keeping the email summary concise and free of credentials.
- `BackupJobRunner` aggregates all results and continues with the next database. Unexpected fatal startup errors are logged and result in a nonzero exit code.
- Avoid logging the same exception repeatedly at multiple layers; record full details once and pass structured failure results upward.

## Reliability and security

- Prefer sequential processing for predictable resource usage.
- Use cancellation tokens and configurable SQL command timeout long enough for backups.
- Do not use `TrustServerCertificate=True` as the production default.
- Ensure Windows permissions for SQL backup write, SMB share read and local ZIP/log write; SFTP permissions for upload/rename.
- Handle SQL Server Agent CmdExec account/proxy permissions explicitly when scheduled from a remote or local host.
- Do not put credentials in logs or email. Validate SFTP host-key fingerprint against a trusted out-of-band value.
- Native SQL backup compression plus ZIP often yields little additional reduction; make native compression configurable, default to ZIP-only for this requirement.
- SQL Server backup files are sensitive: restrict folder permissions and recommend encryption at rest and in transit. ZIP alone is **not** encryption.
- All local and remote cleanup/retention is out of scope.
- An email failure must not erase or overwrite database backup results.

## Expected output from the coding assistant

Generate a **compilable .NET 9 solution** with all source files, project files, sample config, README, unit tests for orchestration and failure handling, and a sample SQL Server Agent CmdExec job-step command. Use interfaces and constructor DI; no large all-in-one class.

Implement config validation, clear error messages, secure credential handling, and a testable SFTP client abstraction. Include examples of these scenarios in tests: all succeed; SQL backup fails for one database; ZIP fails; SFTP fails; email fails; and missing UNC share. Document how to publish a self-contained Windows executable and how to configure SQL Server Agent to run it.

## Acceptance criteria

- Changing SQL connection, database names, temp paths, SFTP destination, SMTP settings or recipients requires **config changes only**, not code changes.
- Exactly one ZIP per successfully backed-up database.
- Each ZIP includes only the matching current-run `.bak`.
- No automatic retries; one failed database does not prevent remaining databases from running.
- One log file and at most one consolidated email per run.
- Full per-database success/failure reporting.
- The application never deletes `.bak`, `.zip`, or log files; cleanup is out of scope.
- Compatible with execution from a different Windows PC via properly permissioned UNC paths.
- Cleanly separated classes and business orchestration, with unit tests.


## Runtime and deployment target

Target `net9.0` as requested. Note: .NET 9 reached end of support in May 2026; use a supported LTS release for production where possible. To avoid installing a separate .NET runtime on the Windows PC, publish a self-contained Windows executable, for example:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The .NET SDK is required on the build machine, but not on the target PC for a self-contained deployment. Validate third-party NuGet package compatibility with net9.0.
