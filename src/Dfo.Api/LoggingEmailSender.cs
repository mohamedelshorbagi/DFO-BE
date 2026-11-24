using Dfo.Application;

/// <summary>
/// Placeholder until a real provider (SMTP / SendGrid / SES) is wired in. In Development it prints the
/// whole message so the reset link can be copied; elsewhere it logs only the recipient and subject,
/// because a reset link is a credential and must never end up in production logs.
/// </summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> log, IHostEnvironment env) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (env.IsDevelopment()) log.LogInformation("EMAIL to {To} | {Subject}\n{Body}", to, subject, body);
        else log.LogWarning("EMAIL NOT SENT (no provider configured) to {To} | {Subject}", to, subject);
        return Task.CompletedTask;
    }
}
