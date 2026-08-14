using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BookingsController : ControllerBase
{
    private readonly AppDbContext _context;

    public BookingsController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // no Status and no TotalAmount. both were in the original and both are
    // server-owned - see the long note in Create
    public record BookingCreateDto([Required] int UserId);

    // there is genuinely nothing on a Booking a client should edit directly.
    // Status goes through the PATCH state machine, TotalAmount follows the
    // tickets. so the PUT recalculates instead of assigning
    public record BookingUpdateDto();

    public record StatusUpdateDto([Required] string Status);

    // --- output DTO ---
    // Include(b => b.User) dragged PasswordHash along and Include(b =>
    // b.Tickets) dragged every passenger name - on an anonymous endpoint.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // so EF Core can't map .BookingDate back to a column through the
    // constructor. sort the entities first
    public record BookingResponseDto(
        int Id, int UserId, string CustomerName, string CustomerEmail,
        DateTime BookingDate, string Status, decimal TotalAmount,
        int TicketCount, bool IsPaid, string? PaymentStatus, decimal? PaidAmount);

    private static readonly Func<IQueryable<Booking>, IQueryable<BookingResponseDto>> ToDto =
        q => q.Select(b => new BookingResponseDto(
            b.Id, b.UserId, b.User.Name, b.User.Email,
            b.BookingDate, b.Status, b.TotalAmount,
            b.Tickets.Count,
            // Payment is the optional side of the 1:1 - the null checks are
            // load-bearing, an unpaid booking genuinely has no payment row
            b.Payment != null,
            b.Payment != null ? b.Payment.Status : null,
            b.Payment != null ? (decimal?)b.Payment.Amount : null));

    private static readonly string[] ValidStatuses = { "Pending", "Confirmed", "Cancelled" };

    // same pattern as PaymentsController and FlightsController.
    // note Confirmed -> Pending is NOT allowed: a confirmed booking has a
    // completed payment behind it, so un-confirming would leave money with
    // nothing attached to it. that's what Cancelled + refund is for
    private static readonly Dictionary<string, string[]> AllowedTransitions = new()
    {
        ["Pending"]   = new[] { "Confirmed", "Cancelled" },
        ["Confirmed"] = new[] { "Cancelled" },
        ["Cancelled"] = Array.Empty<string>()
    };

    private bool CanAccess(int ownerUserId)
    {
        if (User.IsInRole("Admin") || User.IsInRole("Staff")) return true;
        var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return callerId != null && int.Parse(callerId) == ownerUserId;
    }


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] BookingCreateDto dto)
    {
        if (!CanAccess(dto.UserId)) return Forbid();

        var user = await _context.Users.FindAsync(dto.UserId);
        if (user == null) return BadRequest($"User {dto.UserId} not found.");

        var booking = new Booking
        {
            UserId = dto.UserId,
            BookingDate = DateTime.UtcNow,
            Status = "Pending",
            // starts at ZERO, always. this is the important line in the file.
            // the original took TotalAmount straight off the dto - but
            // TicketsController now does booking.TotalAmount += price on
            // every ticket added. two owners for one number means you can
            // create a booking with TotalAmount = 500, add 300 worth of
            // tickets, and end up at 800 - then PaymentsController rejects
            // payment because it won't match a figure that was never real.
            // one writer only: the tickets. new fix
            TotalAmount = 0m
        };

        _context.Bookings.Add(booking);
        await _context.SaveChangesAsync();

        var result = await ToDto(_context.Bookings.Where(b => b.Id == booking.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetById), new { id = booking.Id }, result);
    }


    // ============ 2. PUT — full update (recalculate) ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Update(int id, [FromBody] BookingUpdateDto dto)
    {
        var booking = await _context.Bookings.FindAsync(id);
        if (booking == null) return NotFound();

        if (booking.Status == "Cancelled")
            return Conflict("Cannot modify a cancelled booking.");

        // this PUT is a RECONCILIATION, not an edit - there's nothing on a
        // booking a client should set directly. it re-derives TotalAmount
        // from the tickets, which is the repair tool for a booking that
        // drifted (a crash mid-ticket-create, a manual db edit).
        // an unusual shape for a PUT, but assigning TotalAmount from the
        // request body is what caused the whole problem above
        var ticketTotal = await _context.Tickets
            .Where(t => t.BookingId == id)
            .SumAsync(t => (decimal?)t.Price) ?? 0m;
        // the (decimal?) cast + ?? 0 is deliberate - SumAsync over an empty
        // set returns null for a nullable projection rather than throwing

        // don't silently rewrite the total under a payment that already
        // matched the old figure
        var payment = await _context.Payments.FirstOrDefaultAsync(p => p.BookingId == id);
        if (payment is { Status: "Completed" } && payment.Amount != ticketTotal)
            return Conflict(
                $"Ticket total ({ticketTotal:F2}) no longer matches the completed payment " +
                $"({payment.Amount:F2}). Refund and rebook instead.");

        booking.TotalAmount = ticketTotal;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Bookings.Where(b => b.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update (status change) ============
    [HttpPatch("{id}/status")]
    [Authorize]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] StatusUpdateDto dto)
    {
        var booking = await _context.Bookings
            .Include(b => b.Payment)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (booking == null) return NotFound();

        if (!CanAccess(booking.UserId)) return Forbid();

        if (!ValidStatuses.Contains(dto.Status))
            return BadRequest($"Status must be one of: {string.Join(", ", ValidStatuses)}");

        var allowed = AllowedTransitions[booking.Status];
        if (!allowed.Contains(dto.Status))
            return Conflict(allowed.Length == 0
                ? $"A {booking.Status} booking cannot change status."
                : $"Cannot go from {booking.Status} to {dto.Status}. Allowed: {string.Join(", ", allowed)}");

        // Confirmed is normally set BY PaymentsController when a payment
        // completes - that's the flow. a manual confirm without money behind
        // it is a staff override, so only staff can do it
        if (dto.Status == "Confirmed")
        {
            var paid = booking.Payment is { Status: "Completed" };
            if (!paid && !User.IsInRole("Admin") && !User.IsInRole("Staff"))
                return Conflict("Booking cannot be confirmed until payment completes.");
        }

        // cancelling with money already taken would leave the customer paid
        // and with nothing. the refund has to come first
        if (dto.Status == "Cancelled" && booking.Payment is { Status: "Completed" })
            return Conflict("Refund the payment before cancelling this booking.");

        booking.Status = dto.Status;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Bookings.Where(b => b.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var booking = await _context.Bookings
            .Include(b => b.Payment)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (booking == null) return NotFound();

        // edge case first, and this is the most destructive endpoint in the
        // project. THREE cascades fire from one DELETE:
        //   Ticket.BookingId  -> Cascade
        //   Payment.BookingId -> Cascade
        //   Baggage.TicketId  -> Cascade (via the tickets)
        // so removing one booking silently erases its tickets, its bags AND
        // its payment record. a completed payment vanishing means money was
        // received with nothing left to show it. these cascades are correct
        // for the normal lifecycle - they're just far too broad for a
        // manual delete, so we gate it here
        if (booking.Payment is { Status: "Completed" or "Refunded" })
            return Conflict("Cannot delete a booking with a settled payment — " +
                            "the financial record must survive. Cancel it instead.");

        var ticketCount = await _context.Tickets.CountAsync(t => t.BookingId == id);
        if (ticketCount > 0 && booking.Status != "Cancelled")
            return Conflict($"This booking has {ticketCount} ticket(s). Cancel it first, " +
                            "then delete if you really need to.");

        _context.Bookings.Remove(booking);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> GetAll()
    {
        // was AllowAnonymous with User, Tickets and Payment included -
        // every customer email, password hash, passenger name and seat,
        // no login required.
        // now: staff see everything, a passenger sees only their own.
        // this scoping pattern is what the other user-owned controllers
        // should copy
        var query = _context.Bookings.AsQueryable();

        if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
        {
            var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (callerId == null) return Forbid();
            var uid = int.Parse(callerId);
            query = query.Where(b => b.UserId == uid);
        }

        // OrderByDescending moved INSIDE the ToDto call, onto the entities -
        // chained onto the result it compiles and throws at runtime.
        // new fix
        return Ok(await ToDto(query.OrderByDescending(b => b.BookingDate)).ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        var booking = await _context.Bookings.FindAsync(id);
        // 404 for both "doesn't exist" and "not yours". this used to return
        // NotFound for one and Forbid for the other, which meant walking the id
        // range told you exactly which booking ids were real
        if (booking == null || !CanAccess(booking.UserId)) return NotFound();

        return Ok(await ToDto(_context.Bookings.Where(b => b.Id == id)).FirstAsync());
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Filter(
        [FromQuery] string? status,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] decimal? minTotal,
        [FromQuery] bool? unpaidOnly,
        [FromQuery] string? customerEmail)
    {
        var query = _context.Bookings.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(b => b.Status == status);

        if (fromDate.HasValue)
            query = query.Where(b => b.BookingDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(b => b.BookingDate <= toDate.Value);

        if (minTotal.HasValue)
            query = query.Where(b => b.TotalAmount >= minTotal.Value);

        // the report finance actually wants: confirmed bookings with no
        // money against them. filtering on the ABSENCE of a related row,
        // which EF Core turns into a NOT EXISTS
        if (unpaidOnly == true)
            query = query.Where(b => b.Payment == null || b.Payment.Status != "Completed");

        if (!string.IsNullOrWhiteSpace(customerEmail))
            query = query.Where(b => b.User.Email.Contains(customerEmail));

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderByDescending(b => b.BookingDate)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byStatus = await _context.Bookings
            .GroupBy(b => b.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                TotalValue = g.Sum(b => b.TotalAmount),
                TotalTickets = g.Sum(b => b.Tickets.Count),
                AverageValue = Math.Round(g.Average(b => b.TotalAmount), 2)
            })
            .OrderByDescending(s => s.TotalValue)
            .ToListAsync();

        // top customers by spend - crosses into User and back out to Payment
        var topCustomers = await _context.Bookings
            .GroupBy(b => new { b.UserId, b.User.Name, b.User.Email })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.Name,
                g.Key.Email,
                BookingCount = g.Count(),
                LifetimeValue = g.Sum(b => b.TotalAmount),
                // only money actually collected, not just booked
                PaidValue = g.Sum(b => b.Payment != null && b.Payment.Status == "Completed"
                    ? b.TotalAmount : 0m)
            })
            .OrderByDescending(s => s.PaidValue)
            .Take(10)
            .ToListAsync();

        // the drift detector. if any row comes back here, TotalAmount and
        // the tickets have gone out of sync - which is exactly the bug the
        // original Create introduced. worth keeping even after the fix,
        // it's how you PROVE the invariant holds during the demo
        var mismatched = await _context.Bookings
            .Where(b => b.TotalAmount != b.Tickets.Sum(t => t.Price))
            .Select(b => new
            {
                b.Id,
                Stored = b.TotalAmount,
                Calculated = b.Tickets.Sum(t => t.Price),
                b.Status
            })
            .ToListAsync();

        return Ok(new
        {
            ByStatus = byStatus,
            TopCustomers = topCustomers,
            IntegrityCheck = new
            {
                DriftedBookings = mismatched.Count,
                Details = mismatched
            }
        });
    }
}