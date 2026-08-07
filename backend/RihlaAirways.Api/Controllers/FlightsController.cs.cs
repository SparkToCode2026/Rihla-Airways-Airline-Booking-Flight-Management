using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FlightsController : ControllerBase
{
    private readonly AppDbContext _context;

    public FlightsController(AppDbContext context) => _context = context;

    public record FlightCreateDto(string FlightNumber, int RouteId, int AirplaneId, DateTime DepartureTime, DateTime ArrivalTime, string Status);
    public record FlightUpdateDto(string FlightNumber, int RouteId, int AirplaneId, DateTime DepartureTime, DateTime ArrivalTime, string Status);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] FlightCreateDto dto)
    {
        var flight = new Flight
        {
            FlightNumber = dto.FlightNumber,
            RouteId = dto.RouteId,
            AirplaneId = dto.AirplaneId,
            DepartureTime = dto.DepartureTime,
            ArrivalTime = dto.ArrivalTime,
            Status = dto.Status
        };
        _context.Flights.Add(flight);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = flight.Id }, flight);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] FlightUpdateDto dto)
    {
        var flight = await _context.Flights.FindAsync(id);
        if (flight == null) return NotFound();
        flight.FlightNumber = dto.FlightNumber;
        flight.RouteId = dto.RouteId;
        flight.AirplaneId = dto.AirplaneId;
        flight.DepartureTime = dto.DepartureTime;
        flight.ArrivalTime = dto.ArrivalTime;
        flight.Status = dto.Status;
        await _context.SaveChangesAsync();
        return Ok(flight);
    }

    [HttpPatch("{id}/status")]
    [Authorize]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] string newStatus)
    {
        var flight = await _context.Flights.FindAsync(id);
        if (flight == null) return NotFound();
        flight.Status = newStatus;
        await _context.SaveChangesAsync();
        return Ok(flight);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var flight = await _context.Flights.FindAsync(id);
        if (flight == null) return NotFound();
        _context.Flights.Remove(flight);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Flights
            .Include(f => f.Route)
            .ThenInclude(r => r.OriginAirport)
            .Include(f => f.Route)
            .ThenInclude(r => r.DestinationAirport)
            .Include(f => f.Airplane)
            .Include(f => f.Tickets)
            .Include(f => f.FlightCrews)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var flight = await _context.Flights
            .Include(f => f.Route)
            .ThenInclude(r => r.OriginAirport)
            .Include(f => f.Route)
            .ThenInclude(r => r.DestinationAirport)
            .Include(f => f.Airplane)
            .Include(f => f.Tickets)
            .Include(f => f.FlightCrews)
            .FirstOrDefaultAsync(f => f.Id == id);
        if (flight == null) return NotFound();
        return Ok(flight);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? status, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var query = _context.Flights.AsQueryable();
        if (!string.IsNullOrEmpty(status))
            query = query.Where(f => f.Status == status);
        if (fromDate.HasValue)
            query = query.Where(f => f.DepartureTime >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(f => f.DepartureTime <= toDate.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Flights
            .GroupBy(f => f.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        return Ok(stats);
    }
}