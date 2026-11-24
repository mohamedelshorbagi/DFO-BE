using Dfo.Application;
using Dfo.Domain;
using Xunit;

namespace Dfo.Tests;

public class AlertMatcherTests
{
    private static Property Villa() => new()
    {
        Title = "Corner Villa", City = "New Cairo", Type = "villa", Purpose = Purpose.Sale,
        Category = Category.Residential, Price = 9_000_000, Location = "Fifth Settlement",
        Compound = "Mivida", ListingCode = "DH-2026-000001", Status = ListingStatus.Published
    };

    [Fact]
    public void Empty_criteria_match_any_published_listing() =>
        Assert.True(AlertMatcher.Matches(new SearchAlert(), Villa()));

    [Fact]
    public void Unpublished_listings_never_match()
    {
        var p = Villa(); p.Status = ListingStatus.PendingVerification;
        Assert.False(AlertMatcher.Matches(new SearchAlert(), p));
    }

    [Fact]
    public void Every_set_criterion_must_match()
    {
        var a = new SearchAlert { Purpose = Purpose.Sale, City = "new cairo", Type = "villa", MaxPrice = 10_000_000 };
        Assert.True(AlertMatcher.Matches(a, Villa()));
        a.MaxPrice = 5_000_000;
        Assert.False(AlertMatcher.Matches(a, Villa()));
        a.MaxPrice = null; a.Purpose = Purpose.Rent;
        Assert.False(AlertMatcher.Matches(a, Villa()));
    }

    [Fact]
    public void Price_bounds_are_inclusive()
    {
        var a = new SearchAlert { MinPrice = 9_000_000, MaxPrice = 9_000_000 };
        Assert.True(AlertMatcher.Matches(a, Villa()));
    }

    [Theory]
    [InlineData("mivida", true)]
    [InlineData("DH-2026-000001", true)]
    [InlineData("penthouse", false)]
    public void Free_text_searches_title_location_compound_and_code(string q, bool expected) =>
        Assert.Equal(expected, AlertMatcher.Matches(new SearchAlert { Query = q }, Villa()));
}

public class AlertDueTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Instant_is_always_due() =>
        Assert.True(AlertMatcher.IsDue(new SearchAlert { Frequency = AlertFrequency.Instant, LastCheckedAt = Now }, Now));

    [Fact]
    public void Daily_waits_24_hours()
    {
        var a = new SearchAlert { Frequency = AlertFrequency.Daily, LastCheckedAt = Now.AddHours(-23) };
        Assert.False(AlertMatcher.IsDue(a, Now));
        a.LastCheckedAt = Now.AddHours(-24);
        Assert.True(AlertMatcher.IsDue(a, Now));
    }

    [Fact]
    public void Weekly_waits_7_days()
    {
        var a = new SearchAlert { Frequency = AlertFrequency.Weekly, LastCheckedAt = Now.AddDays(-6) };
        Assert.False(AlertMatcher.IsDue(a, Now));
        a.LastCheckedAt = Now.AddDays(-7);
        Assert.True(AlertMatcher.IsDue(a, Now));
    }

    [Fact]
    public void Inactive_alerts_are_never_due() =>
        Assert.False(AlertMatcher.IsDue(new SearchAlert { Frequency = AlertFrequency.Instant, IsActive = false }, Now));
}
