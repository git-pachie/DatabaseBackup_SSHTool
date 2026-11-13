namespace SqlBackupManager.Configuration;

/// <summary>
/// SFTP destination settings. Password and key passphrase are stored in user secrets.
/// </summary>
public sealed class SftpOptions
{
    public const string SectionName = "Sftp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Authentication mode. When "Auto" (default), selects key-based auth
    /// when PrivateKeyPath is set, otherwise password-based.
    /// </summary>
    public string Authentication { get; set; } = "Auto";

    /// <summary>
    /// Path to SSH private key file. When null/empty, password authentication is used.
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Expected SSH host key fingerprint, e.g. "SHA256:base64data".
    /// This is the server's public host key, not the client's private key.
    /// Required in all modes; host-key verification is never disabled.
    /// </summary>
    public string HostKeyFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Remote directory where ZIP files are uploaded.
    /// </summary>
    public string RemoteDirectory { get; set; } = string.Empty;

    /// <summary>
    /// SFTP password for password authentication.
    /// Stored in user secrets. Required when PrivateKeyPath is null/empty.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Passphrase for the SSH private key.
    /// Stored in user secrets. Used when PrivateKeyPath is set and the key is passphrase-protected.
    /// </summary>
    public string? KeyPassphrase { get; set; }
}
