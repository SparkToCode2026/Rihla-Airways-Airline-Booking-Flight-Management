using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AirplanesController : ControllerBase
{
    private readonly AppDbContext _context;

    public AirplanesController(AppDbContext context) => _context = context;

    public record AirplaneCreateDto(string Model, string RegistrationNumber, int Capacity, int ManufactureYear);
    public record AirplaneUpdateDto(string Model, string RegistrationNumber, int Capacity, int ManufactureYear);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] AirplaneCreateDto dto)
    {
        var airplane = new Airplane
        {
            Model = dto.Model,
            RegistrationNumber = dto.RegistrationNumber,
            Capacity = dto.Capacity,
            ManufactureYear = dto.ManufactureYear
        };
        _context.Airplanes.Add(airplane);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = airplane.Id }, airplane);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] AirplaneUpdateDto dto)
    {
        var airplane = await _context.Airplanes.FindAsync(id);
        if (airplane == null) return NotFound();
        airplane.Model = dto.Model;
        airplane.RegistrationNumber = dto.RegistrationNumber;
        airplane.Capacity = dto.Capacity;
        airplane.ManufactureYear = dto.ManufactureYear;
        await _context.SaveChangesAsync();
        return Ok(airplane);
    }

    [HttpPatch("{id}/capacity")]
    [Authorize]
    public async Task<IActionResult> UpdateCapacity(int id, [FromBody] int newCapacity)
    {
        var airplane = await _context.Airplanes.FindAsync(id);
        if (airplane == null) return NotFound();
        airplane.Capacity = newCapacity;
        await _context.SaveChangesAsync();
        return Ok(airplane);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var airplane = await _context.Airplanes.FindAsync(id);
        if (airplane == null) return NotFound();
        _context.Airplanes.Remove(airplane);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Airplanes
            .Include(a => a.Flights)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var airplane = await _context.Airplanes
            .Include(a => a.Flights)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (airplane == null) return NotFound();
        return Ok(airplane);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? model, [FromQuery] int? minYear, [FromQuery] int? maxYear)
    {
        var query = _context.Airplanes.AsQueryable();
        if (!string.IsNullOrEmpty(model))
            query = query.Where(a => a.Model.Contains(model));
        if (minYear.HasValue)
            query = query.Where(a => a.ManufactureYear >= minYear.Value);
        if (maxYear.HasValue)
            query = query.Where(a => a.ManufactureYear <= maxYear.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Airplanes
            .GroupBy(a => a.Model)
            .Select(g => new { Model = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        return Ok(stats);
    }
}