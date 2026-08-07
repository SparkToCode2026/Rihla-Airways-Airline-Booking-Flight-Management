using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SeatClassesController : ControllerBase
{
    private readonly AppDbContext _context;

    public SeatClassesController(AppDbContext context) => _context = context;

    public record SeatClassCreateDto(string Name, decimal PriceMultiplier, int BaggageAllowanceKg);
    public record SeatClassUpdateDto(string Name, decimal PriceMultiplier, int BaggageAllowanceKg);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] SeatClassCreateDto dto)
    {
        var seatClass = new SeatClass
        {
            Name = dto.Name,
            PriceMultiplier = dto.PriceMultiplier,
            BaggageAllowanceKg = dto.BaggageAllowanceKg
        };
        _context.SeatClasses.Add(seatClass);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = seatClass.Id }, seatClass);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] SeatClassUpdateDto dto)
    {
        var seatClass = await _context.SeatClasses.FindAsync(id);
        if (seatClass == null) return NotFound();
        seatClass.Name = dto.Name;
        seatClass.PriceMultiplier = dto.PriceMultiplier;
        seatClass.BaggageAllowanceKg = dto.BaggageAllowanceKg;
        await _context.SaveChangesAsync();
        return Ok(seatClass);
    }

    [HttpPatch("{id}/baggage")]
    [Authorize]
    public async Task<IActionResult> UpdateBaggageAllowance(int id, [FromBody] int newAllowanceKg)
    {
        var seatClass = await _context.SeatClasses.FindAsync(id);
        if (seatClass == null) return NotFound();
        seatClass.BaggageAllowanceKg = newAllowanceKg;
        await _context.SaveChangesAsync();
        return Ok(seatClass);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var seatClass = await _context.SeatClasses.FindAsync(id);
        if (seatClass == null) return NotFound();
        _context.SeatClasses.Remove(seatClass);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.SeatClasses
            .Include(s => s.Tickets)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var seatClass = await _context.SeatClasses
            .Include(s => s.Tickets)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (seatClass == null) return NotFound();
        return Ok(seatClass);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? name, [FromQuery] decimal? minMultiplier)
    {
        var query = _context.SeatClasses.AsQueryable();
        if (!string.IsNullOrEmpty(name))
            query = query.Where(s => s.Name.Contains(name));
        if (minMultiplier.HasValue)
            query = query.Where(s => s.PriceMultiplier >= minMultiplier.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.SeatClasses
            .Select(s => new
            {
                s.Name,
                AvgPriceMultiplier = _context.SeatClasses.Average(sc => sc.PriceMultiplier),
                TotalTickets = s.Tickets.Count
            })
            .OrderByDescending(s => s.TotalTickets)
            .ToListAsync();
        return Ok(stats);
    }
}