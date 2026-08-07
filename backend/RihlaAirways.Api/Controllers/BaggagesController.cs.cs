using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BaggagesController : ControllerBase
{
    private readonly AppDbContext _context;

    public BaggagesController(AppDbContext context) => _context = context;

    public record BaggageCreateDto(int TicketId, int BaggageNumber, decimal WeightKg, string Type, decimal Fee);
    public record BaggageUpdateDto(int TicketId, int BaggageNumber, decimal WeightKg, string Type, decimal Fee);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] BaggageCreateDto dto)
    {
        var baggage = new Baggage
        {
            TicketId = dto.TicketId,
            BaggageNumber = dto.BaggageNumber,
            WeightKg = dto.WeightKg,
            Type = dto.Type,
            Fee = dto.Fee
        };
        _context.Baggages.Add(baggage);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = baggage.Id }, baggage);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] BaggageUpdateDto dto)
    {
        var baggage = await _context.Baggages.FindAsync(id);
        if (baggage == null) return NotFound();
        baggage.TicketId = dto.TicketId;
        baggage.BaggageNumber = dto.BaggageNumber;
        baggage.WeightKg = dto.WeightKg;
        baggage.Type = dto.Type;
        baggage.Fee = dto.Fee;
        await _context.SaveChangesAsync();
        return Ok(baggage);
    }

    [HttpPatch("{id}/fee")]
    [Authorize]
    public async Task<IActionResult> UpdateFee(int id, [FromBody] decimal newFee)
    {
        var baggage = await _context.Baggages.FindAsync(id);
        if (baggage == null) return NotFound();
        baggage.Fee = newFee;
        await _context.SaveChangesAsync();
        return Ok(baggage);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var baggage = await _context.Baggages.FindAsync(id);
        if (baggage == null) return NotFound();
        _context.Baggages.Remove(baggage);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Baggages
            .Include(b => b.Ticket)
            .ThenInclude(t => t.Booking)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var baggage = await _context.Baggages
            .Include(b => b.Ticket)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (baggage == null) return NotFound();
        return Ok(baggage);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? type, [FromQuery] decimal? minWeight, [FromQuery] decimal? maxWeight)
    {
        var query = _context.Baggages.AsQueryable();
        if (!string.IsNullOrEmpty(type))
            query = query.Where(b => b.Type.Contains(type));
        if (minWeight.HasValue)
            query = query.Where(b => b.WeightKg >= minWeight.Value);
        if (maxWeight.HasValue)
            query = query.Where(b => b.WeightKg <= maxWeight.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Baggages
            .GroupBy(b => b.Type)
            .Select(g => new { Type = g.Key, TotalFee = g.Sum(b => b.Fee), Count = g.Count() })
            .OrderByDescending(s => s.TotalFee)
            .ToListAsync();
        return Ok(stats);
    }
}