using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;

    public UsersController(AppDbContext context) => _context = context;

    public record UserCreateDto(string Name, string Email, string PasswordHash, string Role);
    public record UserUpdateDto(string Name, string Email, string PasswordHash, string Role);

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] UserCreateDto dto)
    {
        var user = new User
        {
            Name = dto.Name,
            Email = dto.Email,
            PasswordHash = dto.PasswordHash,
            Role = dto.Role,
            CreatedAt = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = user.Id }, user);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] UserUpdateDto dto)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();
        user.Name = dto.Name;
        user.Email = dto.Email;
        user.PasswordHash = dto.PasswordHash;
        user.Role = dto.Role;
        await _context.SaveChangesAsync();
        return Ok(user);
    }

    [HttpPatch("{id}/role")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] string newRole)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();
        user.Role = newRole;
        await _context.SaveChangesAsync();
        return Ok(user);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();
        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Users
            .Include(u => u.PassengerProfile)
            .Include(u => u.Bookings)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var user = await _context.Users
            .Include(u => u.PassengerProfile)
            .Include(u => u.Bookings)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        return Ok(user);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? role, [FromQuery] string? email)
    {
        var query = _context.Users.AsQueryable();
        if (!string.IsNullOrEmpty(role))
            query = query.Where(u => u.Role == role);
        if (!string.IsNullOrEmpty(email))
            query = query.Where(u => u.Email.Contains(email));
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Users
            .GroupBy(u => u.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        return Ok(stats);
    }
}