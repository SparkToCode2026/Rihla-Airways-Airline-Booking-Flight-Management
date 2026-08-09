using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CrewsController : ControllerBase
{
    private readonly AppDbContext _context;

    public CrewsController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    public record CrewCreateDto(
        [Required][MaxLength(100)] string Name,
        [Required][MaxLength(50)] string Role,
        [Required][MaxLength(50)] string LicenseNumber,
        [Required][MaxLength(50)] string PassportNumber,
        [Required][MaxLength(100)] string Nationality,
        [Required] DateOnly DateOfBirth);

    public record CrewUpdateDto(
        [Required][MaxLength(100)] string Name,
        [Required][MaxLength(50)] string Role,
        [Required][MaxLength(50)] string LicenseNumber,
        [Required][MaxLength(50)] string PassportNumber,
        [Required][MaxLength(100)] string Nationality,
        [Required] DateOnly DateOfBirth);

    public record RoleUpdateDto([Required][MaxLength(50)] string Role);

    // --- output DTOs ---
    // two shapes, same reasoning as PassengerProfilesController: this table
    // holds employee passport numbers and dates of birth. a rostering
    // screen needs a name and a licence, not an identity document

    // full - Admin only
    public record CrewDetailDto(
        int Id, string Name, string Role, string LicenseNumber,
        string PassportNumber, string Nationality, DateOnly DateOfBirth,
        int Age, int TotalAssignments, int UpcomingFlights);

    // redacted - for Staff building rosters
    public record CrewSummaryDto(
        int Id, string Name, string Role, string LicenseNumber,
        string Nationality, int TotalAssignments, int UpcomingFlights);

    // note there's no DateOfBirth and no passport here at all - not masked,
    // simply absent. a field you don't project can't leak.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // so EF Core can't map .Name back to a column through the constructor.
    // sort the entities first
    private static readonly Func<IQueryable<Crew>, IQueryable<CrewSummaryDto>> ToSummary =
        q => q.Select(c => new CrewSummaryDto(
            c.Id, c.Name, c.Role, c.LicenseNumber, c.Nationality,
            c.FlightCrews.Count,
            c.FlightCrews.Count(fc => fc.Flight.DepartureTime > DateTime.UtcNow)));

    // careful - this is Crew.Role, the PERMANENT job title. it is not
    // FlightCrew.DutyRole, which is what they do on one specific flight.
    // a Pilot (job title) can be rostered as FirstOfficer (duty role)
    private static readonly string[] ValidRoles =
        { "Pilot", "CoPilot", "Attendant", "Engineer" };


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize(Roles = "Admin")]   // creating employee records is not a
                                   // staff-level operation
    public async Task<IActionResult> Create([FromBody] CrewCreateDto dto)
    {
        if (!ValidRoles.Contains(dto.Role))
            return BadRequest($"Role must be one of: {string.Join(", ", ValidRoles)}");

        // uppercase FIRST, then check. the old version compared the raw dto
        // value against the table but stored the uppercased one, so posting
        // "om-p-1001" would pass this check and then collide with the
        // existing "OM-P-1001" at the unique index. new fix
        var licence = dto.LicenseNumber.ToUpper();

        // Crews.LicenseNumber is a unique index - a duplicate throws
        // DbUpdateException instead of saying what's wrong
        if (await _context.Crews.AnyAsync(c => c.LicenseNumber == licence))
            return Conflict($"Licence {licence} is already registered.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (dto.DateOfBirth > today)
            return BadRequest("Date of birth cannot be in the future.");

        // a minimum age check that a passenger profile doesn't need - crew
        // are employees, and 18 is the floor for any commercial licence
        if (dto.DateOfBirth > today.AddYears(-18))
            return BadRequest("Crew members must be at least 18 years old.");

        if (dto.DateOfBirth < today.AddYears(-100))
            return BadRequest("Date of birth is not valid.");

        var crew = new Crew
        {
            Name = dto.Name,
            Role = dto.Role,
            LicenseNumber = licence,
            PassportNumber = dto.PassportNumber.ToUpper(),
            Nationality = dto.Nationality,
            DateOfBirth = dto.DateOfBirth
        };

        _context.Crews.Add(crew);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = crew.Id },
            new CrewDetailDto(crew.Id, crew.Name, crew.Role, crew.LicenseNumber,
                crew.PassportNumber, crew.Nationality, crew.DateOfBirth,
                today.Year - crew.DateOfBirth.Year, 0, 0));
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] CrewUpdateDto dto)
    {
        var crew = await _context.Crews.FindAsync(id);
        if (crew == null) return NotFound();

        if (!ValidRoles.Contains(dto.Role))
            return BadRequest($"Role must be one of: {string.Join(", ", ValidRoles)}");

        var licence = dto.LicenseNumber.ToUpper();
        // "c.Id != id" so re-saving an unchanged licence doesn't conflict
        // with itself
        if (await _context.Crews.AnyAsync(c => c.LicenseNumber == licence && c.Id != id))
            return Conflict($"Licence {licence} belongs to another crew member.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (dto.DateOfBirth > today.AddYears(-18) || dto.DateOfBirth < today.AddYears(-100))
            return BadRequest("Date of birth is not valid.");

        // demoting a pilot who is currently rostered in a cockpit seat would
        // leave a scheduled flight with an unqualified Captain. FlightCrews
        // checks Crew.Role at assignment time, so a later change here can
        // quietly invalidate a roster that was valid when it was made
        if (crew.Role != dto.Role && !ValidPilotRoles(dto.Role))
        {
            var cockpit = await _context.FlightCrews
                .Where(fc => fc.CrewId == id
                          && (fc.DutyRole == "Captain" || fc.DutyRole == "FirstOfficer")
                          && fc.Flight.DepartureTime > DateTime.UtcNow
                          && fc.Flight.Status != "Cancelled")
                .Select(fc => fc.Flight.FlightNumber)
                .ToListAsync();

            if (cockpit.Count > 0)
                return Conflict($"Cannot change role — still rostered in a cockpit seat on: {string.Join(", ", cockpit)}");
        }

        crew.Name = dto.Name;
        crew.Role = dto.Role;
        crew.LicenseNumber = licence;
        crew.PassportNumber = dto.PassportNumber.ToUpper();
        crew.Nationality = dto.Nationality;
        crew.DateOfBirth = dto.DateOfBirth;
        await _context.SaveChangesAsync();

        return Ok(await GetDetailAsync(id));
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/role")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] RoleUpdateDto dto)
    {
        var crew = await _context.Crews.FindAsync(id);
        if (crew == null) return NotFound();

        if (!ValidRoles.Contains(dto.Role))
            return BadRequest($"Role must be one of: {string.Join(", ", ValidRoles)}");

        // same cockpit guard as the PUT - a role change is a role change
        // whichever endpoint it arrives through
        if (!ValidPilotRoles(dto.Role))
        {
            var cockpit = await _context.FlightCrews
                .Where(fc => fc.CrewId == id
                          && (fc.DutyRole == "Captain" || fc.DutyRole == "FirstOfficer")
                          && fc.Flight.DepartureTime > DateTime.UtcNow
                          && fc.Flight.Status != "Cancelled")
                .Select(fc => fc.Flight.FlightNumber)
                .ToListAsync();

            if (cockpit.Count > 0)
                return Conflict($"Cannot change role — still rostered in a cockpit seat on: {string.Join(", ", cockpit)}");
        }

        crew.Role = dto.Role;
        await _context.SaveChangesAsync();

        return Ok(await GetDetailAsync(id));
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var crew = await _context.Crews.FindAsync(id);
        if (crew == null) return NotFound();

        // edge case first, and this is the most destructive delete in the
        // project. FlightCrew.CrewId is Restrict, so SaveChanges throws -
        // annoying but SAFE.
        // the danger is someone "fixing" that 500 by flipping the FK to
        // Cascade. then deleting a retired pilot silently erases every
        // roster row showing who operated which flight - the exact record
        // an aviation authority asks for. guard it here, leave the FK alone
        var assignments = await _context.FlightCrews.CountAsync(fc => fc.CrewId == id);
        if (assignments > 0)
            return Conflict(
                $"Cannot delete {crew.Name} — {AssignmentText(assignments)} on record. " +
                "Crew assignment history is permanent; mark them inactive instead.");

        _context.Crews.Remove(crew);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [Authorize(Roles = "Admin,Staff")]   // was AllowAnonymous and returned
                                          // every employee's passport, DOB
                                          // and full flight history
    public async Task<IActionResult> GetAll()
    {
        // OrderBy moved INSIDE the ToSummary call, onto the entities -
        // chained onto the result it compiles and throws at runtime.
        // new fix
        return Ok(await ToSummary(_context.Crews.OrderBy(c => c.Name)).ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetById(int id)
    {
        // shape depends on the caller - Admin sees the passport and DOB,
        // Staff get the roster view. same endpoint, two responses.
        // same approach as PassengerProfilesController
        if (User.IsInRole("Admin"))
        {
            var detail = await GetDetailAsync(id);
            return detail == null ? NotFound() : Ok(detail);
        }

        var summary = await ToSummary(_context.Crews.Where(c => c.Id == id))
            .FirstOrDefaultAsync();
        return summary == null ? NotFound() : Ok(summary);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Filter(
        [FromQuery] string? role,
        [FromQuery] string? nationality,
        [FromQuery] string? name,
        [FromQuery] bool? availableOnly,
        [FromQuery] DateTime? freeOn)
    {
        var query = _context.Crews.AsQueryable();

        // == not Contains - roles are a fixed set, and Contains("Pilot")
        // would also match "CoPilot", which is a different job
        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(c => c.Role == role);

        if (!string.IsNullOrWhiteSpace(nationality))
            query = query.Where(c => c.Nationality.Contains(nationality));

        if (!string.IsNullOrWhiteSpace(name))
            query = query.Where(c => c.Name.Contains(name));

        // nobody rostered on anything upcoming
        if (availableOnly == true)
            query = query.Where(c => !c.FlightCrews.Any(fc =>
                fc.Flight.DepartureTime > DateTime.UtcNow &&
                fc.Flight.Status != "Cancelled"));

        // "who is free on this date" - the query a rostering manager runs
        // before assigning anyone. negation across a relationship, and EF
        // Core still turns it into one NOT EXISTS
        if (freeOn.HasValue)
        {
            var dayStart = freeOn.Value.Date;
            var dayEnd = dayStart.AddDays(1);
            query = query.Where(c => !c.FlightCrews.Any(fc =>
                fc.Flight.DepartureTime < dayEnd &&
                fc.Flight.ArrivalTime > dayStart &&
                fc.Flight.Status != "Cancelled"));
        }

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToSummary(query.OrderBy(c => c.Name)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byRole = await _context.Crews
            .GroupBy(c => c.Role)
            .Select(g => new
            {
                Role = g.Key,
                Count = g.Count(),
                TotalAssignments = g.Sum(c => c.FlightCrews.Count),
                // Average throws on an empty set, Sum and Count don't -
                // but the group itself is never empty here, so it's safe.
                // the guard matters when averaging a NAVIGATION collection
                AverageAssignments = Math.Round(g.Average(c => c.FlightCrews.Count), 1)
            })
            .OrderByDescending(s => s.Count)
            .ToListAsync();

        var byNationality = await _context.Crews
            .GroupBy(c => c.Nationality)
            .Select(g => new { Nationality = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync();

        // busiest crew, by minutes scheduled rather than flight count -
        // ten short hops is not the same workload as three long hauls
        var workload = await _context.Crews
            .Where(c => c.FlightCrews.Any())
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Role,
                FlightsAssigned = c.FlightCrews.Count,
                MinutesScheduled = c.FlightCrews.Sum(fc =>
                    EF.Functions.DateDiffMinute(fc.Flight.DepartureTime, fc.Flight.ArrivalTime)),
                Upcoming = c.FlightCrews.Count(fc => fc.Flight.DepartureTime > DateTime.UtcNow)
            })
            .OrderByDescending(s => s.MinutesScheduled)
            .Take(10)
            .ToListAsync();

        return Ok(new { ByRole = byRole, ByNationality = byNationality, BusiestCrew = workload });
    }


    private static bool ValidPilotRoles(string role) => role is "Pilot" or "CoPilot";

    // renamed to PascalCase - it's a method, and lowercase private methods
    // read like fields at a glance
    private static string AssignmentText(int n) =>
        n == 1 ? "1 flight assignment" : $"{n} flight assignments";

    private async Task<CrewDetailDto?> GetDetailAsync(int id)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await _context.Crews
            .Where(c => c.Id == id)
            .Select(c => new CrewDetailDto(
                c.Id, c.Name, c.Role, c.LicenseNumber,
                c.PassportNumber, c.Nationality, c.DateOfBirth,
                today.Year - c.DateOfBirth.Year,
                c.FlightCrews.Count,
                c.FlightCrews.Count(fc => fc.Flight.DepartureTime > DateTime.UtcNow)))
            .FirstOrDefaultAsync();
    }
}