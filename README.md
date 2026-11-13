# SQL Backup Manager

A configurable .NET 9 C# console application that backs up selected Microsoft SQL Server databases, creates one ZIP per database, uploads each ZIP to an SFTP remote directory, writes a separate log file per run, and sends one consolidated SMTP email summary.

## Scope

- **Backup** selected databases using `BACKUP DATABASE` (full)
- **ZIP** each `.bak` file individually
- **SFTP upload** each ZIP to a configured remote directory
- **Log** to a per-run Serilog file with UTC timestamp and unique Run ID
- **Email** one consolidated summary per run
- **No** retries, cleanup, retention, or deletion — handled by a separate job

## Architecture

```
SqlBackupManager/
  src/SqlBackupManager/
    Program.cs                     # Entry point: config, DI, Serilog, exit codes
    appsettings.json               # Configuration (non-secret values only)
    Configuration/                  # Strongly typed options
    Models/                         # Result types and enums
    Interfaces/                     # Service contracts (testable)
    Services/                       # Implementations: SQL, ZIP, SFTP, Email
    ErrorHandling/                  # Centralized exception formatting + redaction
    Orchestration/                  # BackupJobRunner: per-database pipeline
    Utilities/                      # File name + path validation helpers
  tests/SqlBackupManager.Tests/     # xUnit + Moq tests
  README.md
```

## Dependencies

| Package | Purpose |
|---|---|
| `Microsoft.Data.SqlClient` | SQL Server backup commands |
| `SSH.NET` | SFTP upload with host-key verification |
| `MailKit` | SMTP email with TLS modes |
| `Serilog` + `Serilog.Sinks.File` | Per-run logging |

## Configuration

All non-secret configuration is in `appsettings.json`. Secrets come from environment variables.

### appsettings.json

```json
{
  "SqlServer": {
    "ConnectionString": "Server=SQLSERVER01;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;",
    "Databases": ["BSS", "OPS_DB", "TRM"],
    "BackupDirectoryOnSqlServer": "D:\\SQLBackups\\Temp",
    "BackupDirectoryFromApp": "\\\\SQLSERVER01\\SQLBackups$\\Temp",
    "UseNativeCompression": false,
    "CommandTimeoutSeconds": 3600
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

### Environment Variables (Secrets)

Set these in the environment running the application. Never put them in config files or code.

| Variable | Required When | Description |
|---|---|---|
| `Sftp__Password` | SFTP password auth | SFTP password |
| `Sftp__KeyPassphrase` | SFTP key auth + passphrase | SSH private key passphrase |
| `Sql__Password` | SQL Server username auth | SQL Server password |
| `Email__SmtpPassword` | SMTP username configured | SMTP password |

### Cross-machine file handling

`BACKUP DATABASE ... TO DISK` writes on the **SQL Server host**, not the application PC. The application reads the same physical folder via a UNC share.

- `BackupDirectoryOnSqlServer`: Local path where SQL Server writes the `.bak` (SQL Server service account needs write access)
- `BackupDirectoryFromApp`: UNC share path the application uses to read the same folder (application's Windows account needs read access)

Example:
- SQL Server writes: `D:\SQLBackups\Temp\BSS_20261113_142200_ABCD.bak`
- Application reads: `\\SQLSERVER01\SQLBackups$\Temp\BSS_20261113_142200_ABCD.bak`
- Application writes ZIP: `C:\SqlBackupManager\Zip\BSS_20261113_142200_ABCD.zip`
- SFTP destination: `/database-backups/sqlserver/BSS_20261113_142200_ABCD.zip`

## Build and Publish

### Build

```powershell
dotnet build SqlBackupManager.sln -c Release
```

### Publish self-contained Windows executable

No .NET runtime is needed on the target PC:

```powershell
dotnet publish src/SqlBackupManager -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output is in `src/SqlBackupManager/bin/Release/net9.0/win-x64/publish/`.

### Run tests

```powershell
dotnet test SqlBackupManager.sln --logger "console;verbosity=detailed"
```

## Scheduling with SQL Server Agent

Configure a SQL Server Agent job with a **CmdExec** job step pointing to the published executable.

### Steps

1. Publish the self-contained executable to a folder accessible from the SQL Server host (or the machine running the Agent), e.g. `C:\SqlBackupManager\publish\SqlBackupManager.exe`.
2. Copy `appsettings.json` next to the executable.
3. Set environment variables for secrets on the account running the Agent job (or configure the CmdExec proxy). Use a **SQL Server Agent proxy** with a credential mapped to a Windows domain account that has:
   - Read access to the UNC backup share
   - Write access to the local ZIP and log directories
   - Access to the SFTP server
   - Access to the SMTP server
4. Create a SQL Server Agent job:
   - Type: **Operating system (CmdExec)**
   - Run as: **The proxy account** (not the SQL Server service account unless it has the above permissions)
   - Command: `C:\SqlBackupManager\publish\SqlBackupManager.exe`
5. Set a schedule (e.g. daily at 2:00 AM).

### Sample CmdExec job-step command

```cmd
C:\SqlBackupManager\publish\SqlBackupManager.exe
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | All databases succeeded and reporting succeeded |
| 1 | One or more databases failed, or email was required but failed |
| 2 | Configuration validation error |
| 3 | Run cancelled (Ctrl+C) |
| 4 | Fatal startup error |

SQL Server Agent monitors nonzero exit codes as job failure.

## Security Notes

- **Never** put secrets in `appsettings.json` or commit them to source control.
- **Always** verify the SFTP host key fingerprint against a trusted out-of-band value.
- **Never** use `TrustServerCertificate=True` as a production default for SQL connections.
- ZIP files are **not** encrypted — use encryption at rest and in transit for backup files.
- Passwords, private keys, tokens, and connection string secrets are redacted from all logs and email summaries.
- The application does not delete `.bak`, `.zip`, or log files. A separate job handles cleanup/retention.

## Workflow

1. Generate Run ID + UTC timestamp; create per-run log file
2. Validate configuration, directory access, database list, host key, and secrets
3. For each configured database, sequentially:
   - Validate database name and existence
   - `BACKUP DATABASE ... WITH CHECKSUM` (optionally `COMPRESSION`) to a unique `.bak`
   - Verify backup file is accessible and nonempty; attempt `RESTORE VERIFYONLY`
   - ZIP the `.bak` into its own archive; verify ZIP integrity
   - Upload ZIP to SFTP (temp name, size check, rename to final)
   - Mark successful only after verified SFTP upload
   - On failure: record stage + error, continue to next database
4. Produce run summary
5. Send one consolidated email if enabled
6. Return exit code for monitoring

## .NET 9 Note

.NET 9 reached end of support in May 2026. For production, consider targeting a supported LTS release (.NET 8 or later). The code is compatible with minimal changes to `TargetFramework` in the `.csproj` files.
