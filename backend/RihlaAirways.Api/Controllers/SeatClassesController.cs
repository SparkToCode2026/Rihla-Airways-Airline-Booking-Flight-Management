using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SeatClassesController : ControllerBase
{
    private readonly AppDbContext _context;

    public SeatClassesController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // the Range on PriceMultiplier is doing real work here. two reasons:
    // the column is decimal(5,2) so anything over 999.99 throws a truncation
    // error, and TicketsController multiplies the base fare by this value -
    // a 0 or a negative would hand out free or negative-priced tickets
    public record SeatClassCreateDto(
        [Required][MaxLength(50)] string Name,
        [Required][Range(0.1, 999.99)] decimal PriceMultiplier,
        [Required][Range(0, 200)] int BaggageAllowanceKg);

    public record SeatClassUpdateDto(
        [Required][MaxLength(50)] string Name,
        [Required][Range(0.1, 999.99)] decimal PriceMultiplier,
        [Required][Range(0, 200)] int BaggageAllowanceKg);

    public record BaggageAllowanceDto([Required][Range(0, 200)] int BaggageAllowanceKg);

    // --- output DTO ---
    // Include(s => s.Tickets) was returning every ticket row inside each
    // seat class, and Ticket carries PassengerName - so an anonymous call
    // to /api/seatclasses leaked every passenger name in the system.
    // we only ever wanted the COUNT, so that's all this returns
    public record SeatClassResponseDto(
        int Id, string Name, decimal PriceMultiplier,
        int BaggageAllowanceKg, int TicketCount);

    // never chain .OrderBy() onto the RESULT of this. it's a positional
    // record, so once the values are inside a constructor call EF Core
    // can't map .PriceMultiplier back to a column and the query dies at
    // runtime with "could not be translated" - compiles fine either way.
    // sort the entities first, then project
    private static readonly Func<IQueryable<SeatClass>, IQueryable<SeatClassResponseDto>> ToDto =
        q => q.Select(s => new SeatClassResponseDto(
            s.Id, s.Name, s.PriceMultiplier, s.BaggageAllowanceKg, s.Tickets.Count));


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize(Roles = "Admin")]   // pricing rules are not passenger-editable
    public async Task<IActionResult> Create([FromBody] SeatClassCreateDto dto)
    {
        // SeatClasses.Name is a unique index - a duplicate throws
        // DbUpdateException and the client gets a raw 500 instead of
        // something that explains itself
        if (await _context.SeatClasses.AnyAsync(s => s.Name == dto.Name))
            return Conflict($"A seat class named '{dto.Name}' already exists.");

        var seatClass = new SeatClass
        {
            Name = dto.Name,
            PriceMultiplier = dto.PriceMultiplier,
            BaggageAllowanceKg = dto.BaggageAllowanceKg
        };

        _context.SeatClasses.Add(seatClass);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = seatClass.Id },
            new SeatClassResponseDto(seatClass.Id, seatClass.Name,
                seatClass.PriceMultiplier, seatClass.BaggageAllowanceKg, 0));
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] SeatClassUpdateDto dto)
    {
        var seatClass = await _context.SeatClasses.FindAsync(id);
        if (seatClass == null) return NotFound();

        // note the "s.Id != id" - without it, saving a seat class with its
        // name unchanged conflicts with itself. same trap as the seat check
        // in TicketsController
        if (await _context.SeatClasses.AnyAsync(s => s.Name == dto.Name && s.Id != id))
            return Conflict($"A seat class named '{dto.Name}' already exists.");

        seatClass.Name = dto.Name;
        seatClass.PriceMultiplier = dto.PriceMultiplier;
        seatClass.BaggageAllowanceKg = dto.BaggageAllowanceKg;
        await _context.SaveChangesAsync();

        // worth knowing: changing the multiplier does NOT reprice existing
        // tickets. Ticket.Price is frozen at purchase on purpose - that's
        // the whole reason we store it instead of computing it on read
        return Ok(await ToDto(_context.SeatClasses.Where(s => s.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/baggage")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateBaggageAllowance(
        int id, [FromBody] BaggageAllowanceDto dto)
    {
        // DTO not a bare int - a raw [FromBody] int works in postman but
        // swagger renders it oddly and the range attribute has nowhere
        // to live otherwise
        var seatClass = await _context.SeatClasses.FindAsync(id);
        if (seatClass == null) return NotFound();

        seatClass.BaggageAllowanceKg = dto.BaggageAllowanceKg;
        await _context.SaveChangesAsync();

        // note this does NOT recalculate fees on baggage already checked in -
        // Baggage.Fee is frozen at check-in for the same reason Ticket.Price
        // is frozen at purchase
        return Ok(await ToDto(_context.SeatClasses.Where(s => s.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var seatClass = await _context.SeatClasses.FindAsync(id);
        if (seatClass == null) return NotFound();

        // Ticket.SeatClassId is Restrict in the DbContext - deleting a class
        // that tickets reference throws DbUpdateException. same guard shape
        // as the bookings check in UsersController
        var ticketCount = await _context.Tickets.CountAsync(t => t.SeatClassId == id);
        if (ticketCount > 0)
            return Conflict($"Cannot delete '{seatClass.Name}' — {ticketCount} ticket(s) use it.");

        _context.SeatClasses.Remove(seatClass);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entity) ============
    [HttpGet]
    [AllowAnonymous]   // fine to stay anonymous now that the DTO only
                       // exposes a count instead of the ticket rows -
                       // customers do need to browse fare classes
    public async Task<IActionResult> GetAll()
    {
        // OrderBy moved INSIDE the ToDto call, onto the entities. it used to
        // be chained onto the result, which compiles but throws at runtime -
        // EF Core can't see through the record constructor to find the
        // column. new fix
        return Ok(await ToDto(
            _context.SeatClasses.OrderBy(s => s.PriceMultiplier))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var seatClass = await ToDto(_context.SeatClasses.Where(s => s.Id == id))
            .FirstOrDefaultAsync();
        return seatClass == null ? NotFound() : Ok(seatClass);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter(
        [FromQuery] string? name,
        [FromQuery] decimal? minMultiplier,
        [FromQuery] decimal? maxMultiplier,
        [FromQuery] int? minBaggageKg)
    {
        var query = _context.SeatClasses.AsQueryable();

        if (!string.IsNullOrWhiteSpace(name))
            query = query.Where(s => s.Name.Contains(name));

        // min and max together give a real range filter, which is what the
        // spec asks for. min alone is only half of one
        if (minMultiplier.HasValue)
            query = query.Where(s => s.PriceMultiplier >= minMultiplier.Value);

        if (maxMultiplier.HasValue)
            query = query.Where(s => s.PriceMultiplier <= maxMultiplier.Value);

        if (minBaggageKg.HasValue)
            query = query.Where(s => s.BaggageAllowanceKg >= minBaggageKg.Value);

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderBy(s => s.PriceMultiplier)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // the original had AvgPriceMultiplier = _context.SeatClasses.Average(...)
        // INSIDE the per-row Select. that computes the average across ALL
        // seat classes and then stamps the same number on every row, so
        // Economy, Business and First all reported an identical "average".
        // it's a global constant pretending to be a per-row stat, and it may
        // not even translate to SQL since it reaches back into the context
        // mid-query. new fix - real per-class aggregates below.
        //
        // note this OrderBy is fine AFTER the Select, unlike ToDto above -
        // anonymous types (new { ... }) keep their property names visible
        // to EF Core, positional records don't
        //
        // the request-failed bug: this used to compute AverageTicketPrice
        // with a ternary INSIDE the query - `s.Tickets.Any() ? s.Tickets
        // .Average(...) : 0m`. that's a per-row conditional average over a
        // correlated navigation collection, and EF Core throws
        // "could not be translated" at runtime trying to turn it into SQL -
        // same trap the AirplanesController stats comment already warns
        // about. fix follows that same pattern: pull the safe aggregates
        // (Count/Sum are 0 on an empty set, no guard needed) out of the
        // database, then do the division in memory after ToListAsync
        var raw = await _context.SeatClasses
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.PriceMultiplier,
                s.BaggageAllowanceKg,
                TicketsSold = s.Tickets.Count,
                // aggregates over THIS class's tickets, not the whole table
                TotalRevenue = s.Tickets.Sum(t => t.Price)
            })
            .ToListAsync();

        var stats = raw
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.PriceMultiplier,
                s.BaggageAllowanceKg,
                s.TicketsSold,
                s.TotalRevenue,
                AverageTicketPrice = s.TicketsSold > 0
                    ? Math.Round(s.TotalRevenue / s.TicketsSold, 2)
                    : 0m
            })
            .OrderByDescending(s => s.TotalRevenue)
            .ToList();

        return Ok(stats);
    }
}