using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SqlBackupManager.Configuration;
using SqlBackupManager.ErrorHandling;
using SqlBackupManager.Interfaces;
using SqlBackupManager.Models;

namespace SqlBackupManager.Services;

/// <summary>
/// Sends a consolidated HTML/plain-text summary email after processing all databases,
/// using MailKit. Optionally attaches the per-run log file.
/// </summary>
public sealed class EmailService : IEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailService> _logger;
    private readonly BackupErrorHandler _errorHandler;

    public EmailService(
        IOptions<EmailOptions> options,
        ILogger<EmailService> logger,
        BackupErrorHandler errorHandler)
    {
        _options = options.Value;
        _logger = logger;
        _errorHandler = errorHandler;
    }

    public async Task<bool> SendSummaryEmailAsync(
        BackupRunResult runResult,
        CancellationToken cancellationToken = default)
    {
        // Determine whether to send based on success/failure policy
        var shouldSend = runResult.AllSucceeded
            ? _options.SendOnSuccess
            : _options.SendOnFailure;

        if (!shouldSend)
        {
            _logger.LogInformation(
                "Email summary skipped (SendOn{Condition} is false)",
                runResult.AllSucceeded ? "Success" : "Failure");
            return false;
        }

        try
        {
            var message = BuildMessage(runResult);
            await SendAsync(message, cancellationToken);

            _logger.LogInformation(
                "Summary email sent to {Recipients}: Subject = {Subject}",
                string.Join(", ", _options.Recipients),
                message.Subject);

            return true;
        }
        catch (Exception ex)
        {
            // Email failure is logged independently and does not erase backup results
            _logger.LogError(ex, "Failed to send summary email: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Builds the MimeMessage with both plain-text and HTML bodies, and optional log attachment.
    /// </summary>
    private MimeMessage BuildMessage(BackupRunResult runResult)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));

        foreach (var recipient in _options.Recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        // Subject: [SQL Backup] SUCCESS | PARTIAL | FAILED - <RunId>
        message.Subject = $"[SQL Backup] {runResult.StatusLabel} - {runResult.RunId}";

        var multipart = new Multipart("mixed");

        // Build text and HTML bodies
        var (plainText, html) = BuildBodies(runResult);

        var bodyPart = new Multipart("alternative")
        {
            new TextPart("plain") { Text = plainText },
            new TextPart("html") { Text = html }
        };
        multipart.Add(bodyPart);

        // Attach log file if enabled and available
        if (_options.AttachLogFile &&
            !string.IsNullOrEmpty(runResult.LogFilePath) &&
            File.Exists(runResult.LogFilePath))
        {
            var attachment = new MimePart("text", "plain")
            {
                Content = new MimeContent(File.OpenRead(runResult.LogFilePath!)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                FileName = Path.GetFileName(runResult.LogFilePath)
            };
            multipart.Add(attachment);
        }

        message.Body = multipart;
        return message;
    }

    /// <summary>
    /// Builds both plain-text and HTML versions of the summary body.
    /// </summary>
    private (string plainText, string html) BuildBodies(BackupRunResult runResult)
    {
        var sb = new StringBuilder();

        // Plain text
        sb.Clear();
        sb.AppendLine($"SQL Backup Manager — Run Summary");
        sb.AppendLine($"Run ID: {runResult.RunId}");
        sb.AppendLine($"Status: {runResult.StatusLabel}");
        sb.AppendLine($"Started (UTC): {runResult.StartedAtUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Finished (UTC): {runResult.EndedAtUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Elapsed: {runResult.Elapsed:hh\\:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine($"Databases: {runResult.TotalCount} total, {runResult.SuccessCount} succeeded, {runResult.FailureCount} failed");
        sb.AppendLine();
        sb.AppendLine("Per-database results:");
        sb.AppendLine(new string('-', 80));

        foreach (var db in runResult.DatabaseResults)
        {
            var status = db.Success ? "SUCCESS" : "FAILED";
            sb.AppendLine($"  [{status}] {db.DatabaseName} ({db.Elapsed:hh\\:mm\\:ss})");
            sb.AppendLine($"    Stage: {db.Stage}");

            if (db.BakFileSizeBytes.HasValue)
                sb.AppendLine($"    .bak size: {FormatBytes(db.BakFileSizeBytes.Value)}");
            if (db.ZipFileSizeBytes.HasValue)
                sb.AppendLine($"    .zip size: {FormatBytes(db.ZipFileSizeBytes.Value)}");
            if (db.Success && !string.IsNullOrEmpty(db.RemoteZipPath))
                sb.AppendLine($"    Remote: {db.RemoteZipPath}");
            if (!db.Success && !string.IsNullOrEmpty(db.ErrorMessage))
                sb.AppendLine($"    Error: {BackupErrorHandler.RedactSensitiveData(db.ErrorMessage)}");

            sb.AppendLine();
        }

        if (!runResult.EmailSent && !string.IsNullOrEmpty(runResult.EmailError))
        {
            sb.AppendLine($"Email notification error: {BackupErrorHandler.RedactSensitiveData(runResult.EmailError)}");
        }

        var plainText = sb.ToString();

        // HTML
        sb.Clear();
        sb.AppendLine("<html><body style=\"font-family: 'Segoe UI', Arial, sans-serif; font-size: 14px;\">");
        sb.AppendLine("<h2>SQL Backup Manager — Run Summary</h2>");
        sb.AppendLine("<table style=\"border-collapse: collapse; margin-bottom: 16px;\">");

        AppendHtmlRow(sb, "Run ID", runResult.RunId);
        AppendHtmlRow(sb, "Status", $"<strong>{runResult.StatusLabel}</strong>");

        var statusColor = runResult.AllSucceeded ? "#2E7D32" : runResult.IsPartial ? "#F57F17" : "#C62828";
        AppendHtmlRow(sb, "Started (UTC)", runResult.StartedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"));
        AppendHtmlRow(sb, "Finished (UTC)", runResult.EndedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"));
        AppendHtmlRow(sb, "Elapsed", runResult.Elapsed.ToString(@"hh\:mm\:ss"));
        AppendHtmlRow(sb, "Databases",
            $"{runResult.TotalCount} total, {runResult.SuccessCount} succeeded, {runResult.FailureCount} failed");

        sb.AppendLine("</table>");
        sb.AppendLine("<h3>Per-database results</h3>");
        sb.AppendLine("<table style=\"border-collapse: collapse; width: 100%;\">");
        sb.AppendLine("<tr style=\"background-color: #f0f0f0; text-align: left;\">");
        sb.AppendLine("<th style=\"padding: 6px; border: 1px solid #ddd;\">Database</th>");
        sb.AppendLine("<th style=\"padding: 6px; border: 1px solid #ddd;\">Status</th>");
        sb.AppendLine("<th style=\"padding: 6px; border: 1px solid #ddd;\">Stage</th>");
        sb.AppendLine("<th style=\"padding: 6px; border: 1px solid #ddd;\">Elapsed</th>");
        sb.AppendLine("<th style=\"padding: 6px; border: 1px solid #ddd;\">.bak Size</th>");
        sb.AppendLine("<th style=\"padding: 6px; border: 1px solid #ddd;\">.zip Size</th>");
        sb.AppendLine("<th style=\"padding: 6px; border: 1px solid #ddd;\">Error</th>");
        sb.AppendLine("</tr>");

        foreach (var db in runResult.DatabaseResults)
        {
            var statusColorDb = db.Success ? "#2E7D32" : "#C62828";
            var statusText = db.Success ? "SUCCESS" : "FAILED";
            var errorMsg = db.Success || string.IsNullOrEmpty(db.ErrorMessage)
                ? ""
                : BackupErrorHandler.RedactSensitiveData(db.ErrorMessage);

            sb.AppendLine("<tr>");
            sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd;\">{EscapeHtml(db.DatabaseName)}</td>");
            sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd; color: {statusColorDb};\"><strong>{statusText}</strong></td>");
            sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd;\">{db.Stage}</td>");
            sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd;\">{db.Elapsed:hh\\:mm\\:ss}</td>");
            sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd;\">{FormatBytes(db.BakFileSizeBytes)}</td>");
            sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd;\">{FormatBytes(db.ZipFileSizeBytes)}</td>");
            sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd;\">{EscapeHtml(errorMsg)}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</table>");

        if (!runResult.EmailSent && !string.IsNullOrEmpty(runResult.EmailError))
        {
            sb.AppendLine("<p style=\"color: #C62828;\"><strong>Email notification error:</strong> ");
            sb.AppendLine(EscapeHtml(BackupErrorHandler.RedactSensitiveData(runResult.EmailError)));
            sb.AppendLine("</p>");
        }

        sb.AppendLine("</body></html>");
        var html = sb.ToString();

        return (plainText, html);
    }

    /// <summary>
    /// Sends the message via SMTP with the configured security settings.
    /// </summary>
    private async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();

        var secureOption = _options.Security.ToLowerInvariant() switch
        {
            "none" => SecureSocketOptions.None,
            "starttls" => SecureSocketOptions.StartTls,
            "ssltls" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls
        };

        await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secureOption, cancellationToken);

        // Authenticate if username is configured
        if (!string.IsNullOrWhiteSpace(_options.SmtpUsername))
        {
            if (string.IsNullOrEmpty(_options.SmtpPassword))
            {
                throw new InvalidOperationException(
                    "SMTP authentication is configured (SmtpUsername is set) but SmtpPassword is not configured in user secrets.");
            }

            await client.AuthenticateAsync(_options.SmtpUsername, _options.SmtpPassword, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static void AppendHtmlRow(StringBuilder sb, string label, string value)
    {
        sb.AppendLine("<tr>");
        sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd; font-weight: bold;\">{label}</td>");
        sb.AppendLine($"<td style=\"padding: 6px; border: 1px solid #ddd;\">{value}</td>");
        sb.AppendLine("</tr>");
    }

    private static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue)
            return "—";

        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var len = (double)bytes.Value;
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
