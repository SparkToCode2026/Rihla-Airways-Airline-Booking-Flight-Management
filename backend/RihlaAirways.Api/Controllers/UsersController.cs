using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;

    public UsersController(AppDbContext context) => _context = context;

    // --- input DTOs --- Data Transfer Object
    // DTO it is a simple class or record used to carry data between different layers of an application
    // (like from a database or service layer to a web API controller or client).
    // note the validation attributes - the spec asks for "model validation /
    // DTO" and a bare record with no attributes only does half of that.
    // [ApiController] auto-returns 400 with the error list when these fail,
    // so we never have to check ModelState by hand
    public record UserCreateDto(
        [Required][MaxLength(100)] string Name,
        [Required][EmailAddress][MaxLength(150)] string Email,
        [Required][MinLength(6)] string Password,
        [Required] string Role);

    // no Role here on purpose - see the note on Update below
    public record UserUpdateDto(
        [Required][MaxLength(100)] string Name,
        [Required][EmailAddress][MaxLength(150)] string Email);

    public record RoleUpdateDto([Required] string Role);

    // --- output DTO ---
    // this is the fix for TWO problems at once:
    // 1) PasswordHash never leaves the server - it's simply not a field here
    // 2) no navigation back to User, so the json serializer can't loop
    //    forever on User -> Bookings -> User -> Bookings
    // every controller in this project needs its own version of this.
    // returning the raw entity is what causes the object-cycle crash
    public record UserResponseDto(
        int Id, string Name, string Email, string Role, DateTime CreatedAt,
        string? PassportNumber, int BookingCount);

    // one place that defines what a User looks like on the way out -
    // beats repeating the same Select in five endpoints
    private static readonly Func<IQueryable<User>, IQueryable<UserResponseDto>> ToDto =
        q => q.Select(u => new UserResponseDto(
            u.Id, u.Name, u.Email, u.Role, u.CreatedAt,
            u.PassengerProfile != null ? u.PassengerProfile.PassportNumber : null,
            u.Bookings.Count));

    // valid roles - the string-not-enum decision means nothing stops
    // someone POSTing Role = "SuperAdmin" unless we check it ourselves
    private static readonly string[] ValidRoles = { "Passenger", "Staff", "Admin" };


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] UserCreateDto dto)
    {
        if (!ValidRoles.Contains(dto.Role))
            return BadRequest($"Role must be one of: {string.Join(", ", ValidRoles)}");

        // edge case first: Users.Email has a unique index, so a duplicate
        // throws DbUpdateException and the client gets an ugly 500.
        // checking first turns that into a clean 409
        if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
            return Conflict($"A user with email {dto.Email} already exists.");

        var user = new User
        {
            Name = dto.Name,
            Email = dto.Email,
            // the client sends a PLAIN password and we hash it here.
            // taking a pre-made hash from the request body would mean
            // trusting the caller to hash correctly, which defeats the point.
            // swap this for BCrypt when we add the auth package - flagged
            // so nobody forgets it's a placeholder
            PasswordHash = HashPassword(dto.Password),
            Role = dto.Role,
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // note we return the DTO, not the entity - otherwise the freshly
        // created PasswordHash goes straight back to the client
        return CreatedAtAction(nameof(GetById), new { id = user.Id },
            new UserResponseDto(user.Id, user.Name, user.Email, user.Role,
                                user.CreatedAt, null, 0));
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] UserUpdateDto dto)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();

        // Role is deliberately NOT updatable here. it was in the original
        // version, which meant any logged-in passenger could PUT their own
        // record with Role = "Admin" and promote themselves. role changes
        // go through the Admin-only PATCH below - new fix
        if (dto.Email != user.Email &&
            await _context.Users.AnyAsync(u => u.Email == dto.Email))
            return Conflict($"Email {dto.Email} is already taken.");

        user.Name = dto.Name;
        user.Email = dto.Email;
        await _context.SaveChangesAsync();

        return Ok(new UserResponseDto(user.Id, user.Name, user.Email,
                                      user.Role, user.CreatedAt, null, 0));
    }


    // ============ 3. PATCH — second distinct update (status change) ============
    [HttpPatch("{id}/role")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] RoleUpdateDto dto)
    {
        // a DTO instead of [FromBody] string - a bare string means swagger
        // makes you type "Admin" WITH the quotes, which trips everyone up.
        // an object is also easier to extend later
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (!ValidRoles.Contains(dto.Role))
            return BadRequest($"Role must be one of: {string.Join(", ", ValidRoles)}");

        user.Role = dto.Role;
        await _context.SaveChangesAsync();

        return Ok(new UserResponseDto(user.Id, user.Name, user.Email,
                                      user.Role, user.CreatedAt, null, 0));
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();

        // edge case: Booking.UserId is Restrict in the DbContext, on purpose -
        // we don't want financial history vanishing. so SaveChanges would
        // throw DbUpdateException here and the client sees a raw 500.
        // catching it up front gives a message that actually explains why
        if (await _context.Bookings.AnyAsync(b => b.UserId == id))
            return Conflict("Cannot delete a user who has bookings. " +
                            "Deactivate the account instead.");

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entity) ============
    [HttpGet]
    [Authorize(Roles = "Admin")]   // was AllowAnonymous - this endpoint
                                   // dumps every email in the system
    public async Task<IActionResult> GetAll()
    {
        // the Select in ToDto does the job Include() was doing, but better:
        // EF Core translates it into a single SQL query that pulls ONLY the
        // columns we need, instead of loading every booking row into memory
        // just to count them
        return Ok(await ToDto(_context.Users).ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        var user = await ToDto(_context.Users.Where(u => u.Id == id))
            .FirstOrDefaultAsync();

        return user == null ? NotFound() : Ok(user);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Filter(
        [FromQuery] string? role,
        [FromQuery] string? email,
        [FromQuery] DateTime? registeredAfter)
    {
        // AsQueryable + conditional Where is the right pattern - nothing
        // hits the database until ToListAsync, so we're building one SQL
        // statement rather than filtering in memory
        var query = _context.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(u => u.Role == role);

        if (!string.IsNullOrWhiteSpace(email))
            query = query.Where(u => u.Email.Contains(email));

        // added a date-range filter - the spec explicitly mentions date range
        // as an example for this case, and it makes the endpoint less trivial
        if (registeredAfter.HasValue)
            query = query.Where(u => u.CreatedAt >= registeredAfter.Value);

        return Ok(await ToDto(query).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _context.Users
            .GroupBy(u => u.Role)
            .Select(g => new
            {
                Role = g.Key,
                Count = g.Count(),
                // reaching into a related table for the aggregate - the spec
                // wants GETs that cross relationships, and this one does it
                // inside the aggregate rather than with an Include
                TotalBookings = g.Sum(u => u.Bookings.Count),
                EarliestRegistration = g.Min(u => u.CreatedAt)
            })
            .OrderByDescending(s => s.Count)
            .ToListAsync();

        return Ok(stats);
    }


    // temporary - gets replaced by BCrypt.Net-Next when we do the JWT step.
    // sha256 with no salt is NOT acceptable for real passwords, it's here
    // only so the endpoint works before the auth package lands
    private static string HashPassword(string password)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(bytes);
    }
}