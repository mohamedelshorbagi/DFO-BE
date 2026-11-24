using System.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dfo.Tests;

public class MediaSignerTests
{
    private static MediaUrlSigner Make(string key = "k1-0123456789abcdef0123456789abcdef") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Media:SigningKey"] = key }).Build());

    private static (string q, long exp, string sig) Parse(string url)
    {
        var qs = HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
        return (qs["q"]!, long.Parse(qs["exp"]!), qs["sig"]!);
    }

    [Fact]
    public void Fresh_url_validates()
    {
        var s = Make(); var id = Guid.NewGuid();
        var (q, exp, sig) = Parse(s.Url(id, high: false));
        Assert.Equal("low", q);
        Assert.True(s.Valid(id, q, exp, sig));
    }

    [Fact]
    public void Low_res_signature_cannot_be_upgraded_to_high()
    {
        var s = Make(); var id = Guid.NewGuid();
        var (_, exp, sig) = Parse(s.Url(id, high: false));
        Assert.False(s.Valid(id, "high", exp, sig));
    }

    [Fact]
    public void Signature_is_bound_to_the_image_id()
    {
        var s = Make();
        var (q, exp, sig) = Parse(s.Url(Guid.NewGuid(), true));
        Assert.False(s.Valid(Guid.NewGuid(), q, exp, sig));
    }

    [Fact]
    public void Expired_url_is_rejected()
    {
        var s = Make(); var id = Guid.NewGuid();
        var (q, exp, sig) = Parse(s.Url(id, true));
        Assert.False(s.Valid(id, q, exp - (long)MediaUrlSigner.Ttl.TotalSeconds - 10, sig));   // also breaks sig, but expiry alone must fail too
        Assert.False(s.Valid(id, q, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(), sig));
    }

    [Fact]
    public void Different_key_or_tampered_signature_is_rejected()
    {
        var id = Guid.NewGuid();
        var (q, exp, sig) = Parse(Make("key-A-0123456789abcdef0123456789abcd").Url(id, true));
        Assert.False(Make("key-B-0123456789abcdef0123456789abcd").Valid(id, q, exp, sig));
        Assert.False(Make("key-A-0123456789abcdef0123456789abcd").Valid(id, q, exp, sig[..^1] + (sig[^1] == '0' ? '1' : '0')));
        Assert.False(Make().Valid(id, q, exp, ""));
    }

    [Fact]
    public void Unknown_quality_is_rejected()
    {
        var s = Make(); var id = Guid.NewGuid();
        var (_, exp, sig) = Parse(s.Url(id, true));
        Assert.False(s.Valid(id, "original", exp, sig));
    }
}
