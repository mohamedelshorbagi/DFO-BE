using Dfo.Application;
using Xunit;

namespace Dfo.Tests;

public class ResetLinkTests
{
    [Fact]
    public void Builds_a_link_to_the_frontend_reset_page()
    {
        var link = ResetLink.Build("https://dfo.eg/", "a@b.com", "tok");
        Assert.Equal("https://dfo.eg/reset-password?email=a%40b.com&token=tok", link);
    }

    [Fact]
    public void Url_encodes_tokens_that_contain_reserved_characters()
    {
        // Identity tokens are base64 and can contain '+', '/' and '='; unencoded they get corrupted in transit.
        var link = ResetLink.Build("http://localhost:4200", "a+b@c.com", "ab+/=cd");
        Assert.Contains("email=a%2Bb%40c.com", link);
        Assert.Contains("token=ab%2B%2F%3Dcd", link);
        Assert.DoesNotContain("token=ab+", link);
    }
}
