using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AirportsController : ControllerBase
{
    private readonly AppDbContext _context;

    public AirportsController(AppDbContext context) => _context = context;

    public record AirportCreateDto(string Code, string Name, string City, string Country);
    public record AirportUpdateDto(string Code, string Name, string City, string Country);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] AirportCreateDto dto)
    {
        var airport = new Airport
        {
            Code = dto.Code.ToUpperInvariant(),
            Name = dto.Name,
            City = dto.City,
            Country = dto.Country
        };
        _context.Airports.Add(airport);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = airport.Id }, airport);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] AirportUpdateDto dto)
    {
        var airport = await _context.Airports.FindAsync(id);
        if (airport == null) return NotFound();
        airport.Code = dto.Code.ToUpperInvariant();
        airport.Name = dto.Name;
        airport.City = dto.City;
        airport.Country = dto.Country;
        await _context.SaveChangesAsync();
        return Ok(airport);
    }

    [HttpPatch("{id}/city")]
    [Authorize]
    public async Task<IActionResult> UpdateCity(int id, [FromBody] string newCity)
    {
        var airport = await _context.Airports.FindAsync(id);
        if (airport == null) return NotFound();
        airport.City = newCity;
        await _context.SaveChangesAsync();
        return Ok(airport);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var airport = await _context.Airports.FindAsync(id);
        if (airport == null) return NotFound();
        _context.Airports.Remove(airport);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Airports
            .Include(a => a.DepartingRoutes)
            .Include(a => a.ArrivingRoutes)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var airport = await _context.Airports
            .Include(a => a.DepartingRoutes)
            .Include(a => a.ArrivingRoutes)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (airport == null) return NotFound();
        return Ok(airport);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? country, [FromQuery] string? city)
    {
        var query = _context.Airports.AsQueryable();
        if (!string.IsNullOrEmpty(country))
            query = query.Where(a => a.Country.Contains(country));
        if (!string.IsNullOrEmpty(city))
            query = query.Where(a => a.City.Contains(city));
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Airports
            .GroupBy(a => a.Country)
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        return Ok(stats);
    }
}