using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FlightCrewsController : ControllerBase
{
    private readonly AppDbContext _context;

    public FlightCrewsController(AppDbContext context) => _context = context;

    public record FlightCrewCreateDto(int FlightId, int CrewId, string DutyRole);
    public record FlightCrewUpdateDto(int FlightId, int CrewId, string DutyRole);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] FlightCrewCreateDto dto)
    {
        var fc = new FlightCrew
        {
            FlightId = dto.FlightId,
            CrewId = dto.CrewId,
            DutyRole = dto.DutyRole
        };
        _context.FlightCrews.Add(fc);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = fc.Id }, fc);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] FlightCrewUpdateDto dto)
    {
        var fc = await _context.FlightCrews.FindAsync(id);
        if (fc == null) return NotFound();
        fc.FlightId = dto.FlightId;
        fc.CrewId = dto.CrewId;
        fc.DutyRole = dto.DutyRole;
        await _context.SaveChangesAsync();
        return Ok(fc);
    }

    [HttpPatch("{id}/dutyrole")]
    [Authorize]
    public async Task<IActionResult> UpdateDutyRole(int id, [FromBody] string newDutyRole)
    {
        var fc = await _context.FlightCrews.FindAsync(id);
        if (fc == null) return NotFound();
        fc.DutyRole = newDutyRole;
        await _context.SaveChangesAsync();
        return Ok(fc);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var fc = await _context.FlightCrews.FindAsync(id);
        if (fc == null) return NotFound();
        _context.FlightCrews.Remove(fc);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.FlightCrews
            .Include(fc => fc.Flight)
            .Include(fc => fc.Crew)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var fc = await _context.FlightCrews
            .Include(fc => fc.Flight)
            .Include(fc => fc.Crew)
            .FirstOrDefaultAsync(fc => fc.Id == id);
        if (fc == null) return NotFound();
        return Ok(fc);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] int? flightId, [FromQuery] int? crewId, [FromQuery] string? dutyRole)
    {
        var query = _context.FlightCrews.AsQueryable();
        if (flightId.HasValue)
            query = query.Where(fc => fc.FlightId == flightId.Value);
        if (crewId.HasValue)
            query = query.Where(fc => fc.CrewId == crewId.Value);
        if (!string.IsNullOrEmpty(dutyRole))
            query = query.Where(fc => fc.DutyRole.Contains(dutyRole));
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.FlightCrews
            .GroupBy(fc => fc.DutyRole)
            .Select(g => new { DutyRole = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        return Ok(stats);
    }
}