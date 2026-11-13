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
    appsettings.json               # Non-secret configuration (committed)
    secrets.sample.json            # TEMPLATE for user secrets (placeholders only)
    Configuration/                 # Strongly typed options
    Models/                        # Result types and enums
    Interfaces/                    # Service contracts (testable)
    Services/                      # Implementations: SQL, ZIP, SFTP, Email
    ErrorHandling/                 # Centralized exception formatting + redaction
    Orchestration/                 # BackupJobRunner: per-database pipeline
    Utilities/                     # File name + path validation helpers
  tests/SqlBackupManager.Tests/    # xUnit + Moq tests
  README.md
```

## Dependencies

| Package | Purpose |
|---|---|
| `Microsoft.Data.SqlClient` | SQL Server backup commands |
| `SSH.NET` | SFTP upload with host-key verification |
| `MailKit` | SMTP email (`UseSsl` toggles STARTTLS / implicit SSL) |
| `SharpZipLib` | Password-protected (AES-256) ZIP creation |
| `Serilog` + `Serilog.Sinks.File` | Per-run logging |
| `Microsoft.Extensions.Configuration.UserSecrets` | Loads local user secrets |

## Configuration

### Where each setting lives

Configuration sources are loaded in this order. **A later source overrides an earlier one.**

| Order | Source | What goes here | Committed to git? |
|---|---|---|---|
| 1 | `appsettings.json` | Databases, backup paths, timeouts, ZIP/log directories | Yes |
| 2 | dotnet user secrets (**development machine**) | SQL connection string, **all** SFTP settings, **all** Email settings, **all passwords** | **No** |
| 3 | `appsettings.secrets.json` next to the exe (**servers**) | Same keys as user secrets | **No** (git-ignored) |
| 4 | Environment variables | Optional overrides of any key (see below) | No |

All passwords (`Sftp:Password`, `Sftp:KeyPassphrase`, `Email:SmtpPassword`, `SqlServer:Password`) are stored with the rest of their section, either in user secrets (development) or `appsettings.secrets.json` (server).

### Production servers: use `appsettings.secrets.json` (no .NET SDK needed)

`dotnet user-secrets` is an SDK tool and only works inside a project folder. **Do not use it on a server.** Running it there fails with `No .NET SDKs were found`.

On the server:

1. Copy `appsettings.secrets.sample.json` next to `SqlBackupManager.exe` and rename it to `appsettings.secrets.json`.
2. Replace every placeholder with your real value.
3. Restrict the file with NTFS permissions so only the account that runs the job (and administrators) can read it.

```powershell
icacls "C:\SqlBackupManager\publish\appsettings.secrets.json" /inheritance:r /grant:r "Administrators:F" "DOMAIN\svc-sqlbackup:R"
```

The file is optional. It uses the same structure as `secrets.sample.json`. It is plain text and is excluded from git.

### 1. appsettings.json (non-sensitive)

Contains only paths, database names, and timeouts:

```json
{
  "SqlServer": {
    "Databases": ["BSS", "OPS_DB", "TRM"],
    "BackupDirectoryOnSqlServer": "D:\\SQLBackups\\Temp",
    "BackupDirectoryFromApp": "\\\\SQLSERVER01\\SQLBackups$\\Temp",
    "UseNativeCompression": false,
    "CommandTimeoutSeconds": 3600
  },
  "Storage": {
    "LocalZipDirectory": "C:\\SqlBackupManager\\Zip",
    "LogDirectory": "C:\\SqlBackupManager\\Logs",
    "ZipPassword": "CHANGE_ME_ZIP_PASSWORD"
  }
}
```

#### ZIP password (`Storage:ZipPassword`)

Every ZIP is encrypted with this single password, using **AES-256**. It is read from `appsettings.json`.

- The value is **required**. The app refuses to start if it is empty or still `CHANGE_ME_ZIP_PASSWORD`.
- **Do not commit your real password.** `appsettings.json` is tracked by git. Keep the placeholder in the repository and set the real value only in the copy on the server. To keep it out of that file entirely, put `Storage:ZipPassword` in `appsettings.secrets.json` or the `Storage__ZipPassword` environment variable, which override it.
- **Keep the password somewhere safe.** If it is lost, the backups cannot be opened. Changing it does not re-encrypt ZIPs that already exist.
- **Opening the ZIPs:** AES-256 ZIPs open with 7-Zip, WinZip and similar tools. Windows Explorer's built-in "Extract all" cannot open them.
- The password is never written to logs or emails.

### 2. dotnet user secrets (sensitive)

Secrets are stored in `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` and are never committed to source control.

> **All values below are PLACEHOLDERS.** `sftp.example.com`, `backupuser`, `your-app@gmail.com`, `dba@example.com`, `your-sftp-password`, `your-gmail-app-password` and the fingerprint are examples. Replace every one with your real value before running.

> **User secrets are NOT encrypted.** They are plain text in your Windows profile. They keep secrets out of git, nothing more. Do not treat them as a vault.

> **User secrets belong to one Windows account.** They are only visible to the account that ran `dotnet user-secrets set`. If SQL Server Agent runs the job under a proxy account, set the secrets while logged in as that account (or use environment variables, see below).

A template file is provided at `src/SqlBackupManager/secrets.sample.json`. It is a reference only. The application does **not** read it.

#### Setup

Run from `src/SqlBackupManager`:

```powershell
# Initialize user secrets (adds a UserSecretsId to the .csproj; already done in this repo)
dotnet user-secrets init

