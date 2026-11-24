using System.Net;
using System.Net.Sockets;
using System.Text;
using Dfo.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace Dfo.Tests;

/// <summary>Minimal in-process SMTP server so the real MailKit code path is exercised without credentials or network.</summary>
internal sealed class FakeSmtp : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public List<byte[]> Messages { get; } = new();
    public int Connections;
    public string RcptReply = "250 OK";
    public int FailFirstConnections;          // greet with "421" this many times (a transient failure)
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

    public FakeSmtp()
    {
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                var n = Interlocked.Increment(ref Connections);
                _ = Task.Run(() => Serve(c, n));
            }
        });
    }

    private void Serve(TcpClient c, int n)
    {
        using var client = c;
        using var s = client.GetStream();
        var r = new StreamReader(s, Encoding.Latin1);   // Latin1 keeps bytes intact for MimeKit to re-parse
        var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        try
        {
            if (n <= FailFirstConnections) { w.WriteLine("421 4.3.2 Service not available"); return; }
            w.WriteLine("220 stub ESMTP");
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (cmd)
                {
                    case "EHLO": case "HELO": w.WriteLine("250 stub"); break;
                    case "MAIL": w.WriteLine("250 OK"); break;
                    case "RCPT": w.WriteLine(RcptReply); break;
                    case "DATA":
                        w.WriteLine("354 end with <CRLF>.<CRLF>");
                        var sb = new StringBuilder();
                        while ((line = r.ReadLine()) is not null && line != ".")
                            sb.Append(line.StartsWith("..") ? line[1..] : line).Append("\r\n");
                        lock (Messages) Messages.Add(Encoding.Latin1.GetBytes(sb.ToString()));
                        w.WriteLine("250 queued");
                        break;
                    case "QUIT": w.WriteLine("221 bye"); return;
                    default: w.WriteLine("250 OK"); break;
                }
            }
        }
        catch (IOException) { }
    }

    public MimeMessage Last()
    {
        lock (Messages) return MimeMessage.Load(new MemoryStream(Messages[^1]));
    }

    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

public class SmtpEmailSenderTests
{
    private static SmtpEmailSender Make(FakeSmtp smtp, int attempts = 3) => new(
        Options.Create(new SmtpOptions
        {
            Host = "127.0.0.1", Port = smtp.Port, Security = "None",
            FromAddress = "noreply@dfo.test", FromName = "DFO", MaxAttempts = attempts, TimeoutSeconds = 5
        }),
        NullLogger<SmtpEmailSender>.Instance);

    [Fact]
    public async Task Delivers_a_message_with_correct_headers_and_arabic_body()
    {
        using var smtp = new FakeSmtp();
        var body = "Reset: https://dfo.eg/reset-password?email=a%40b.com&token=abc\nلإعادة تعيين كلمة المرور";
        await Make(smtp).SendAsync("user@example.com", "Reset your DFO password", body);

        var m = smtp.Last();
        Assert.Equal("user@example.com", m.To.Mailboxes.Single().Address);
        Assert.Equal("noreply@dfo.test", m.From.Mailboxes.Single().Address);
        Assert.Equal("Reset your DFO password", m.Subject);
        var text = m.TextBody.Replace("\r\n", "\n");
        Assert.Contains("token=abc", text);
        Assert.Contains("لإعادة تعيين كلمة المرور", text);   // survives the UTF-8 round trip
    }

    [Fact]
    public async Task Line_breaks_in_the_subject_cannot_inject_headers()
    {
        using var smtp = new FakeSmtp();
        await Make(smtp).SendAsync("user@example.com", "Hello\r\nBcc: attacker@evil.test", "body");

        var m = smtp.Last();
        Assert.Empty(m.Bcc);
        Assert.False(m.Headers.Contains(HeaderId.Bcc));
        Assert.Single(m.Headers, h => h.Id == HeaderId.Subject);
    }

    [Theory]
    [InlineData("a@x.com, b@y.com")]
    [InlineData("a@x.com\r\nBcc: evil@y.com")]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task Rejects_anything_but_a_single_valid_recipient_without_connecting(string to)
    {
        using var smtp = new FakeSmtp();
        await Assert.ThrowsAsync<PermanentEmailException>(() => Make(smtp).SendAsync(to, "s", "b"));
        Assert.Equal(0, smtp.Connections);
    }

    [Fact]
    public async Task Rejected_recipient_is_permanent_and_is_not_retried()
    {
        using var smtp = new FakeSmtp { RcptReply = "550 5.1.1 No such user" };
        await Assert.ThrowsAsync<PermanentEmailException>(() => Make(smtp).SendAsync("ghost@example.com", "s", "b"));
        Assert.Equal(1, smtp.Connections);   // one attempt only
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_then_succeeds()
    {
        using var smtp = new FakeSmtp { FailFirstConnections = 1 };
        await Make(smtp, attempts: 2).SendAsync("user@example.com", "s", "b");
        Assert.Equal(2, smtp.Connections);
        Assert.Single(smtp.Messages);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_with_a_non_permanent_error()
    {
        using var smtp = new FakeSmtp { FailFirstConnections = 99 };
        var ex = await Record.ExceptionAsync(() => Make(smtp, attempts: 2).SendAsync("user@example.com", "s", "b"));
        Assert.NotNull(ex);
        Assert.IsNotType<PermanentEmailException>(ex);   // caller may retry later
        Assert.Equal(2, smtp.Connections);
        Assert.Empty(smtp.Messages);
    }
}

public class SmtpOptionsTests
{
    [Fact]
    public void Unconfigured_is_valid_and_reports_not_configured()
    {
        var o = new SmtpOptions();
        o.Validate();
        Assert.False(o.IsConfigured);
    }

    [Fact]
    public void Host_without_a_valid_from_address_fails_fast() =>
        Assert.Throws<InvalidOperationException>(() => new SmtpOptions { Host = "smtp.x.test", FromAddress = "nope" }.Validate());

    [Fact]
    public void Credentials_over_plaintext_are_refused() =>
        Assert.Throws<InvalidOperationException>(() =>
            new SmtpOptions { Host = "smtp.x.test", FromAddress = "a@b.test", Username = "u", Password = "p", Security = "None" }.Validate());

    [Fact]
    public void Starttls_with_credentials_is_accepted() =>
        new SmtpOptions { Host = "smtp.x.test", FromAddress = "a@b.test", Username = "u", Password = "p" }.Validate();

    [Theory]
    [InlineData("StartTls", MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData("SslOnConnect", MailKit.Security.SecureSocketOptions.SslOnConnect)]
    [InlineData("none", MailKit.Security.SecureSocketOptions.None)]
    [InlineData("garbage", MailKit.Security.SecureSocketOptions.StartTls)]   // unknown value falls back to the safe choice
    public void Security_mode_parses(string v, MailKit.Security.SecureSocketOptions expected) =>
        Assert.Equal(expected, new SmtpOptions { Security = v }.SocketOptions);
}
