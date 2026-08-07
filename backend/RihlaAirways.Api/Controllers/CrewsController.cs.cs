using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CrewsController : ControllerBase
{
    private readonly AppDbContext _context;

    public CrewsController(AppDbContext context) => _context = context;

    public record CrewCreateDto(string Name, string Role, string LicenseNumber, string PassportNumber, string Nationality, DateOnly DateOfBirth);
    public record CrewUpdateDto(string Name, string Role, string LicenseNumber, string PassportNumber, string Nationality, DateOnly DateOfBirth);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CrewCreateDto dto)
    {
        var crew = new Crew
        {
            Name = dto.Name,
            Role = dto.Role,
            LicenseNumber = dto.LicenseNumber,
            PassportNumber = dto.PassportNumber,
            Nationality = dto.Nationality,
            DateOfBirth = dto.DateOfBirth
        };
        _context.Crews.Add(crew);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = crew.Id }, crew);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] CrewUpdateDto dto)
    {
        var crew = await _context.Crews.FindAsync(id);
        if (crew == null) return NotFound();
        crew.Name = dto.Name;
        crew.Role = dto.Role;
        crew.LicenseNumber = dto.LicenseNumber;
        crew.PassportNumber = dto.PassportNumber;
        crew.Nationality = dto.Nationality;
        crew.DateOfBirth = dto.DateOfBirth;
        await _context.SaveChangesAsync();
        return Ok(crew);
    }

    [HttpPatch("{id}/role")]
    [Authorize]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] string newRole)
    {
        var crew = await _context.Crews.FindAsync(id);
        if (crew == null) return NotFound();
        crew.Role = newRole;
        await _context.SaveChangesAsync();
        return Ok(crew);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var crew = await _context.Crews.FindAsync(id);
        if (crew == null) return NotFound();
        _context.Crews.Remove(crew);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Crews
            .Include(c => c.FlightCrews)
            .ThenInclude(fc => fc.Flight)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var crew = await _context.Crews
            .Include(c => c.FlightCrews)
            .ThenInclude(fc => fc.Flight)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (crew == null) return NotFound();
        return Ok(crew);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? role, [FromQuery] string? nationality)
    {
        var query = _context.Crews.AsQueryable();
        if (!string.IsNullOrEmpty(role))
            query = query.Where(c => c.Role.Contains(role));
        if (!string.IsNullOrEmpty(nationality))
            query = query.Where(c => c.Nationality.Contains(nationality));
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Crews
            .GroupBy(c => c.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        return Ok(stats);
    }
}