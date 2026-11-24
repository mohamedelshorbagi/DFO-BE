using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dfo.Tests;

public class ChatDomainTests
{
    // A context that is never connected: the rules under test only touch the change tracker.
    private static ChatDomain Make(out DfoDbContext db)
    {
        db = new DfoDbContext(new DbContextOptionsBuilder<DfoDbContext>().UseSqlServer("Server=unused;Database=unused").Options);
        return new ChatDomain(db);
    }

    private static ChatThread Thread() => new() { PropertyId = Guid.NewGuid(), BuyerId = "buyer-1", OwnerId = "owner-1" };

    [Fact]
    public void Buyer_message_is_stored_trimmed_and_bumps_the_owners_unread_count()
    {
        var chat = Make(out _);
        var t = Thread();
        var m = chat.AddMessage(t, "buyer-1", "  Is it still available?  ", "en");

        Assert.Equal("Is it still available?", m.Text);
        Assert.Equal("buyer", m.SenderRole);
        Assert.Equal(1, t.OwnerUnread);
        Assert.Equal(0, t.BuyerUnread);
        Assert.False(m.HasViolation);
        Assert.Null(m.WarningNotice);
    }

    [Fact]
    public void Owner_reply_bumps_the_buyers_unread_count()
    {
        var chat = Make(out _);
        var t = Thread();
        chat.AddMessage(t, "owner-1", "Yes, come see it on Friday.", "en");
        Assert.Equal(1, t.BuyerUnread);
        Assert.Equal(0, t.OwnerUnread);
    }

    [Fact]
    public void Contact_details_are_masked_on_the_server_and_flagged()
    {
        var chat = Make(out _);
        var m = chat.AddMessage(Thread(), "buyer-1", "call me on 01012345678 or mail a@b.com", "en");

        Assert.True(m.HasViolation);
        Assert.NotNull(m.WarningNotice);
        Assert.DoesNotContain("01012345678", m.Text);
        Assert.DoesNotContain("a@b.com", m.Text);
    }

    [Fact]
    public void Arabic_senders_get_the_arabic_warning()
    {
        var chat = Make(out _);
        var m = chat.AddMessage(Thread(), "buyer-1", "كلمني على 01012345678", "ar");
        Assert.True(m.HasViolation);
        Assert.Contains("DFO", m.WarningNotice);
        Assert.Contains("لحمايتك", m.WarningNotice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Empty_messages_are_rejected(string? text)
    {
        var chat = Make(out _);
        var ex = Assert.Throws<ApiException>(() => chat.AddMessage(Thread(), "buyer-1", text, "en"));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Messages_over_the_limit_are_rejected_but_the_limit_itself_is_allowed()
    {
        var chat = Make(out _);
        chat.AddMessage(Thread(), "buyer-1", new string('a', ChatDomain.MaxTextLength), "en");
        var ex = Assert.Throws<ApiException>(() => chat.AddMessage(Thread(), "buyer-1", new string('a', ChatDomain.MaxTextLength + 1), "en"));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Strangers_cannot_post_into_a_thread()
    {
        var chat = Make(out _);
        var t = Thread();
        var ex = Assert.Throws<ApiException>(() => chat.AddMessage(t, "someone-else", "hello", "en"));
        Assert.Equal(403, ex.Status);
        Assert.Equal(0, t.OwnerUnread + t.BuyerUnread);   // and nothing is counted
    }

    [Fact]
    public async Task Owners_cannot_start_a_conversation_about_their_own_listing()
    {
        var chat = Make(out _);
        var p = new Property { OwnerId = "owner-1" };
        var ex = await Assert.ThrowsAsync<ApiException>(() => chat.GetOrCreate(p, "owner-1"));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Other_participant_is_the_opposite_side_and_null_for_strangers()
    {
        var t = Thread();
        Assert.Equal("owner-1", ChatDomain.OtherParticipant(t, "buyer-1"));
        Assert.Equal("buyer-1", ChatDomain.OtherParticipant(t, "owner-1"));
        Assert.Null(ChatDomain.OtherParticipant(t, "x"));
    }

    [Theory]
    [InlineData("Karim Ezzat El-Sayed", true, "Karim")]      // buyers appear by first name only
    [InlineData("Dr. Tarek Mostafa", false, "Dr. Tarek Mostafa")]   // owners already have a public name on their listing
    [InlineData("", true, "User")]
    [InlineData(null, false, "User")]
    public void Public_names_protect_buyer_identity(string? full, bool isBuyer, string expected) =>
        Assert.Equal(expected, ChatShaper.PublicName(full, isBuyer));
}
