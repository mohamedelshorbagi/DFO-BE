using System.Web;
using Dfo.Application;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dfo.Tests;

public class ViewingDefaultsTests
{
    [Fact]
    public void Default_hours_cover_every_day_so_a_new_listing_is_bookable()
    {
        var rules = ViewingRules.DefaultRules("owner-1");
        Assert.Equal(7, rules.Select(r => r.DayOfWeek).Distinct().Count());
        Assert.All(rules, r => Assert.Equal(60, r.SlotDurationMin));
        Assert.Equal(new TimeOnly(14, 0), rules.Single(r => r.DayOfWeek == 1).StartTime);   // Monday
        Assert.Equal(new TimeOnly(11, 0), rules.Single(r => r.DayOfWeek == 5).StartTime);   // Friday
        Assert.Equal(new TimeOnly(18, 0), rules.Single(r => r.DayOfWeek == 6).EndTime);     // Saturday
    }

    [Fact]
    public void Default_hours_produce_slots()
    {
        var monday = new DateOnly(2030, 1, 7);
        var slots = ViewingRules.GenerateSlots(monday, ViewingRules.DefaultRules("o"), [], [], new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(4, slots.Count);   // 14:00-18:00 in 60-minute slots
    }
}

public class DocumentLinkTests
{
    private static MediaUrlSigner Make(string key = "doc-key-0123456789abcdef0123456789abcdef") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Media:SigningKey"] = key }).Build());

    private static (long exp, string sig) Parse(string url)
    {
        var qs = HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
        return (long.Parse(qs["exp"]!), qs["sig"]!);
    }

    [Fact]
    public void A_fresh_link_is_valid_for_its_document_only()
    {
        var s = Make(); var id = Guid.NewGuid();
        var (exp, sig) = Parse(s.DocUrl(id));
        Assert.True(s.ValidDoc(id, exp, sig));
        Assert.False(s.ValidDoc(Guid.NewGuid(), exp, sig));
    }

    [Fact]
    public void Links_expire_and_cannot_be_forged()
    {
        var s = Make(); var id = Guid.NewGuid();
        var (exp, sig) = Parse(s.DocUrl(id));
        Assert.False(s.ValidDoc(id, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(), sig));
        Assert.False(s.ValidDoc(id, exp, ""));
        Assert.False(s.ValidDoc(id, exp, null));
        Assert.False(Make("other-key-0123456789abcdef0123456789abcd").ValidDoc(id, exp, sig));
    }

    [Fact]
    public void A_document_link_cannot_be_reused_as_an_image_link()
    {
        var s = Make(); var id = Guid.NewGuid();
        var (exp, sig) = Parse(s.DocUrl(id));
        Assert.False(s.Valid(id, "high", exp, sig));
        Assert.False(s.Valid(id, "low", exp, sig));
    }

    [Fact]
    public void Document_links_last_ten_minutes()
    {
        var s = Make();
        var (exp, _) = Parse(s.DocUrl(Guid.NewGuid()));
        var seconds = exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(seconds, 590, 601);
    }
}
