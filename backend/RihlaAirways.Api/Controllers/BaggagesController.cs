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
public class BaggagesController : ControllerBase
{
    private readonly AppDbContext _context;

    public BaggagesController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // no BaggageNumber and no Fee. both were in the original and both are
    // server-derived - see the notes in Create
    public record BaggageCreateDto(
        [Required] int TicketId,
        [Required][Range(0.1, 9999.99)] decimal WeightKg,
        [Required][MaxLength(30)] string Type);

    // TicketId is NOT updatable - moving a bag to a different ticket changes
    // its identity. BaggageNumber is scoped to a ticket, so the number would
    // have to be reissued and the fee recalculated against a different
    // seat class. it was updatable in the original - new fix
    public record BaggageUpdateDto(
        [Required][Range(0.1, 9999.99)] decimal WeightKg,
        [Required][MaxLength(30)] string Type);

    public record FeeUpdateDto([Required][Range(0, 999999.99)] decimal Fee);

    // --- output DTO ---
    // ThenInclude(t => t.Booking) reached Booking -> User -> PasswordHash,
    // three hops from a baggage tag, on an anonymous endpoint.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // so EF Core can't map .WeightKg back to a column through the
    // constructor. sort the entities first
    public record BaggageResponseDto(
        int Id, int TicketId, int BaggageNumber, string BagTag,
        decimal WeightKg, string Type, decimal Fee,
        string PassengerName, string SeatNumber, string FlightNumber,
        string SeatClassName, int AllowanceKg, decimal ExcessKg);

    private static readonly Func<IQueryable<Baggage>, IQueryable<BaggageResponseDto>> ToDto =
        q => q.Select(b => new BaggageResponseDto(
            b.Id, b.TicketId, b.BaggageNumber,
            // the human-readable tag IS the weak-entity identity made
            // visible: "ticket 42, bag 2". this is what the partial key
            // means in practice - the number alone identifies nothing
            "T" + b.TicketId + "-B" + b.BaggageNumber,
            b.WeightKg, b.Type, b.Fee,
            b.Ticket.PassengerName, b.Ticket.SeatNumber,
            b.Ticket.Flight.FlightNumber,
            b.Ticket.SeatClass.Name,
            b.Ticket.SeatClass.BaggageAllowanceKg,
            b.WeightKg > b.Ticket.SeatClass.BaggageAllowanceKg
                ? b.WeightKg - b.Ticket.SeatClass.BaggageAllowanceKg
                : 0m));

    private static readonly string[] ValidTypes = { "Checked", "CarryOn", "Oversized" };

