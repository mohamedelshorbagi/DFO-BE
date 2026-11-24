namespace Dfo.Application;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>The mail server definitively refused this message (bad address, rejected). Retrying will not help.</summary>
public class PermanentEmailException(string message, Exception? inner = null) : Exception(message, inner);

public static class ResetLink
{
    /// <summary>Frontend page that collects the new password. The token travels in the query string of an emailed link, as is standard.</summary>
    public static string Build(string frontendBaseUrl, string email, string token) =>
        $"{frontendBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
}
