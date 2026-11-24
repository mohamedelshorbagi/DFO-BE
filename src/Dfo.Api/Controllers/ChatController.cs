using System.Security.Claims;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Authorize]
[Route("api/v1/chats")]
public class ChatController(DfoDbContext db, ChatDomain chat, ChatShaper shape, IHubContext<ChatHub> hub) : ControllerBase
{
    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public record CreateChatDto(Guid PropertyId);
    public record SendDto(string? Text, string? Lang);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var rows = await db.ChatThreads.AsNoTracking()
            .Where(t => t.BuyerId == Uid || t.OwnerId == Uid)
            .OrderByDescending(t => t.UpdatedAt).Take(200)
            .Select(t => new { Thread = t, Last = t.Messages.OrderByDescending(m => m.Timestamp).FirstOrDefault() })
            .ToListAsync();
        var data = await shape.Threads(rows.Select(r => (r.Thread, r.Last)), Uid);
        return Ok(new { data, meta = new { total = data.Count, nextCursor = (string?)null } });
    }

    /// <summary>Starts (or returns) the caller's conversation about a listing. Only buyers start conversations; the owner is derived server-side.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateChatDto d)
    {
        var p = await db.Properties.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d.PropertyId && x.Status == ListingStatus.Published)
                ?? throw ApiException.NotFound("Property not found.");
        var (t, created) = await chat.GetOrCreate(p, Uid);
        if (created) await SaveThreadOnce(t);
        var data = (await shape.Threads([(t, null)], Uid))[0];
        return created ? Created($"/api/v1/chats/{t.Id}", new { data }) : Ok(new { data });
    }

    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> Messages(Guid id, [FromQuery] int? limit, [FromQuery] DateTime? before)
    {
        var t = await Participant(id, track: false);
        var take = Math.Clamp(limit ?? 100, 1, 200);
        var q = db.ChatMessages.AsNoTracking().Where(m => m.ThreadId == id);
        if (before is { } b) q = q.Where(m => m.Timestamp < b.ToUniversalTime());
        // Newest page first from the database, returned oldest-first so the UI can append directly.
        var page = (await q.OrderByDescending(m => m.Timestamp).Take(take).ToListAsync()).OrderBy(m => m.Timestamp).ToList();
        var people = await shape.People([t.BuyerId, t.OwnerId]);
        return Ok(new { data = page.Select(m => shape.Message(m, people, t)), meta = new { total = page.Count, nextCursor = (string?)null } });
    }

    [HttpPost("{id:guid}/messages")]
    [EnableRateLimiting("chat")]
    public async Task<IActionResult> Send(Guid id, SendDto d)
    {
        var t = await Participant(id, track: true);
        var msg = chat.AddMessage(t, Uid, d.Text, d.Lang);
        await db.SaveChangesAsync();
        await Push(t, msg);
        var people = await shape.People([t.BuyerId, t.OwnerId]);
        return Created($"/api/v1/chats/{id}/messages", new { data = shape.Message(msg, people, t), meta = new { total = 1, nextCursor = (string?)null } });
    }

    [HttpPatch("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id)
    {
        var t = await Participant(id, track: true);
        await db.ChatMessages.Where(m => m.ThreadId == id && m.SenderId != Uid && m.Status != MessageStatus.Read)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Read));
        if (Uid == t.BuyerId) t.BuyerUnread = 0; else t.OwnerUnread = 0;
        await db.SaveChangesAsync();
        // Lets the sender's screen flip "sent" to "read" without polling.
        await hub.Clients.Group(ChatHub.Group(ChatDomain.OtherParticipant(t, Uid)!)).SendAsync("read", new { threadId = id, readerId = Uid });
        return Ok(new { success = true });
    }

    // ---------- helpers ----------
    /// <summary>404 (not 403) for threads the caller isn't in, so thread ids can't be probed.</summary>
    private async Task<ChatThread> Participant(Guid id, bool track)
    {
        var q = track ? db.ChatThreads.AsQueryable() : db.ChatThreads.AsNoTracking();
        return await q.FirstOrDefaultAsync(t => t.Id == id && (t.BuyerId == Uid || t.OwnerId == Uid))
               ?? throw ApiException.NotFound("Conversation not found.");
    }

    private async Task SaveThreadOnce(ChatThread t)
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Two requests created the same thread at once; the unique index picked a winner. Use that one.
            db.ChangeTracker.Clear();
            var existing = await db.ChatThreads.AsNoTracking().FirstAsync(x => x.PropertyId == t.PropertyId && x.BuyerId == t.BuyerId);
            t.Id = existing.Id;
        }
    }

    private async Task Push(ChatThread t, ChatMessage msg)
    {
        var people = await shape.People([t.BuyerId, t.OwnerId]);
        var payload = shape.Message(msg, people, t);
        var other = ChatDomain.OtherParticipant(t, Uid)!;
        // Each side gets its own unread number; the sender's other tabs/devices get the message too (the client de-duplicates by id).
        await hub.Clients.Group(ChatHub.Group(other)).SendAsync("message", new { message = payload, unreadCount = Uid == t.BuyerId ? t.OwnerUnread : t.BuyerUnread });
        await hub.Clients.Group(ChatHub.Group(Uid)).SendAsync("message", new { message = payload, unreadCount = Uid == t.BuyerId ? t.BuyerUnread : t.OwnerUnread });
    }
}
