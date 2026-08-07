using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PassengerProfilesController : ControllerBase
{
    private readonly AppDbContext _context;

    public PassengerProfilesController(AppDbContext context) => _context = context;

    public record PassengerProfileCreateDto(int UserId, string PassportNumber, string Nationality, DateOnly DateOfBirth, string PhoneNumber);
    public record PassengerProfileUpdateDto(int UserId, string PassportNumber, string Nationality, DateOnly DateOfBirth, string PhoneNumber);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] PassengerProfileCreateDto dto)
    {
        var profile = new PassengerProfile
        {
            UserId = dto.UserId,
            PassportNumber = dto.PassportNumber,
            Nationality = dto.Nationality,
            DateOfBirth = dto.DateOfBirth,
            PhoneNumber = dto.PhoneNumber
        };
        _context.PassengerProfiles.Add(profile);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = profile.Id }, profile);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] PassengerProfileUpdateDto dto)
    {
        var profile = await _context.PassengerProfiles.FindAsync(id);
        if (profile == null) return NotFound();
        profile.UserId = dto.UserId;
        profile.PassportNumber = dto.PassportNumber;
        profile.Nationality = dto.Nationality;
        profile.DateOfBirth = dto.DateOfBirth;
        profile.PhoneNumber = dto.PhoneNumber;
        await _context.SaveChangesAsync();
        return Ok(profile);
    }

    [HttpPatch("{id}/phone")]
    [Authorize]
    public async Task<IActionResult> UpdatePhone(int id, [FromBody] string newPhone)
    {
        var profile = await _context.PassengerProfiles.FindAsync(id);
        if (profile == null) return NotFound();
        profile.PhoneNumber = newPhone;
        await _context.SaveChangesAsync();
        return Ok(profile);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var profile = await _context.PassengerProfiles.FindAsync(id);
        if (profile == null) return NotFound();
        _context.PassengerProfiles.Remove(profile);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.PassengerProfiles
            .Include(p => p.User)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var profile = await _context.PassengerProfiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (profile == null) return NotFound();
        return Ok(profile);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? nationality, [FromQuery] string? passportNumber)
    {
        var query = _context.PassengerProfiles.AsQueryable();
        if (!string.IsNullOrEmpty(nationality))
            query = query.Where(p => p.Nationality.Contains(nationality));
        if (!string.IsNullOrEmpty(passportNumber))
            query = query.Where(p => p.PassportNumber.Contains(passportNumber));
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.PassengerProfiles
            .GroupBy(p => p.Nationality)
            .Select(g => new { Nationality = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        return Ok(stats);
    }
}