using Xunit;

namespace Dfo.Tests;

public class RefreshPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Tokens_are_unique_and_url_safe()
    {
        var set = Enumerable.Range(0, 200).Select(_ => RefreshPolicy.NewToken()).ToHashSet();
        Assert.Equal(200, set.Count);
        Assert.All(set, t =>
        {
            Assert.True(t.Length >= 60);   // 48 random bytes
            Assert.DoesNotContain('+', t); Assert.DoesNotContain('/', t); Assert.DoesNotContain('=', t);
        });
    }

    [Fact]
    public void Hash_is_stable_hex_and_not_the_token()
    {
        var t = RefreshPolicy.NewToken();
        var h = RefreshPolicy.Hash(t);
        Assert.Equal(h, RefreshPolicy.Hash(t));
        Assert.Equal(64, h.Length);
        Assert.NotEqual(t, h);
        Assert.NotEqual(h, RefreshPolicy.Hash(t + "x"));
    }

    [Fact]
    public void Refresh_slides_forward_within_the_cap() =>
        Assert.Equal(T0.AddDays(5 + 14), RefreshPolicy.ExpiryFor(T0.AddDays(5), T0));

    [Fact]
    public void Session_can_never_outlive_the_absolute_cap()
    {
        // 25 days in, a sliding window would reach day 39, but the login is capped at day 30.
        Assert.Equal(T0.AddDays(30), RefreshPolicy.ExpiryFor(T0.AddDays(25), T0));
    }

    [Fact]
    public void First_token_gets_the_full_sliding_window() =>
        Assert.Equal(T0.AddDays(14), RefreshPolicy.ExpiryFor(T0, T0));
}
