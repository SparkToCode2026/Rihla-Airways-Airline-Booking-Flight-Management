using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FlightCrewsController : ControllerBase
{
    private readonly AppDbContext _context;

    public FlightCrewsController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    public record FlightCrewCreateDto(
        [Required] int FlightId,
        [Required] int CrewId,
        [Required][MaxLength(50)] string DutyRole);

    // only DutyRole is updatable. changing FlightId or CrewId isn't an edit,
    // it's a different assignment - and it re-opens every clash check.
    // delete and re-create instead. new fix
    public record FlightCrewUpdateDto([Required][MaxLength(50)] string DutyRole);

    public record DutyRoleUpdateDto([Required][MaxLength(50)] string DutyRole);

    // --- output DTO ---
    // Include(fc => fc.Crew) returned PassportNumber, Nationality and
    // DateOfBirth - the immigration fields we added to Crew - on an
    // anonymous endpoint. a roster needs a name and a licence, nothing more.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // EF Core can't map .DepartureTime back to a column through the
    // constructor. and note the path differs: DepartureTime on the dto is
    // Flight.DepartureTime on the entity
    public record FlightCrewResponseDto(
        int Id,
        int FlightId, string FlightNumber, DateTime DepartureTime,
        string OriginCode, string DestinationCode, string FlightStatus,
        int CrewId, string CrewName, string CrewRole, string LicenseNumber,
        string DutyRole);

    private static readonly Func<IQueryable<FlightCrew>, IQueryable<FlightCrewResponseDto>> ToDto =
        q => q.Select(fc => new FlightCrewResponseDto(
            fc.Id,
            fc.FlightId, fc.Flight.FlightNumber, fc.Flight.DepartureTime,
            fc.Flight.Route.OriginAirport.Code,
            fc.Flight.Route.DestinationAirport.Code,
            fc.Flight.Status,
            fc.CrewId, fc.Crew.Name, fc.Crew.Role, fc.Crew.LicenseNumber,
            fc.DutyRole));

    // DutyRole is the payload that justifies this whole join table existing -
    // it's the reason FlightCrew is an explicit entity and not a hidden
    // many-to-many. so it deserves validation more than most string fields.
    // remember it is NOT the same as Crew.Role: that's the permanent job
    // title, this is what they're doing on THIS flight
    private static readonly string[] ValidDutyRoles =
        { "Captain", "FirstOfficer", "PurserCabinCrew", "CabinCrew", "FlightEngineer" };

    // roles that need an actual pilot licence behind them - we don't roster
    // a cabin attendant as Captain
    private static readonly string[] CockpitDuties = { "Captain", "FirstOfficer" };
    private static readonly string[] PilotJobTitles = { "Pilot", "CoPilot" };


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Create([FromBody] FlightCrewCreateDto dto)
    {
        if (!ValidDutyRoles.Contains(dto.DutyRole))
            return BadRequest($"DutyRole must be one of: {string.Join(", ", ValidDutyRoles)}");

        var flight = await _context.Flights.FindAsync(dto.FlightId);
        if (flight == null) return BadRequest($"Flight {dto.FlightId} not found.");

        var crew = await _context.Crews.FindAsync(dto.CrewId);
        if (crew == null) return BadRequest($"Crew member {dto.CrewId} not found.");

        if (flight.Status is "Departed" or "Landed" or "Cancelled")
            return Conflict($"Cannot change the roster of a {flight.Status.ToLower()} flight.");

        // 1) UX_FlightCrew_Pair - the same person twice on one flight
        if (await _context.FlightCrews.AnyAsync(fc =>
                fc.FlightId == dto.FlightId && fc.CrewId == dto.CrewId))
            return Conflict($"{crew.Name} is already assigned to this flight.");

        // 2) THE important one, and there's no db constraint for it:
        // the same person rostered on two OVERLAPPING flights. the unique
        // index above only catches the same flight twice - it can't see
        // across flights. a pilot on RA204 (08:00-11:00) and RA309
        // (09:00-12:00) is two valid rows that are jointly impossible.
        // same overlap test as the airplane clash in FlightsController:
        // A starts before B ends AND A ends after B starts
        var clash = await _context.FlightCrews
            .Where(fc => fc.CrewId == dto.CrewId
                      && fc.Flight.Status != "Cancelled"
                      && fc.Flight.DepartureTime < flight.ArrivalTime
                      && fc.Flight.ArrivalTime > flight.DepartureTime)
            .Select(fc => fc.Flight.FlightNumber)
            .FirstOrDefaultAsync();

        if (clash != null)
            return Conflict($"{crew.Name} is already rostered on flight {clash} during this window.");

        // 3) cockpit duties need a pilot licence behind them - Crew.Role is
        // the permanent job title, so we check that rather than DutyRole
        if (CockpitDuties.Contains(dto.DutyRole) && !PilotJobTitles.Contains(crew.Role))
            return BadRequest($"{crew.Name} is a {crew.Role} and cannot be assigned as {dto.DutyRole}.");

        // 4) exactly one Captain per flight. not a db constraint because it's
        // a rule about a SET of rows, which a unique index can't express
        if (dto.DutyRole == "Captain" &&
            await _context.FlightCrews.AnyAsync(fc =>
                fc.FlightId == dto.FlightId && fc.DutyRole == "Captain"))
            return Conflict("This flight already has a Captain assigned.");

        var fcNew = new FlightCrew
        {
            FlightId = dto.FlightId,
            CrewId = dto.CrewId,
            DutyRole = dto.DutyRole
        };

        _context.FlightCrews.Add(fcNew);
        await _context.SaveChangesAsync();

        var result = await ToDto(_context.FlightCrews.Where(x => x.Id == fcNew.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetById), new { id = fcNew.Id }, result);
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Update(int id, [FromBody] FlightCrewUpdateDto dto)
    {
        var fc = await _context.FlightCrews
            .Include(x => x.Crew)
            .Include(x => x.Flight)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (fc == null) return NotFound();

        if (!ValidDutyRoles.Contains(dto.DutyRole))
            return BadRequest($"DutyRole must be one of: {string.Join(", ", ValidDutyRoles)}");

        if (fc.Flight.Status is "Departed" or "Landed")
            return Conflict($"Cannot change the roster of a {fc.Flight.Status.ToLower()} flight.");

        if (CockpitDuties.Contains(dto.DutyRole) && !PilotJobTitles.Contains(fc.Crew.Role))
            return BadRequest($"{fc.Crew.Name} is a {fc.Crew.Role} and cannot be assigned as {dto.DutyRole}.");

        // "x.Id != id" so re-saving the same Captain doesn't conflict with itself
        if (dto.DutyRole == "Captain" &&
            await _context.FlightCrews.AnyAsync(x =>
                x.FlightId == fc.FlightId && x.DutyRole == "Captain" && x.Id != id))
            return Conflict("This flight already has a Captain assigned.");

        fc.DutyRole = dto.DutyRole;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.FlightCrews.Where(x => x.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/dutyrole")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> UpdateDutyRole(int id, [FromBody] DutyRoleUpdateDto dto)
    {
        var fc = await _context.FlightCrews
            .Include(x => x.Crew)
            .Include(x => x.Flight)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (fc == null) return NotFound();

        if (!ValidDutyRoles.Contains(dto.DutyRole))
            return BadRequest($"DutyRole must be one of: {string.Join(", ", ValidDutyRoles)}");

        // the PUT blocks roster changes on a departed flight but this PATCH
        // didn't - same rule, two endpoints, one of them was missing it.
        // needs the Include(x => x.Flight) above to read Status. new fix
        if (fc.Flight.Status is "Departed" or "Landed")
            return Conflict($"Cannot change the roster of a {fc.Flight.Status.ToLower()} flight.");

        if (CockpitDuties.Contains(dto.DutyRole) && !PilotJobTitles.Contains(fc.Crew.Role))
            return BadRequest($"{fc.Crew.Name} is a {fc.Crew.Role} and cannot be assigned as {dto.DutyRole}.");

        if (dto.DutyRole == "Captain" &&
            await _context.FlightCrews.AnyAsync(x =>
                x.FlightId == fc.FlightId && x.DutyRole == "Captain" && x.Id != id))
            return Conflict("This flight already has a Captain assigned.");

        fc.DutyRole = dto.DutyRole;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.FlightCrews.Where(x => x.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Delete(int id)
    {
        var fc = await _context.FlightCrews
            .Include(x => x.Flight)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (fc == null) return NotFound();

        // no FK guard needed - nothing points AT FlightCrew, it's a leaf.
        // compare to FlightsController.Delete where Ticket.FlightId is
        // Restrict and we have to check before removing
        if (fc.Flight.Status is "Departed" or "Landed")
            return Conflict("Cannot remove crew from a flight that has already operated. " +
                            "This is the historical record of who worked it.");

        _context.FlightCrews.Remove(fc);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [Authorize(Roles = "Admin,Staff")]   // was AllowAnonymous and returned
                                          // crew passport numbers and DOBs
    public async Task<IActionResult> GetAll()
    {
        // OrderBy moved INSIDE the ToDto call, and the path changes with it:
        // fc.DepartureTime on the dto is fc.Flight.DepartureTime on the
        // entity. chained onto the result it compiles and throws at
        // runtime - new fix
        return Ok(await ToDto(
            _context.FlightCrews
                .OrderBy(fc => fc.Flight.DepartureTime)
                .ThenBy(fc => fc.DutyRole))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetById(int id)
    {
        var fc = await ToDto(_context.FlightCrews.Where(x => x.Id == id))
            .FirstOrDefaultAsync();
        return fc == null ? NotFound() : Ok(fc);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Filter(
        [FromQuery] int? flightId,
        [FromQuery] int? crewId,
        [FromQuery] string? dutyRole,
        [FromQuery] string? crewName,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        var query = _context.FlightCrews.AsQueryable();

        if (flightId.HasValue)
            query = query.Where(fc => fc.FlightId == flightId.Value);

        if (crewId.HasValue)
            query = query.Where(fc => fc.CrewId == crewId.Value);

        // == not Contains - the duty roles are a fixed set now
        if (!string.IsNullOrWhiteSpace(dutyRole))
            query = query.Where(fc => fc.DutyRole == dutyRole);

        if (!string.IsNullOrWhiteSpace(crewName))
            query = query.Where(fc => fc.Crew.Name.Contains(crewName));

        // this is the query the roster page actually runs: "what is this
        // crew member working next week". the date lives on Flight, two
        // tables away from where we're filtering
        if (fromDate.HasValue)
            query = query.Where(fc => fc.Flight.DepartureTime >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(fc => fc.Flight.DepartureTime <= toDate.Value);

        // same fix as GetAll - sort the entities on Flight.DepartureTime,
        // then project
        return Ok(await ToDto(query.OrderBy(fc => fc.Flight.DepartureTime)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byDutyRole = await _context.FlightCrews
            .GroupBy(fc => fc.DutyRole)
            .Select(g => new
            {
                DutyRole = g.Key,
                AssignmentCount = g.Count(),
                // how many DIFFERENT people fill this duty - a count of
                // assignments alone hides the fact that one person might
                // be covering all of them
                DistinctCrewMembers = g.Select(fc => fc.CrewId).Distinct().Count()
            })
            .OrderByDescending(s => s.AssignmentCount)
            .ToListAsync();

        // workload per person - the number a rostering manager cares about,
        // because it's how you spot someone being overworked
        var byCrewMember = await _context.FlightCrews
            .GroupBy(fc => new { fc.CrewId, fc.Crew.Name, fc.Crew.Role })
            .Select(g => new
            {
                g.Key.CrewId,
                g.Key.Name,
                JobTitle = g.Key.Role,
                FlightsAssigned = g.Count(),
                // total minutes in the air, summed across flights. DateDiff
                // because plain datetime subtraction doesn't translate to SQL
                TotalMinutesScheduled = g.Sum(fc =>
                    EF.Functions.DateDiffMinute(fc.Flight.DepartureTime, fc.Flight.ArrivalTime)),
                UpcomingFlights = g.Count(fc => fc.Flight.DepartureTime > DateTime.UtcNow)
            })
            .OrderByDescending(s => s.TotalMinutesScheduled)
            .ToListAsync();

        // flights that would depart with nobody in the left seat.
        // this is the report that matters most operationally
        var understaffed = await _context.Flights
            .Where(f => f.Status == "Scheduled" && f.DepartureTime > DateTime.UtcNow)
            .Where(f => !f.FlightCrews.Any(fc => fc.DutyRole == "Captain")
                     || f.FlightCrews.Count < 2)
            .Select(f => new
            {
                f.Id,
                f.FlightNumber,
                f.DepartureTime,
                CrewAssigned = f.FlightCrews.Count,
                HasCaptain = f.FlightCrews.Any(fc => fc.DutyRole == "Captain")
            })
            .OrderBy(f => f.DepartureTime)
            .ToListAsync();

        return Ok(new
        {
            ByDutyRole = byDutyRole,
            ByCrewMember = byCrewMember,
            UnderstaffedFlights = understaffed
        });
    }
}