# --- SQL Server ---
dotnet user-secrets set "SqlServer:ConnectionString" "Server=SQLSERVER01;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;"
# Only if using SQL authentication instead of Integrated Security:
# dotnet user-secrets set "SqlServer:Username" "sql-user"
# dotnet user-secrets set "SqlServer:Password" "sql-password"

# --- SFTP ---
dotnet user-secrets set "Sftp:Host" "sftp.example.com"
dotnet user-secrets set "Sftp:Port" "22"
dotnet user-secrets set "Sftp:Username" "backupuser"
dotnet user-secrets set "Sftp:Authentication" "Auto"
dotnet user-secrets set "Sftp:HostKeyFingerprint" "SHA256:REPLACE_WITH_VERIFIED_FINGERPRINT"
dotnet user-secrets set "Sftp:RemoteDirectory" "/database-backups/sqlserver/"
dotnet user-secrets set "Sftp:Password" "your-sftp-password"
# Only if using a private key instead of a password:
# dotnet user-secrets set "Sftp:PrivateKeyPath" "C:\keys\id_ed25519"
# dotnet user-secrets set "Sftp:KeyPassphrase" "key-passphrase"

# --- Email (Gmail example) ---
dotnet user-secrets set "Email:Enabled" "true"
dotnet user-secrets set "Email:SmtpHost" "smtp.gmail.com"
dotnet user-secrets set "Email:SmtpPort" "587"
dotnet user-secrets set "Email:UseSsl" "true"
dotnet user-secrets set "Email:From" "your-app@gmail.com"
dotnet user-secrets set "Email:SmtpUsername" "your-app@gmail.com"
dotnet user-secrets set "Email:SmtpPassword" "your-gmail-app-password"
dotnet user-secrets set "Email:Recipients:0" "dba@example.com"
dotnet user-secrets set "Email:Recipients:1" "it-support@example.com"
dotnet user-secrets set "Email:SendOnSuccess" "true"
dotnet user-secrets set "Email:SendOnFailure" "true"
dotnet user-secrets set "Email:AttachLogFile" "true"
```

Verify what is set (this prints values in plain text):

```powershell
dotnet user-secrets list
```

#### Sample secrets structure (`secrets.sample.json`)

```json
{
  "SqlServer": {
    "ConnectionString": "Server=SQLSERVER01;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;",
    "Username": null,
    "Password": null
  },
  "Sftp": {
    "Host": "sftp.example.com",
    "Port": 22,
    "Username": "backupuser",
    "Authentication": "Auto",
    "PrivateKeyPath": null,
    "HostKeyFingerprint": "SHA256:REPLACE_WITH_VERIFIED_FINGERPRINT",
    "RemoteDirectory": "/database-backups/sqlserver/",
    "Password": "your-sftp-password",
    "KeyPassphrase": null
  },
  "Email": {
    "Enabled": true,
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": 587,
    "UseSsl": true,
    "From": "your-app@gmail.com",
    "Recipients": ["dba@example.com", "it-support@example.com"],
    "SmtpUsername": "your-app@gmail.com",
    "SmtpPassword": "your-gmail-app-password",
    "SkipCertificateRevocationCheck": true,
    "SendOnSuccess": true,
    "SendOnFailure": true,
    "AttachLogFile": true
  }
}
```

#### SFTP authentication

- `Sftp:PrivateKeyPath` set: key authentication (`Sftp:KeyPassphrase` if the key has one).
- `Sftp:PrivateKeyPath` empty: password authentication using `Sftp:Password`.
- The app never falls back to a password if key authentication fails.

#### SFTP host key fingerprint

Host key verification is mandatory and cannot be disabled. `Sftp:HostKeyFingerprint` must match the server's SHA256 fingerprint. The `SHA256:` prefix is optional.

Get the real value from the server administrator, or from a machine you trust:

```powershell
ssh-keyscan -t ed25519 sftp.example.com | ssh-keygen -lf -
```

If the value does not match, the connection is rejected and the log shows `SFTP server host key fingerprint (SHA256): ...`. **Do not paste that value in blindly.** Confirm it with the server owner first, otherwise the check protects nothing.

#### Email (SMTP) settings

| Setting | Meaning |
|---|---|
| `Email:UseSsl` | `true` + port `465` = implicit SSL. `true` + any other port (e.g. `587`) = STARTTLS. `false` = no encryption. |
| `Email:SmtpUsername` / `Email:SmtpPassword` | If `SmtpUsername` is set, the app authenticates and `SmtpPassword` is required. |
| `Email:SkipCertificateRevocationCheck` | Default `true`. Skips only the CRL/OCSP revocation lookup, for networks that cannot reach the revocation servers. The certificate chain, expiry and hostname are still validated. Set to `false` for strict checking. |

> **Gmail:** use an **App Password**, not your account password. 2-Step Verification must be on. Create one at https://myaccount.google.com/apppasswords. Use your Gmail address as both `SmtpUsername` and `From`.

### 3. Environment variables (optional override)

Not required. Because environment variables are loaded last, any key can be overridden by an environment variable. Use `__` (double underscore) in place of `:`.

| Environment variable | Overrides secret |
|---|---|
| `Sftp__Password` | `Sftp:Password` |
| `Sftp__KeyPassphrase` | `Sftp:KeyPassphrase` |
| `SqlServer__Password` | `SqlServer:Password` |
| `Email__SmtpPassword` | `Email:SmtpPassword` |

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

1. Publish the self-contained executable to a folder accessible from the machine running the job, e.g. `C:\SqlBackupManager\publish\SqlBackupManager.exe`.
2. Keep `appsettings.json` next to the executable.
3. Create a **SQL Server Agent proxy** with a credential mapped to a Windows account that has:
   - Read access to the UNC backup share
   - Write access to the local ZIP and log directories
   - Access to the SFTP server and the SMTP server
4. **Provide the secrets.** Create `appsettings.secrets.json` next to the exe (see "Production servers" above) and restrict it to the proxy account. Do not use `dotnet user-secrets` on the server. Environment variables also work, but SQL Server Agent must be restarted to see new machine-level variables.
5. Create a SQL Server Agent job:
   - Type: **Operating system (CmdExec)**
   - Run as: **the proxy account** (not the SQL Server service account unless it has the permissions above)
   - Command: `C:\SqlBackupManager\publish\SqlBackupManager.exe`
6. Set a schedule (e.g. daily at 2:00 AM).

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

- **Never** commit real secrets to source control. `secrets.sample.json` must only ever contain placeholders. `Storage:ZipPassword` lives in `appsettings.json`, so commit only the placeholder.
- User secrets are plain text on disk. Restrict access to the Windows profile that holds them.
- **Always** verify the SFTP host key fingerprint against a trusted out-of-band value.
- **Never** use `TrustServerCertificate=True` as a production default for SQL connections.
- `Email:SkipCertificateRevocationCheck` defaults to `true`. Set it to `false` if your network can reach the revocation servers and you want strict checking.
- ZIP files are encrypted with AES-256 using `Storage:ZipPassword`. The temporary `.bak` files in the backup folder are **not** encrypted, so restrict access to that folder.
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
