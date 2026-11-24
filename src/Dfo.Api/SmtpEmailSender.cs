using System.Net.Sockets;
using Dfo.Application;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>StartTls (port 587, default) | SslOnConnect (port 465) | None (local test servers only).</summary>
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";           // supply via env var Smtp__Password or user-secrets, never in a committed file
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "DFO";
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxAttempts { get; set; } = 3;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public SecureSocketOptions SocketOptions => Security.ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "ssl" or "sslonconnect" => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTls
    };

    /// <summary>Fails fast at startup instead of on the first password-reset email.</summary>
    public void Validate()
    {
        if (!IsConfigured) return;
        if (!SmtpEmailSender.TryParseMailbox(FromAddress, out _))
            throw new InvalidOperationException("Smtp:FromAddress must be a valid email address when Smtp:Host is set.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Smtp:Port is out of range.");
        if (!string.IsNullOrEmpty(Username) && SocketOptions == SecureSocketOptions.None)
            throw new InvalidOperationException("Refusing to send SMTP credentials without TLS. Use Smtp:Security=StartTls or SslOnConnect.");
    }
}

public class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var o = options.Value;
        var message = Build(o, to, subject, body);   // throws PermanentEmailException for an unusable address

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = new SmtpClient { Timeout = o.TimeoutSeconds * 1000 };
                await client.ConnectAsync(o.Host, o.Port, o.SocketOptions, ct);
                if (!string.IsNullOrEmpty(o.Username)) await client.AuthenticateAsync(o.Username, o.Password, ct);
                await client.SendAsync(message, ct);
                await client.DisconnectAsync(true, ct);
                log.LogInformation("Email sent to {To} | {Subject}", to, message.Subject);   // never the body: it can hold a reset link
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 5xx replies mean "this will never work" (bad recipient, auth refused): don't retry, don't pretend.
                if (IsPermanent(ex))
                {
                    log.LogError("Email to {To} permanently rejected: {Reason}", to, ex.Message);
                    throw new PermanentEmailException($"Mail server rejected the message: {ex.Message}", ex);
                }
                if (attempt >= o.MaxAttempts)
                {
                    log.LogError(ex, "Email to {To} failed after {Attempts} attempts", to, attempt);
                    throw;
                }
                log.LogWarning("Email to {To} failed (attempt {Attempt}): {Reason}. Retrying.", to, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt * attempt), ct);   // 1s, 4s, ...
            }
        }
    }

    public static MimeMessage Build(SmtpOptions o, string to, string subject, string body)
    {
        // TryParse accepts exactly one mailbox, so "a@x.com, b@y.com" or "a@x.com\r\nBcc: ..." cannot add recipients.
        if (!TryParseMailbox(to, out var rcpt))
            throw new PermanentEmailException("Recipient address is not a valid email address.");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        msg.To.Add(rcpt);
        // Line breaks in a subject would inject extra headers.
        msg.Subject = new string((subject ?? "").Where(c => c != '\r' && c != '\n').ToArray()).Trim();
        msg.Body = new TextPart("plain") { Text = body ?? "" };   // UTF-8 encoded automatically (Arabic is fine)
        return msg;
    }

    /// <summary>
    /// Exactly one mailbox of the form name@domain.tld. MimeKit alone also accepts bare names like "bob"
    /// (legal for local delivery), which we never want for a public-facing site.
    /// </summary>
    public static bool TryParseMailbox(string? value, out MailboxAddress mailbox)
    {
        mailbox = null!;
        if (string.IsNullOrWhiteSpace(value) || !MailboxAddress.TryParse(value, out var m)) return false;
        var at = m.Address.LastIndexOf('@');
        if (at < 1) return false;
        var domain = m.Address[(at + 1)..];
        if (!domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.')) return false;
        mailbox = m;
        return true;
    }

    private static bool IsPermanent(Exception ex) => ex switch
    {
        SmtpCommandException c => (int)c.StatusCode >= 500,
        AuthenticationException => true,
        _ => false
    };
}

/// <summary>Sends saved-search alerts as email. WhatsApp delivery is not implemented yet and is reported, not silently dropped.</summary>
public class EmailAlertNotifier(IEmailSender email, ILogger<EmailAlertNotifier> log) : IAlertNotifier
{
    public Task SendAsync(AlertMessage m, CancellationToken ct)
    {
        if (m.WhatsAppPhone is not null) log.LogWarning("WhatsApp alerts are not configured; sending email only.");
        return email.SendAsync(m.Email, m.Subject, m.Body, ct);
    }
}
