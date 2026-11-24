using Dfo.Application;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>Chat rules shared by the chat and lead endpoints, so both behave identically.</summary>
public class ChatDomain(DfoDbContext db)
{
    public const int MaxTextLength = 2000;
    public const string SystemId = "system";
    public const string OpeningNotice = "Direct negotiation initiated. Zero brokerage commission applicable.";

    /// <summary>Finds the buyer's thread for this listing or creates it (with the opening system notice). Does not save.</summary>
    public async Task<(ChatThread Thread, bool Created)> GetOrCreate(Property p, string buyerId)
    {
        if (p.OwnerId == buyerId) throw ApiException.BadRequest("You cannot start a conversation about your own listing.");
        var existing = await db.ChatThreads.FirstOrDefaultAsync(t => t.PropertyId == p.Id && t.BuyerId == buyerId);
        if (existing is not null) return (existing, false);

        var t = new ChatThread { PropertyId = p.Id, BuyerId = buyerId, OwnerId = p.OwnerId };
        db.ChatThreads.Add(t);
        db.ChatMessages.Add(new ChatMessage
        {
            ThreadId = t.Id, SenderId = SystemId, SenderRole = "system", Text = OpeningNotice,
            Status = MessageStatus.Read, Timestamp = t.UpdatedAt
        });
        return (t, true);
    }

    /// <summary>
    /// Validates, filters and appends a message; updates unread counters and the thread's activity time.
    /// Contact details are masked here, server-side, so a modified client cannot bypass the filter. Does not save.
    /// </summary>
    public ChatMessage AddMessage(ChatThread t, string senderId, string? rawText, string? lang)
    {
        var text = (rawText ?? "").Trim();
        if (text.Length == 0) throw ApiException.BadRequest("Message text is required.");
        if (text.Length > MaxTextLength) throw ApiException.BadRequest($"Messages are limited to {MaxTextLength} characters.");

        var role = senderId == t.BuyerId ? "buyer" : senderId == t.OwnerId ? "owner" : throw ApiException.Forbidden("Not a participant.");
        var f = ContactFilter.Filter(text, lang == "ar" ? "ar" : "en");
        var now = DateTime.UtcNow;
        var msg = new ChatMessage
        {
            ThreadId = t.Id, SenderId = senderId, SenderRole = role, Text = f.Filtered,
            HasViolation = f.HasViolation, WarningNotice = f.HasViolation ? f.WarningMessage : null,
            Status = MessageStatus.Sent, Timestamp = now
        };
        db.ChatMessages.Add(msg);
        if (role == "buyer") t.OwnerUnread++; else t.BuyerUnread++;
        t.UpdatedAt = now;
        return msg;
    }

    public static string? OtherParticipant(ChatThread t, string uid) =>
        uid == t.BuyerId ? t.OwnerId : uid == t.OwnerId ? t.BuyerId : null;
}

/// <summary>Builds the JSON the frontend's chat components expect.</summary>
public class ChatShaper(DfoDbContext db, MediaUrlSigner signer)
{
    public record Person(string Name, bool Verified, string? Avatar);

    public async Task<Dictionary<string, Person>> People(IEnumerable<string> ids)
    {
        var list = ids.Where(i => i != ChatDomain.SystemId).Distinct().ToList();
        var rows = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.Verified, u.Avatar }).ToListAsync();
        return rows.ToDictionary(r => r.Id, r => new Person(r.Name, r.Verified, r.Avatar));
    }

    /// <summary>Buyers appear by first name only; owners already have a public name on their listing.</summary>
    public static string PublicName(string? full, bool isBuyer)
    {
        var name = (full ?? "").Trim();
        if (name.Length == 0) return "User";
        return isBuyer ? name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] : name;
    }

    public object Message(ChatMessage m, IReadOnlyDictionary<string, Person> people, ChatThread t)
    {
        var isBuyer = m.SenderId == t.BuyerId;
        var name = m.SenderRole == "system" ? "DFO"
            : PublicName(people.GetValueOrDefault(m.SenderId)?.Name, isBuyer);
        return new
        {
            id = m.Id, threadId = m.ThreadId, senderId = m.SenderId, senderRole = m.SenderRole, senderName = name,
            text = m.Text, hasViolation = m.HasViolation, warningNotice = m.WarningNotice,
            timestamp = DateTime.SpecifyKind(m.Timestamp, DateTimeKind.Utc),
            status = m.Status.ToString().ToLowerInvariant()
        };
    }

    public async Task<List<object>> Threads(IEnumerable<(ChatThread Thread, ChatMessage? Last)> rows, string viewerId)
    {
        var list = rows.ToList();
        var people = await People(list.SelectMany(r => new[] { r.Thread.BuyerId, r.Thread.OwnerId }));
        var propIds = list.Select(r => r.Thread.PropertyId).Distinct().ToList();
        var props = await db.Properties.AsNoTracking().Include(p => p.Images)
            .Where(p => propIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        return list.Select(r =>
        {
            var t = r.Thread;
            props.TryGetValue(t.PropertyId, out var p);
            var cover = p?.Images.OrderBy(i => i.SortOrder).FirstOrDefault(i => i.IsCover) ?? p?.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
            people.TryGetValue(t.BuyerId, out var buyer);
            people.TryGetValue(t.OwnerId, out var owner);
            return (object)new
            {
                id = t.Id,
                property = new
                {
                    id = t.PropertyId, listingCode = p?.ListingCode ?? "", title = p?.Title ?? "Listing no longer available",
                    titleAr = p?.TitleAr, price = p?.Price ?? 0, location = p?.Location ?? "",
                    image = cover is null ? "/assets/images/hero_property_egypt.jpg" : signer.Url(cover.Id, high: true)
                },
                buyer = new { id = t.BuyerId, name = PublicName(buyer?.Name, true), role = "buyer", verified = buyer?.Verified ?? false, avatar = buyer?.Avatar },
                owner = new { id = t.OwnerId, name = PublicName(owner?.Name, false), role = "owner", verified = owner?.Verified ?? false, avatar = owner?.Avatar },
                lastMessage = r.Last is null ? null : Message(r.Last, people, t),
                unreadCount = viewerId == t.BuyerId ? t.BuyerUnread : t.OwnerUnread,
                updatedAt = DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc),
                isTyping = false   // typing is live-only (pushed over the hub), never stored
            };
        }).ToList();
    }
}

/// <summary>Real-time channel. Each user joins a private group; the server pushes to groups, clients only send "typing".</summary>
[Microsoft.AspNetCore.Authorization.Authorize]
public class ChatHub(DfoDbContext db) : Hub
{
    public static string Group(string userId) => $"user:{userId}";

    private string? Uid => Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    public override async Task OnConnectedAsync()
    {
        if (Uid is { } id) await Groups.AddToGroupAsync(Context.ConnectionId, Group(id));
        await base.OnConnectedAsync();
    }

    /// <summary>Tells the other participant that this user is typing. Silently ignored for anyone who isn't in the thread.</summary>
    public async Task Typing(Guid threadId)
    {
        if (Uid is not { } uid) return;
        var t = await db.ChatThreads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == threadId && (x.BuyerId == uid || x.OwnerId == uid));
        if (t is null) return;
        var other = ChatDomain.OtherParticipant(t, uid)!;
        var name = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.Name).FirstOrDefaultAsync();
        await Clients.Group(Group(other)).SendAsync("typing", new { threadId, userName = ChatShaper.PublicName(name, uid == t.BuyerId) });
    }
}
