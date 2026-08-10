using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;
using RihlaAirways.Api.Services;
namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IEmailService _email;

    public PaymentsController(AppDbContext context,  IEmailService email)
    {
        _context = context;
        _email = email;
    }

    // --- input DTOs ---
    // no Status on create - a payment always starts Pending and moves
    // through the state machine below. letting the client post
    // Status = "Completed" directly would mean anyone can mark a booking
    // paid without money changing hands
    public record PaymentCreateDto(
        [Required] int BookingId,
        [Required][Range(0.01, 999999.99)] decimal Amount,
        [Required] string Method);

    // BookingId is NOT updatable - moving a payment to a different booking
    // breaks the 1:1 and can collide with that booking's existing payment.
    // it was updatable in the original - new fix
    public record PaymentUpdateDto(
        [Required][Range(0.01, 999999.99)] decimal Amount,
        [Required] string Method);

    public record StatusUpdateDto([Required] string Status);

    // --- output DTO ---
    // Include(p => p.Booking) dragged Booking along, and Booking has a User,
    // and User has PasswordHash - on an anonymous endpoint. flattened to
    // just the booking fields that matter here.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // so EF Core can't map .PaymentDate back to a column once the values
    // are inside the constructor. sort the entities first
    public record PaymentResponseDto(
        int Id, int BookingId, decimal Amount, DateTime PaymentDate,
        string Method, string Status,
        decimal BookingTotal, string BookingStatus, string CustomerName);

    private static readonly Func<IQueryable<Payment>, IQueryable<PaymentResponseDto>> ToDto =
        q => q.Select(p => new PaymentResponseDto(
            p.Id, p.BookingId, p.Amount, p.PaymentDate, p.Method, p.Status,
            p.Booking.TotalAmount, p.Booking.Status, p.Booking.User.Name));

    // the string-not-enum decision means nothing at the db level stops
    // Method = "Camel" or Status = "Whatever" - we check it ourselves
    private static readonly string[] ValidMethods = { "Card", "Cash", "BankTransfer" };
    private static readonly string[] ValidStatuses = { "Pending", "Completed", "Failed", "Refunded" };

    // a payment state MACHINE, not just a string field. without this a
    // Failed payment can flip back to Completed, or a Refunded one can
    // un-refund itself. Completed and Refunded are terminal-ish on purpose
    private static readonly Dictionary<string, string[]> AllowedTransitions = new()
    {
        ["Pending"]   = new[] { "Completed", "Failed" },
        ["Failed"]    = new[] { "Pending" },        // retry
        ["Completed"] = new[] { "Refunded" },
        ["Refunded"]  = Array.Empty<string>()       // end of the line
    };


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] PaymentCreateDto dto)
    {
        if (!ValidMethods.Contains(dto.Method))
            return BadRequest($"Method must be one of: {string.Join(", ", ValidMethods)}");

        var booking = await _context.Bookings.FindAsync(dto.BookingId);
        if (booking == null) return BadRequest($"Booking {dto.BookingId} not found.");

        // edge case first: Payments.BookingId is a UNIQUE index - that index
        // IS the one-to-one. a second payment on the same booking throws
        // DbUpdateException, so we catch it here and say why
        if (await _context.Payments.AnyAsync(p => p.BookingId == dto.BookingId))
            return Conflict($"Booking {dto.BookingId} already has a payment.");

        if (booking.Status == "Cancelled")
            return Conflict("Cannot pay for a cancelled booking.");

        // paying for a booking with no tickets means paying zero, which the
        // Range on Amount already rejects - but the message here explains
        // WHY rather than just saying the number is out of range
        if (booking.TotalAmount <= 0m)
            return Conflict("This booking has no tickets yet, so there is nothing to pay.");

        // the amount has to actually cover the booking. without this you can
        // pay 5 for a 500 booking and the system happily marks it complete
        if (dto.Amount != booking.TotalAmount)
            return BadRequest(
                $"Amount {dto.Amount:F2} does not match the booking total {booking.TotalAmount:F2}.");

        var payment = new Payment
        {
            BookingId = dto.BookingId,
            Amount = dto.Amount,
            Method = dto.Method,
            Status = "Pending",          // always starts here, never from the dto
            PaymentDate = DateTime.UtcNow
        };

        _context.Payments.Add(payment);
        await _context.SaveChangesAsync();

        var result = await ToDto(_context.Payments.Where(p => p.Id == payment.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetById), new { id = payment.Id }, result);
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Update(int id, [FromBody] PaymentUpdateDto dto)
    {
        var payment = await _context.Payments.FindAsync(id);
        if (payment == null) return NotFound();

        if (!ValidMethods.Contains(dto.Method))
            return BadRequest($"Method must be one of: {string.Join(", ", ValidMethods)}");

        // a settled payment is a financial record - editing the amount after
        // the money moved is exactly the kind of thing an audit would flag
        if (payment.Status is "Completed" or "Refunded")
            return Conflict($"Cannot edit a {payment.Status} payment.");

        payment.Amount = dto.Amount;
        payment.Method = dto.Method;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Payments.Where(p => p.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update (status change) ============
    [HttpPatch("{id}/status")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] StatusUpdateDto dto)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (payment == null) return NotFound();

        if (!ValidStatuses.Contains(dto.Status))
            return BadRequest($"Status must be one of: {string.Join(", ", ValidStatuses)}");

        // the state machine. the original accepted any string, so a Failed
        // payment could jump straight back to Completed with no retry
        var allowed = AllowedTransitions[payment.Status];
        if (!allowed.Contains(dto.Status))
            return Conflict(allowed.Length == 0
                ? $"A {payment.Status} payment cannot change status."
                : $"Cannot go from {payment.Status} to {dto.Status}. Allowed: {string.Join(", ", allowed)}");

        payment.Status = dto.Status;

        // this is THE line the spec's email requirement hangs off.
        // Pending -> Completed is where the e-ticket confirmation fires,
        // so the booking gets confirmed at the same moment
        if (dto.Status == "Completed")
        {
            payment.Booking.Status = "Confirmed";
            payment.PaymentDate = DateTime.UtcNow;
        }

        if (dto.Status == "Refunded")
            payment.Booking.Status = "Cancelled";

        await _context.SaveChangesAsync();

        // email AFTER SaveChanges, deliberately. two reasons:
        // 1) we don't want to send "booking confirmed" and then have the save
        //    fail - the customer would have an e-ticket for nothing
        // 2) the service re-queries the booking, so the data has to be
        //    committed before it runs or it reads the old status
        //
        // note there's no try/catch here - EmailService swallows its own
        // exceptions internally. that's on purpose: a mail server being down
        // must never turn a successful payment into a 500. see the long
        // comment in EmailService.SendAsync
        if (dto.Status == "Completed")
            await _email.SendBookingConfirmationAsync(payment.BookingId);

        return Ok(await ToDto(_context.Payments.Where(p => p.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var payment = await _context.Payments.FindAsync(id);
        if (payment == null) return NotFound();

        // deleting a completed payment erases the record that money was
        // received while the booking stays Confirmed - the booking would
        // then look paid with nothing backing it. refund it instead.
        // note Refunded is blocked too - a refund is still a financial
        // event that has to stay on the record
        if (payment.Status is "Completed" or "Refunded")
            return Conflict($"Cannot delete a {payment.Status.ToLower()} payment — " +
                            "the financial record must survive.");

        _context.Payments.Remove(payment);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entity) ============
    [HttpGet]
    [Authorize(Roles = "Admin,Staff")]   // was AllowAnonymous, on financial
                                          // records. worst one in the file
    public async Task<IActionResult> GetAll()
    {
        // OrderByDescending moved INSIDE the ToDto call, onto the entities.
        // chained onto the result it compiles and then throws at runtime -
        // EF Core can't see through the record constructor. new fix
        return Ok(await ToDto(
            _context.Payments.OrderByDescending(p => p.PaymentDate))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        var payment = await ToDto(_context.Payments.Where(p => p.Id == id))
            .FirstOrDefaultAsync();
        return payment == null ? NotFound() : Ok(payment);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Filter(
        [FromQuery] string? status,
        [FromQuery] string? method,
        [FromQuery] decimal? minAmount,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        var query = _context.Payments.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(p => p.Status == status);

        if (!string.IsNullOrWhiteSpace(method))
            query = query.Where(p => p.Method == method);   // == not Contains,
                                                            // the values are a fixed set

        if (minAmount.HasValue)
            query = query.Where(p => p.Amount >= minAmount.Value);

        // a real date range - the spec names date range as the example for
        // this case, and it's the filter finance actually needs
        if (fromDate.HasValue)
            query = query.Where(p => p.PaymentDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(p => p.PaymentDate <= toDate.Value);

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderByDescending(p => p.PaymentDate)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are fine after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byMethod = await _context.Payments
            .GroupBy(p => p.Method)
            .Select(g => new
            {
                Method = g.Key,
                Count = g.Count(),
                TotalAmount = g.Sum(p => p.Amount),
                AverageAmount = Math.Round(g.Average(p => p.Amount), 2),
                // conditional counts inside the group - these are the numbers
                // anyone actually wants, and they still translate to SQL
                CompletedCount = g.Count(p => p.Status == "Completed"),
                FailedCount = g.Count(p => p.Status == "Failed")
            })
            .OrderByDescending(s => s.TotalAmount)
            .ToListAsync();

        var byStatus = await _context.Payments
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Total = g.Sum(p => p.Amount) })
            .OrderByDescending(s => s.Total)
            .ToListAsync();

        // Sum on an empty table is 0 so it's safe unguarded - unlike
        // Average or Max, which throw. same trap as the other stats endpoints
        var revenue = await _context.Payments
            .Where(p => p.Status == "Completed")
            .SumAsync(p => p.Amount);

        return Ok(new { CollectedRevenue = revenue, ByMethod = byMethod, ByStatus = byStatus });
    }
}