using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PassengerProfilesController : ControllerBase
{
    private readonly AppDbContext _context;

    public PassengerProfilesController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    public record PassengerProfileCreateDto(
        [Required] int UserId,
        [Required][MaxLength(50)] string PassportNumber,
        [Required][MaxLength(100)] string Nationality,
        [Required] DateOnly DateOfBirth,
        [Required][MaxLength(20)][Phone] string PhoneNumber);

    // UserId is NOT updatable - moving a profile to a different user breaks
    // the 1:1 and can collide with that user's existing profile. it was
    // updatable in the original - new fix
    public record PassengerProfileUpdateDto(
        [Required][MaxLength(50)] string PassportNumber,
        [Required][MaxLength(100)] string Nationality,
        [Required] DateOnly DateOfBirth,
        [Required][MaxLength(20)][Phone] string PhoneNumber);

    public record PhoneUpdateDto([Required][MaxLength(20)][Phone] string PhoneNumber);

    // --- output DTOs ---
    // TWO of them here, unlike the other controllers, and that's the point:
    // this table holds passport numbers and dates of birth. staff running a
    // report don't need the passport number, and the public certainly doesn't.
    // so we return different shapes depending on who's asking

    // full - only for the profile owner and Admins
    public record ProfileDetailDto(
        int Id, int UserId, string UserName, string UserEmail,
        string PassportNumber, string Nationality,
        DateOnly DateOfBirth, int Age, string PhoneNumber);

    // redacted - for staff lists and aggregate views. passport masked,
    // DOB dropped entirely, age kept because that's what fare rules need
    public record ProfileSummaryDto(
        int Id, int UserId, string UserName,
        string PassportLast4, string Nationality, int Age);

    // Age can't be computed in the query - it's [NotMapped], there's no
    // column for EF Core to translate. so we do the date maths inline
    // against DateOfBirth, which IS a real column.
    // this is the exact trap I mentioned earlier: p.Age in a Where or a
    // Select compiles fine and then throws at runtime.
    //
    // and never chain .OrderBy() onto the RESULT of this either - positional
    // record, same problem. note the property differs between the two sides:
    // UserName on the dto is User.Name on the entity
    private static readonly Func<IQueryable<PassengerProfile>, IQueryable<ProfileSummaryDto>> ToSummary =
        q => q.Select(p => new ProfileSummaryDto(
            p.Id, p.UserId, p.User.Name,
            // last 4 only - enough to confirm identity over the phone,
            // useless to anyone who scrapes the endpoint
            p.PassportNumber.Substring(p.PassportNumber.Length - 4),
            p.Nationality,
            DateTime.UtcNow.Year - p.DateOfBirth.Year));

    // is the caller allowed to see the FULL profile? owner or admin only.
    // without this, any logged-in passenger can read every passport number
    // in the database - [Authorize] alone only checks that you're SOMEBODY,
    // not that you're the right somebody
    private bool CanAccess(int profileUserId)
    {
        if (User.IsInRole("Admin")) return true;
        var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return callerId != null && int.Parse(callerId) == profileUserId;
    }


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] PassengerProfileCreateDto dto)
    {
        if (!CanAccess(dto.UserId))
            return Forbid();

        var user = await _context.Users.FindAsync(dto.UserId);
        if (user == null) return BadRequest($"User {dto.UserId} not found.");

        // edge case first: PassengerProfiles.UserId is a UNIQUE index -
        // that index IS the one-to-one. a second profile for the same user
        // throws DbUpdateException instead of explaining itself
        if (await _context.PassengerProfiles.AnyAsync(p => p.UserId == dto.UserId))
            return Conflict($"User {dto.UserId} already has a passenger profile.");

        // a Staff or Admin account has no reason to hold travel documents -
        // that's the whole reason this table is separate from User
        if (user.Role != "Passenger")
            return BadRequest("Only Passenger accounts can have a passenger profile.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (dto.DateOfBirth > today)
            return BadRequest("Date of birth cannot be in the future.");

        // 120 is arbitrary but it catches the real error, which is someone
        // typing 1025 instead of 2025. without it Age returns nonsense
        if (dto.DateOfBirth < today.AddYears(-120))
            return BadRequest("Date of birth is not valid.");

        var profile = new PassengerProfile
        {
            UserId = dto.UserId,
            PassportNumber = dto.PassportNumber.ToUpper(),
            Nationality = dto.Nationality,
            DateOfBirth = dto.DateOfBirth,
            PhoneNumber = dto.PhoneNumber
        };

        _context.PassengerProfiles.Add(profile);
        await _context.SaveChangesAsync();

        // profile.Age is fine HERE - this is a loaded C# object, not a
        // query being translated to SQL. the [NotMapped] property only
        // breaks inside a Where or a Select that EF Core has to translate
        return CreatedAtAction(nameof(GetById), new { id = profile.Id },
            new ProfileDetailDto(profile.Id, user.Id, user.Name, user.Email,
                profile.PassportNumber, profile.Nationality,
                profile.DateOfBirth, profile.Age, profile.PhoneNumber));
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] PassengerProfileUpdateDto dto)
    {
        var profile = await _context.PassengerProfiles.FindAsync(id);
        if (profile == null) return NotFound();

        // this is the check that was missing entirely. [Authorize] on its own
        // meant any logged-in passenger could PUT /api/passengerprofiles/7
        // and rewrite a stranger's passport number
        if (!CanAccess(profile.UserId)) return Forbid();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (dto.DateOfBirth > today || dto.DateOfBirth < today.AddYears(-120))
            return BadRequest("Date of birth is not valid.");

        profile.PassportNumber = dto.PassportNumber.ToUpper();
        profile.Nationality = dto.Nationality;
        profile.DateOfBirth = dto.DateOfBirth;
        profile.PhoneNumber = dto.PhoneNumber;
        await _context.SaveChangesAsync();

        return Ok(await GetDetailAsync(id));
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/phone")]
    [Authorize]
    public async Task<IActionResult> UpdatePhone(int id, [FromBody] PhoneUpdateDto dto)
    {
        var profile = await _context.PassengerProfiles.FindAsync(id);
        if (profile == null) return NotFound();
        if (!CanAccess(profile.UserId)) return Forbid();

        profile.PhoneNumber = dto.PhoneNumber;
        await _context.SaveChangesAsync();

        return Ok(await GetDetailAsync(id));
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var profile = await _context.PassengerProfiles.FindAsync(id);
        if (profile == null) return NotFound();

        // no FK guard needed - nothing points AT PassengerProfile. it's the
        // dependent side of the 1:1, so it deletes cleanly.
        // compare to UsersController.Delete, where Booking.UserId is Restrict
        // and we have to check first
        _context.PassengerProfiles.Remove(profile);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entity) ============
    [HttpGet]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetAll()
    {
        // the original was AllowAnonymous AND returned full entities with
        // Include(p => p.User). that meant anyone could pull every passport
        // number, date of birth, phone number, email and password hash in
        // the system with one unauthenticated GET.
        // now: staff-only, and the SUMMARY shape - masked passport, no DOB.
        //
        // OrderBy moved INSIDE the ToSummary call. it was .OrderBy(p =>
        // p.UserName) on the result, which compiles and throws at runtime -
        // and note the entity path is p.User.Name, not p.UserName. new fix
        return Ok(await ToSummary(
            _context.PassengerProfiles.OrderBy(p => p.User.Name))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        var profile = await _context.PassengerProfiles.FindAsync(id);
        if (profile == null) return NotFound();

        // the shape depends on WHO is asking - owner and admin get the full
        // record, staff get the redacted one. same endpoint, two responses
        if (CanAccess(profile.UserId))
            return Ok(await GetDetailAsync(id));

        if (User.IsInRole("Staff"))
            return Ok(await ToSummary(_context.PassengerProfiles.Where(p => p.Id == id))
                .FirstAsync());

        return Forbid();
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Filter(
        [FromQuery] string? nationality,
        [FromQuery] string? passportNumber,
        [FromQuery] int? minAge,
        [FromQuery] int? maxAge)
    {
        var query = _context.PassengerProfiles.AsQueryable();

        if (!string.IsNullOrWhiteSpace(nationality))
            query = query.Where(p => p.Nationality.Contains(nationality));

        // == not Contains, and admin-only. the original used Contains on an
        // anonymous endpoint, which let anyone enumerate passport numbers
        // by trying one prefix at a time
        if (!string.IsNullOrWhiteSpace(passportNumber))
        {
            if (!User.IsInRole("Admin")) return Forbid();
            query = query.Where(p => p.PassportNumber == passportNumber.ToUpper());
        }

        // age filtering done on DateOfBirth, NOT on p.Age. p.Age is
        // [NotMapped] so there's no column to translate and EF Core throws
        // at runtime. we convert the age bound into a date bound instead -
        // same answer, and sql can actually run it
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (minAge.HasValue)
            query = query.Where(p => p.DateOfBirth <= today.AddYears(-minAge.Value));

        if (maxAge.HasValue)
            query = query.Where(p => p.DateOfBirth >= today.AddYears(-maxAge.Value - 1));

        // same fix as GetAll - sort the entities on User.Name, then project
        return Ok(await ToSummary(query.OrderBy(p => p.User.Name)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byNationality = await _context.PassengerProfiles
            .GroupBy(p => p.Nationality)
            .Select(g => new
            {
                Nationality = g.Key,
                Count = g.Count(),
                // reaching through the 1:1 into User and then into Bookings -
                // two relationships deep, inside an aggregate
                TotalBookings = g.Sum(p => p.User.Bookings.Count),
                AverageAge = Math.Round(g.Average(p => today.Year - p.DateOfBirth.Year), 1)
            })
            .OrderByDescending(s => s.Count)
            .ToListAsync();

        // age bands computed from DateOfBirth for the same reason as above -
        // Age doesn't exist in SQL
        var ageBands = await _context.PassengerProfiles
            .GroupBy(p =>
                p.DateOfBirth > today.AddYears(-18) ? "Under 18"
                : p.DateOfBirth > today.AddYears(-35) ? "18-34"
                : p.DateOfBirth > today.AddYears(-60) ? "35-59"
                : "60+")
            .Select(g => new { AgeBand = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();

        return Ok(new { ByNationality = byNationality, ByAgeBand = ageBands });
    }


    // one place that builds the full detail shape - beats repeating the
    // same Select in three endpoints
    private async Task<ProfileDetailDto?> GetDetailAsync(int id)
    {
        return await _context.PassengerProfiles
            .Where(p => p.Id == id)
            .Select(p => new ProfileDetailDto(
                p.Id, p.UserId, p.User.Name, p.User.Email,
                p.PassportNumber, p.Nationality, p.DateOfBirth,
                DateTime.UtcNow.Year - p.DateOfBirth.Year,
                p.PhoneNumber))
            .FirstOrDefaultAsync();
    }
}