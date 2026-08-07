using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BookingsController : ControllerBase
{
    private readonly AppDbContext _context;

    public BookingsController(AppDbContext context) => _context = context;

    public record BookingCreateDto(int UserId, string Status, decimal TotalAmount);
    public record BookingUpdateDto(int UserId, string Status, decimal TotalAmount);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] BookingCreateDto dto)
    {
        var booking = new Booking
        {
            UserId = dto.UserId,
            Status = dto.Status,
            TotalAmount = dto.TotalAmount,
            BookingDate = DateTime.UtcNow
        };
        _context.Bookings.Add(booking);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = booking.Id }, booking);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] BookingUpdateDto dto)
    {
        var booking = await _context.Bookings.FindAsync(id);
        if (booking == null) return NotFound();
        booking.UserId = dto.UserId;
        booking.Status = dto.Status;
        booking.TotalAmount = dto.TotalAmount;
        await _context.SaveChangesAsync();
        return Ok(booking);
    }

    [HttpPatch("{id}/status")]
    [Authorize]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] string newStatus)
    {
        var booking = await _context.Bookings.FindAsync(id);
        if (booking == null) return NotFound();
        booking.Status = newStatus;
        await _context.SaveChangesAsync();
        return Ok(booking);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var booking = await _context.Bookings.FindAsync(id);
        if (booking == null) return NotFound();
        _context.Bookings.Remove(booking);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Bookings
            .Include(b => b.User)
            .Include(b => b.Tickets)
            .Include(b => b.Payment)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var booking = await _context.Bookings
            .Include(b => b.User)
            .Include(b => b.Tickets)
            .Include(b => b.Payment)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (booking == null) return NotFound();
        return Ok(booking);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? status, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var query = _context.Bookings.AsQueryable();
        if (!string.IsNullOrEmpty(status))
            query = query.Where(b => b.Status == status);
        if (fromDate.HasValue)
            query = query.Where(b => b.BookingDate >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(b => b.BookingDate <= toDate.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Bookings
            .GroupBy(b => b.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), TotalRevenue = g.Sum(b => b.TotalAmount) })
            .OrderByDescending(s => s.TotalRevenue)
            .ToListAsync();
        return Ok(stats);
    }
}