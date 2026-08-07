using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _context;

    public PaymentsController(AppDbContext context) => _context = context;

    public record PaymentCreateDto(int BookingId, decimal Amount, string Method, string Status);
    public record PaymentUpdateDto(int BookingId, decimal Amount, string Method, string Status);

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] PaymentCreateDto dto)
    {
        var payment = new Payment
        {
            BookingId = dto.BookingId,
            Amount = dto.Amount,
            Method = dto.Method,
            Status = dto.Status,
            PaymentDate = DateTime.UtcNow
        };
        _context.Payments.Add(payment);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = payment.Id }, payment);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] PaymentUpdateDto dto)
    {
        var payment = await _context.Payments.FindAsync(id);
        if (payment == null) return NotFound();
        payment.BookingId = dto.BookingId;
        payment.Amount = dto.Amount;
        payment.Method = dto.Method;
        payment.Status = dto.Status;
        await _context.SaveChangesAsync();
        return Ok(payment);
    }

    [HttpPatch("{id}/status")]
    [Authorize]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] string newStatus)
    {
        var payment = await _context.Payments.FindAsync(id);
        if (payment == null) return NotFound();
        payment.Status = newStatus;
        await _context.SaveChangesAsync();
        return Ok(payment);
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var payment = await _context.Payments.FindAsync(id);
        if (payment == null) return NotFound();
        _context.Payments.Remove(payment);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var data = await _context.Payments
            .Include(p => p.Booking)
            .ToListAsync();
        return Ok(data);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (payment == null) return NotFound();
        return Ok(payment);
    }

    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter([FromQuery] string? status, [FromQuery] string? method, [FromQuery] decimal? minAmount)
    {
        var query = _context.Payments.AsQueryable();
        if (!string.IsNullOrEmpty(status))
            query = query.Where(p => p.Status == status);
        if (!string.IsNullOrEmpty(method))
            query = query.Where(p => p.Method.Contains(method));
        if (minAmount.HasValue)
            query = query.Where(p => p.Amount >= minAmount.Value);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Payments
            .GroupBy(p => p.Method)
            .Select(g => new { Method = g.Key, TotalAmount = g.Sum(p => p.Amount), Count = g.Count() })
            .OrderByDescending(s => s.TotalAmount)
            .ToListAsync();
        return Ok(stats);
    }
}