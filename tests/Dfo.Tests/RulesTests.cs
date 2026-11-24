using Dfo.Application;
using Dfo.Domain;
using Xunit;

namespace Dfo.Tests;

public class ContactFilterTests
{
    [Theory]
    [InlineData("mail me at john@example.com please")]
    [InlineData("call me tomorrow")]
    [InlineData("my number is 01012345678")]
    [InlineData("رقمي ٠١٠١٢٣٤٥٦٧٨")]
    [InlineData("https://wa.me/201012345678")]
    [InlineData("ping @owner_ahmed")]
    public void Flags_contact_attempts(string text)
    {
        var r = ContactFilter.Filter(text);
        Assert.True(r.HasViolation);
        Assert.DoesNotContain("01012345678", r.Filtered);
        Assert.DoesNotContain("example.com", r.Filtered);
    }

    [Fact]
    public void Leaves_normal_text_alone()
    {
        var r = ContactFilter.Filter("Is the 3 bedroom apartment still available on Saturday?");
        Assert.False(r.HasViolation);
        Assert.Equal(r.Original, r.Filtered);
    }

    [Fact]
    public void Short_numbers_are_not_phone_numbers()
    {
        Assert.False(ContactFilter.Filter("It is 120 sqm with 3 rooms").HasViolation);
    }
}

public class ViewingRulesTests
{
    private static AvailabilityRule Rule(DayOfWeek d, string s, string e, int dur = 30) => new()
    {
        OwnerId = "o1", DayOfWeek = (int)d, StartTime = TimeOnly.Parse(s), EndTime = TimeOnly.Parse(e), SlotDurationMin = dur
    };

    // A Monday far in the future so "past" never interferes.
    private static readonly DateOnly Monday = new(2030, 1, 7);
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Generates_slots_from_weekly_rule()
    {
        var slots = ViewingRules.GenerateSlots(Monday, [Rule(DayOfWeek.Monday, "10:00", "12:00")], [], [], Now);
        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.True(s.Available));
    }

    [Fact]
    public void Other_weekday_has_no_slots()
    {
        var slots = ViewingRules.GenerateSlots(Monday.AddDays(1), [Rule(DayOfWeek.Monday, "10:00", "12:00")], [], [], Now);
        Assert.Empty(slots);
    }

    [Fact]
    public void Blocked_exception_removes_the_day()
    {
        var ex = new AvailabilityException { Date = Monday, Blocked = true };
        Assert.Empty(ViewingRules.GenerateSlots(Monday, [Rule(DayOfWeek.Monday, "10:00", "12:00")], [ex], [], Now));
    }

    [Fact]
    public void Custom_window_replaces_the_rule()
    {
        var ex = new AvailabilityException { Date = Monday, Blocked = false, StartTime = TimeOnly.Parse("14:00"), EndTime = TimeOnly.Parse("15:00"), SlotDurationMin = 20 };
        var slots = ViewingRules.GenerateSlots(Monday, [Rule(DayOfWeek.Monday, "10:00", "12:00")], [ex], [], Now);
        Assert.Equal(3, slots.Count);
    }

    [Fact]
    public void Booked_and_past_slots_are_unavailable()
    {
        var rules = new[] { Rule(DayOfWeek.Monday, "10:00", "11:00") };
        var first = ViewingRules.CairoToUtc(Monday, TimeOnly.Parse("10:00"));
        var slots = ViewingRules.GenerateSlots(Monday, rules, [], [(first, first.AddMinutes(30))], Now);
        Assert.Equal("booked", slots[0].UnavailableReason);
        Assert.True(slots[1].Available);

        var past = ViewingRules.GenerateSlots(Monday, rules, [], [], first.AddMinutes(5));
        Assert.Equal("past", past[0].UnavailableReason);
    }

    [Fact]
    public void Slot_times_are_cairo_wall_clock_in_utc()
    {
        // January: Cairo is UTC+2, so 10:00 Cairo == 08:00 UTC.
        Assert.Equal(8, ViewingRules.CairoToUtc(Monday, TimeOnly.Parse("10:00")).Hour);
    }

    [Theory]
    [InlineData(ViewingStatus.Requested, ViewingStatus.Confirmed, true)]
    [InlineData(ViewingStatus.Requested, ViewingStatus.Completed, false)]
    [InlineData(ViewingStatus.Declined, ViewingStatus.Confirmed, false)]
    [InlineData(ViewingStatus.Confirmed, ViewingStatus.RescheduleRequested, true)]
    [InlineData(ViewingStatus.Completed, ViewingStatus.Cancelled, false)]
    public void State_machine(ViewingStatus from, ViewingStatus to, bool ok) =>
        Assert.Equal(ok, ViewingRules.CanTransition(from, to));
}
