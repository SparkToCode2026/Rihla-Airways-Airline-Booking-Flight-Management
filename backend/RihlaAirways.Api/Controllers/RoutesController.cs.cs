using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using Route = RihlaAirways.Api.Models.Route; // ⬅️ THIS IS THE ALIAS – DO NOT REMOVE

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RoutesController : ControllerBase
{
    private readonly AppDbContext _context;

    public RoutesController(AppDbContext context) => _context = context;

    public record RouteCreateDto(int OriginAirportId, int DestinationAirportId, int DistanceKm, int EstimatedDurationMin);
    public record RouteUpdateDto(int OriginAirportId, int DestinationAirportId, int DistanceKm, int EstimatedDurationMin);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] RouteCreateDto dto)
    {
        var route = new Route
        {
            OriginAirportId = dto.OriginAirportId,
            DestinationAirportId = dto.DestinationAirportId,
            DistanceKm = dto.DistanceKm,
            EstimatedDurationMin = dto.EstimatedDurationMin
        };
        _context.Routes.Add(route);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = route.Id }, route);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] RouteUpdateDto dto)
    {
        var route = await _context.Routes.FindAsync(id);
        if (route == null) return NotFound();
        route.OriginAirportId = dto.OriginAirportId;
        route.DestinationAirportId = dto.DestinationAirportId;
        route.DistanceKm = dto.DistanceKm;
        route.EstimatedDurationMin = dto.EstimatedDurationMin;
        await _context.SaveChangesAsync();
        return Ok(route);
    }

    [HttpPatch("{id}/duration")]
    [Authorize]
    public async Task<IActionResult> UpdateDuration(int id, [FromBody] int newDurationMin)
    {
        var route = await _context.Routes.FindAsync(id);
        if (route == null) return NotFound();
        route.EstimatedDurationMin = newDurationMin;
        await _context.SaveChangesAsync();
        return Ok(route);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var route = await _context.Routes.FindAsync(id);
        if (route == null) return NotFound();
        _context.Routes.Remove(route);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Routes
            .Include(r => r.OriginAirport)
            .Include(r => r.DestinationAirport)
            .Include(r => r.Flights)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var route = await _context.Routes
            .Include(r => r.OriginAirport)
            .Include(r => r.DestinationAirport)
            .Include(r => r.Flights)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound();
        return Ok(route);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] int? originId, [FromQuery] int? destinationId, [FromQuery] int? maxDistance)
    {
        var query = _context.Routes.AsQueryable();
        if (originId.HasValue)
            query = query.Where(r => r.OriginAirportId == originId.Value);
        if (destinationId.HasValue)
            query = query.Where(r => r.DestinationAirportId == destinationId.Value);
        if (maxDistance.HasValue)
            query = query.Where(r => r.DistanceKm <= maxDistance.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Routes
            .Select(r => new
            {
                TotalRoutes = _context.Routes.Count(),
                AverageDistance = _context.Routes.Average(r => r.DistanceKm),
                LongestRoute = _context.Routes.Max(r => r.DistanceKm)
            })
            .FirstOrDefaultAsync();
        return Ok(stats);
    }
}