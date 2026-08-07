using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TicketsController : ControllerBase
{
    private readonly AppDbContext _context;

    public TicketsController(AppDbContext context) => _context = context;

    public record TicketCreateDto(int BookingId, int FlightId, int SeatClassId, string SeatNumber, decimal Price, string PassengerName);
    public record TicketUpdateDto(int BookingId, int FlightId, int SeatClassId, string SeatNumber, decimal Price, string PassengerName);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] TicketCreateDto dto)
    {
        var ticket = new Ticket
        {
            BookingId = dto.BookingId,
            FlightId = dto.FlightId,
            SeatClassId = dto.SeatClassId,
            SeatNumber = dto.SeatNumber,
            Price = dto.Price,
            PassengerName = dto.PassengerName
        };
        _context.Tickets.Add(ticket);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = ticket.Id }, ticket);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] TicketUpdateDto dto)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();
        ticket.BookingId = dto.BookingId;
        ticket.FlightId = dto.FlightId;
        ticket.SeatClassId = dto.SeatClassId;
        ticket.SeatNumber = dto.SeatNumber;
        ticket.Price = dto.Price;
        ticket.PassengerName = dto.PassengerName;
        await _context.SaveChangesAsync();
        return Ok(ticket);
    }

    [HttpPatch("{id}/seat")]
    [Authorize]
    public async Task<IActionResult> UpdateSeatNumber(int id, [FromBody] string newSeatNumber)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();
        ticket.SeatNumber = newSeatNumber;
        await _context.SaveChangesAsync();
        return Ok(ticket);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();
        _context.Tickets.Remove(ticket);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Tickets
            .Include(t => t.Booking)
            .Include(t => t.Flight)
            .Include(t => t.SeatClass)
            .Include(t => t.Baggages)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var ticket = await _context.Tickets
            .Include(t => t.Booking)
            .Include(t => t.Flight)
            .Include(t => t.SeatClass)
            .Include(t => t.Baggages)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();
        return Ok(ticket);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] int? flightId, [FromQuery] int? seatClassId, [FromQuery] decimal? minPrice)
    {
        var query = _context.Tickets.AsQueryable();
        if (flightId.HasValue)
            query = query.Where(t => t.FlightId == flightId.Value);
        if (seatClassId.HasValue)
            query = query.Where(t => t.SeatClassId == seatClassId.Value);
        if (minPrice.HasValue)
            query = query.Where(t => t.Price >= minPrice.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Tickets
            .GroupBy(t => t.FlightId)
            .Select(g => new { FlightId = g.Key, TotalTickets = g.Count(), TotalRevenue = g.Sum(t => t.Price) })
            .OrderByDescending(s => s.TotalRevenue)
            .ToListAsync();
        return Ok(stats);
    }
}