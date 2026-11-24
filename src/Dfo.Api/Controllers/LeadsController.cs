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
[Route("api/v1")]
public class LeadsController(DfoDbContext db, ChatDomain chat, ChatShaper shape, IHubContext<ChatHub> hub) : ControllerBase
{
    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public record CreateLeadDto(string? Message, decimal? BudgetEgp, string? Lang);
    public record LeadStatusDto(string Status);

    private static readonly Dictionary<string, LeadStatus> StatusIn = new()
    {
        ["new"] = LeadStatus.New, ["contacted"] = LeadStatus.Contacted,
        ["viewing_scheduled"] = LeadStatus.ViewingScheduled, ["closed"] = LeadStatus.Closed
    };

    private static string StatusOut(LeadStatus s) => s switch
    {
        LeadStatus.ViewingScheduled => "viewing_scheduled",
        _ => s.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// A buyer's inquiry. It becomes the first message of a chat with the owner (so the owner replies in-app)
    /// and is also kept as a lead for the owner's dashboard.
    /// </summary>
    [HttpPost("properties/{propertyId:guid}/leads")]
    [EnableRateLimiting("chat")]
    public async Task<IActionResult> Create(Guid propertyId, CreateLeadDto d)
    {
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == propertyId && x.Status == ListingStatus.Published)
                ?? throw ApiException.NotFound("Property not found.");
        if (p.OwnerId == Uid) throw ApiException.BadRequest("You cannot send an inquiry about your own listing.");
        if (d.BudgetEgp is < 0) throw ApiException.BadRequest("Budget cannot be negative.");

        // One inquiry per buyer per listing per day; after that the conversation continues in chat.
        var since = DateTime.UtcNow.AddHours(-24);
        if (await db.Leads.AnyAsync(l => l.PropertyId == propertyId && l.BuyerId == Uid && l.CreatedAt > since))
            throw ApiException.Conflict("You already sent this owner an inquiry today. Continue the conversation in chat.");

        var (thread, created) = await chat.GetOrCreate(p, Uid);
        var msg = chat.AddMessage(thread, Uid, d.Message, d.Lang);   // validates + masks contact details

        var lead = new Lead
        {
            PropertyId = p.Id, BuyerId = Uid, ThreadId = thread.Id, BudgetEgp = d.BudgetEgp,
            Message = msg.Text   // the masked text, never the raw one
        };
        db.Leads.Add(lead);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) when (created)
        {
            throw ApiException.Conflict("Please try again.");   // lost a race creating the thread; nothing was saved
        }
        await db.Properties.Where(x => x.Id == p.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadsCount, x => x.LeadsCount + 1));

        var people = await shape.People([thread.BuyerId, thread.OwnerId]);
        var payload = shape.Message(msg, people, thread);
        await hub.Clients.Group(ChatHub.Group(thread.OwnerId)).SendAsync("message", new { message = payload, unreadCount = thread.OwnerUnread });
        await hub.Clients.Group(ChatHub.Group(Uid)).SendAsync("message", new { message = payload, unreadCount = thread.BuyerUnread });

        return Created($"/api/v1/leads/{lead.Id}", new { data = new { id = lead.Id, threadId = thread.Id, status = StatusOut(lead.Status) } });
    }

    /// <summary>The signed-in owner's leads. Buyer phone and email are deliberately never included: contact happens in chat.</summary>
    [HttpGet("me/leads")]
    public async Task<IActionResult> Mine()
    {
        var rows = await (from l in db.Leads.AsNoTracking()
                          join p in db.Properties.AsNoTracking() on l.PropertyId equals p.Id
                          where p.OwnerId == Uid
                          orderby l.CreatedAt descending
                          select new { l, p.Title }).Take(200).ToListAsync();
        var people = await shape.People(rows.Select(r => r.l.BuyerId));
        return Ok(new
        {
            data = rows.Select(r => new
            {
                id = r.l.Id, propertyId = r.l.PropertyId, propertyTitle = r.Title, threadId = r.l.ThreadId,
                buyerName = ChatShaper.PublicName(people.GetValueOrDefault(r.l.BuyerId)?.Name, true),
                buyerPhone = "", buyerEmail = "",
                budgetEgp = r.l.BudgetEgp ?? 0, message = r.l.Message,
                date = r.l.CreatedAt.ToString("yyyy-MM-dd HH:mm"), status = StatusOut(r.l.Status)
            }),
            meta = new { total = rows.Count }
        });
    }

    [HttpPatch("leads/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, LeadStatusDto d)
    {
        if (!StatusIn.TryGetValue(d.Status ?? "", out var status))
            throw ApiException.BadRequest("status must be new, contacted, viewing_scheduled or closed.");
        // Only the listing's owner may change a lead; anyone else gets the same 404 as a missing lead.
        var lead = await (from l in db.Leads
                          join p in db.Properties on l.PropertyId equals p.Id
                          where l.Id == id && p.OwnerId == Uid
                          select l).FirstOrDefaultAsync()
                   ?? throw ApiException.NotFound("Lead not found.");
        lead.Status = status;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
