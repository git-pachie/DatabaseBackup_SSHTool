namespace SqlBackupManager.Configuration;

/// <summary>
/// SMTP email settings for the consolidated run-summary email.
/// SMTP password is stored in user secrets alongside the rest of the Email config.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; } = true;

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// When true, enables SSL/TLS encryption. On port 587 uses STARTTLS;
    /// on port 465 uses implicit SSL.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    public string From { get; set; } = string.Empty;

    /// <summary>
    /// List of recipient email addresses.
    /// </summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>
    /// Send the summary email even when all databases succeed.
    /// </summary>
    public bool SendOnSuccess { get; set; } = true;

    /// <summary>
    /// Send the summary email when any database fails.
    /// </summary>
    public bool SendOnFailure { get; set; } = true;

    /// <summary>
    /// Attach the per-run log file to the email.
    /// </summary>
    public bool AttachLogFile { get; set; } = true;

    /// <summary>
    /// Optional: SMTP username for authenticated SMTP. When null, sends unauthenticated.
    /// </summary>
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// Optional: SMTP password for authenticated SMTP. Stored in user secrets.
    /// </summary>
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// When true, skips certificate revocation checking (CRL/OCSP).
    /// The certificate chain and hostname are still validated.
    /// Use this when the application runs in a network where CRL endpoints
    /// are not reachable. Defaults to true for compatibility.
    /// </summary>
    public bool SkipCertificateRevocationCheck { get; set; } = true;
}