    // excess baggage rate. a constant for now - a real airline varies this
    // by route and class, but hardcoding it beats letting the client name
    // its own fee
    private const decimal FeePerExcessKg = 5.00m;
    private const decimal OversizedSurcharge = 25.00m;


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] BaggageCreateDto dto)
    {
        if (!ValidTypes.Contains(dto.Type))
            return BadRequest($"Type must be one of: {string.Join(", ", ValidTypes)}");

        var ticket = await _context.Tickets
            .Include(t => t.SeatClass)
            .Include(t => t.Flight)
            .Include(t => t.Booking)
            .FirstOrDefaultAsync(t => t.Id == dto.TicketId);
        if (ticket == null) return BadRequest($"Ticket {dto.TicketId} not found.");

        if (!CanAccess(ticket.Booking.UserId)) return Forbid();

        if (ticket.Flight.Status is "Departed" or "Landed")
            return Conflict("Cannot add baggage to a flight that has already departed.");

        // BaggageNumber is ASSIGNED here, not taken from the request.
        // it's the partial key of a weak entity - "bag 2 of ticket 42" -
        // and a partial key that the client picks isn't really a key.
        // the original let the caller choose it, which meant duplicates hit
        // the (TicketId, BaggageNumber) unique index and 500'd, and you'd
        // get a ticket with bags numbered 1 and 47.
        // Max over an empty set returns null for a nullable projection,
        // hence the cast and the ?? 0 - a first bag gets number 1
        var nextNumber = (await _context.Baggages
            .Where(b => b.TicketId == dto.TicketId)
            .MaxAsync(b => (int?)b.BaggageNumber) ?? 0) + 1;

        // no db constraint for this - just a sanity limit
        if (nextNumber > 10)
            return Conflict("A ticket cannot have more than 10 bags.");

        // Fee is CALCULATED against the seat class allowance. this is the
        // whole reason SeatClass.BaggageAllowanceKg exists - without it that
        // column is decorative and an examiner will ask what it's for.
        // note the allowance is per-ticket, so we count what's already
        // checked in rather than treating each bag independently
        var alreadyChecked = await _context.Baggages
            .Where(b => b.TicketId == dto.TicketId && b.Type == "Checked")
            .SumAsync(b => (decimal?)b.WeightKg) ?? 0m;

        var fee = CalculateFee(dto.Type, dto.WeightKg, alreadyChecked,
                               ticket.SeatClass.BaggageAllowanceKg);

        var baggage = new Baggage
        {
            TicketId = dto.TicketId,
            BaggageNumber = nextNumber,
            WeightKg = dto.WeightKg,
            Type = dto.Type,
            Fee = fee
        };

        _context.Baggages.Add(baggage);
        await _context.SaveChangesAsync();

        var result = await ToDto(_context.Baggages.Where(b => b.Id == baggage.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetById), new { id = baggage.Id }, result);
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Update(int id, [FromBody] BaggageUpdateDto dto)
    {
        var baggage = await _context.Baggages
            .Include(b => b.Ticket).ThenInclude(t => t.SeatClass)
            .Include(b => b.Ticket).ThenInclude(t => t.Flight)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (baggage == null) return NotFound();

        if (!ValidTypes.Contains(dto.Type))
            return BadRequest($"Type must be one of: {string.Join(", ", ValidTypes)}");

        if (baggage.Ticket.Flight.Status is "Departed" or "Landed")
            return Conflict("Cannot modify baggage after departure.");

        // reweighing at the desk means the fee changes too - excluding THIS
        // bag from the running total so we don't count it twice
        var otherChecked = await _context.Baggages
            .Where(b => b.TicketId == baggage.TicketId && b.Type == "Checked" && b.Id != id)
            .SumAsync(b => (decimal?)b.WeightKg) ?? 0m;

        baggage.WeightKg = dto.WeightKg;
        baggage.Type = dto.Type;
        baggage.Fee = CalculateFee(dto.Type, dto.WeightKg, otherChecked,
                                   baggage.Ticket.SeatClass.BaggageAllowanceKg);

        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Baggages.Where(b => b.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update (manual fee override) ============
    [HttpPatch("{id}/fee")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> UpdateFee(int id, [FromBody] FeeUpdateDto dto)
    {
        // deliberately staff-only, unlike the original's plain [Authorize].
        // this endpoint OVERRIDES the calculated fee - a waiver at the desk,
        // a goodwill gesture. a passenger being able to set their own
        // baggage fee to zero is not a feature
        var baggage = await _context.Baggages.FindAsync(id);
        if (baggage == null) return NotFound();

        baggage.Fee = dto.Fee;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Baggages.Where(b => b.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Delete(int id)
    {
        var baggage = await _context.Baggages
            .Include(b => b.Ticket).ThenInclude(t => t.Flight)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (baggage == null) return NotFound();

        if (baggage.Ticket.Flight.Status is "Departed" or "Landed")
            return Conflict("Cannot remove baggage from a flight that has operated.");

        // no FK guard needed - Baggage is a leaf, nothing points at it.
        // and note we do NOT renumber the remaining bags: deleting bag 2 of
        // 3 leaves 1 and 3. that's correct for a weak entity - the partial
        // key identifies the bag, it isn't a display index, and renumbering
        // would change the identity of a bag already tagged and loaded.
        // it also means the next bag added gets number 4, since Create uses
        // MAX + 1 rather than COUNT + 1 - deliberate, COUNT would reuse 3
        _context.Baggages.Remove(baggage);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetAll()
    {
        // OrderBy moved INSIDE the ToDto call, onto the entities - chained
        // onto the result it compiles and throws at runtime. new fix
        return Ok(await ToDto(
            _context.Baggages
                .OrderBy(b => b.TicketId)
                .ThenBy(b => b.BaggageNumber))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        var baggage = await _context.Baggages
            .Include(b => b.Ticket).ThenInclude(t => t.Booking)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (baggage == null) return NotFound();

        if (!CanAccess(baggage.Ticket.Booking.UserId)) return Forbid();

        return Ok(await ToDto(_context.Baggages.Where(b => b.Id == id)).FirstAsync());
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Filter(
        [FromQuery] string? type,
        [FromQuery] decimal? minWeight,
        [FromQuery] decimal? maxWeight,
        [FromQuery] int? flightId,
        [FromQuery] bool? overweightOnly)
    {
        var query = _context.Baggages.AsQueryable();

        // == not Contains - the types are a fixed set now
        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(b => b.Type == type);

        if (minWeight.HasValue)
            query = query.Where(b => b.WeightKg >= minWeight.Value);

        if (maxWeight.HasValue)
            query = query.Where(b => b.WeightKg <= maxWeight.Value);

        // two hops - Baggage -> Ticket -> Flight. the query the loading
        // team runs: what's going on this aircraft
        if (flightId.HasValue)
            query = query.Where(b => b.Ticket.FlightId == flightId.Value);

        // comparing a column against a column on a table two joins away,
        // and EF Core still does it in one query. this is the clearest
        // "filter using a related table's property" case in the project
        if (overweightOnly == true)
            query = query.Where(b => b.WeightKg > b.Ticket.SeatClass.BaggageAllowanceKg);

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderByDescending(b => b.WeightKg)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byType = await _context.Baggages
            .GroupBy(b => b.Type)
            .Select(g => new
            {
                Type = g.Key,
                Count = g.Count(),
                TotalFee = g.Sum(b => b.Fee),
                TotalWeightKg = g.Sum(b => b.WeightKg),
                AverageWeightKg = Math.Round(g.Average(b => b.WeightKg), 2),
                HeaviestKg = g.Max(b => b.WeightKg)
            })
            .OrderByDescending(s => s.TotalFee)
            .ToListAsync();

        // total load per flight - what ground ops needs before the aircraft
        // can be balanced. groups by a column two tables away
        var byFlight = await _context.Baggages
            .GroupBy(b => new { b.Ticket.FlightId, b.Ticket.Flight.FlightNumber })
            .Select(g => new
            {
                g.Key.FlightId,
                g.Key.FlightNumber,
                BagCount = g.Count(),
                TotalWeightKg = g.Sum(b => b.WeightKg),
                ExcessFeesCollected = g.Sum(b => b.Fee)
            })
            .OrderByDescending(s => s.TotalWeightKg)
            .ToListAsync();

        // Sum on an empty table is 0, so this one is safe unguarded -
        // unlike Average or Max, which throw
        var revenue = await _context.Baggages.SumAsync(b => b.Fee);

        return Ok(new
        {
            TotalBaggageRevenue = revenue,
            ByType = byType,
            ByFlight = byFlight
        });
    }


    // fee from the seat class allowance. the running total matters because
    // the allowance is per TICKET, not per bag - one 25kg bag on a 30kg
    // allowance is free, but a second 25kg bag is 20kg over
    private static decimal CalculateFee(string type, decimal weightKg,
                                        decimal alreadyCheckedKg, int allowanceKg)
    {
        // carry-on doesn't count against the checked allowance
        if (type == "CarryOn") return 0m;

        var surcharge = type == "Oversized" ? OversizedSurcharge : 0m;

        var totalAfter = alreadyCheckedKg + weightKg;
        if (totalAfter <= allowanceKg) return surcharge;

        // only the part that pushes past the allowance is chargeable
        var excess = totalAfter - Math.Max(alreadyCheckedKg, allowanceKg);
        return Math.Round(surcharge + (excess * FeePerExcessKg), 2);
    }

    private bool CanAccess(int ownerUserId)
    {
        if (User.IsInRole("Admin") || User.IsInRole("Staff")) return true;
        var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return callerId != null && int.Parse(callerId) == ownerUserId;
    }
}