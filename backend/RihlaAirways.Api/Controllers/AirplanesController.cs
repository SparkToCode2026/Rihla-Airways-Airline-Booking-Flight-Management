using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AirplanesController : ControllerBase
{
    private readonly AppDbContext _context;

    public AirplanesController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // Range on ManufactureYear because it's a plain int with no db
    // constraint - nothing stops year 20255 otherwise
    public record AirplaneCreateDto(
        [Required][MaxLength(100)] string Model,
        [Required][MaxLength(20)] string RegistrationNumber,
        [Required][Range(1, 900)] int Capacity,
        [Required][Range(1950, 2100)] int ManufactureYear);

    public record AirplaneUpdateDto(
        [Required][MaxLength(100)] string Model,
        [Required][MaxLength(20)] string RegistrationNumber,
        [Required][Range(1, 900)] int Capacity,
        [Required][Range(1950, 2100)] int ManufactureYear);

    public record CapacityUpdateDto([Required][Range(1, 900)] int Capacity);

    // --- output DTO ---
    // Include(a => a.Flights) returned every flight row nested in every
    // airplane. counts and a utilisation figure are what a fleet list needs.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // so EF Core can't map .Model back to a column through the constructor.
    // sort the entities first
    public record AirplaneResponseDto(
        int Id, string Model, string RegistrationNumber,
        int Capacity, int ManufactureYear, int AgeYears,
        int TotalFlights, int UpcomingFlights, int SeatsSoldAllTime);

    private static readonly Func<IQueryable<Airplane>, IQueryable<AirplaneResponseDto>> ToDto =
        q => q.Select(a => new AirplaneResponseDto(
            a.Id, a.Model, a.RegistrationNumber, a.Capacity, a.ManufactureYear,
            DateTime.UtcNow.Year - a.ManufactureYear,
            a.Flights.Count,
            a.Flights.Count(f => f.DepartureTime > DateTime.UtcNow && f.Status != "Cancelled"),
            // two hops - Airplane -> Flights -> Tickets
            a.Flights.Sum(f => f.Tickets.Count)));


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize(Roles = "Admin")]   // fleet records aren't passenger-editable
    public async Task<IActionResult> Create([FromBody] AirplaneCreateDto dto)
    {
        var registration = dto.RegistrationNumber.ToUpperInvariant();

        // Airplanes.RegistrationNumber is a unique index - the tail number
        // is what identifies the individual aircraft. note Model is NOT
        // unique and shouldn't be: many planes share "Boeing 737-800"
        if (await _context.Airplanes.AnyAsync(a => a.RegistrationNumber == registration))
            return Conflict($"Registration {registration} is already in the fleet.");

        // a future manufacture year would make AgeYears negative
        if (dto.ManufactureYear > DateTime.UtcNow.Year)
            return BadRequest("Manufacture year cannot be in the future.");

        var airplane = new Airplane
        {
            Model = dto.Model,
            RegistrationNumber = registration,
            Capacity = dto.Capacity,
            ManufactureYear = dto.ManufactureYear
        };

        _context.Airplanes.Add(airplane);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = airplane.Id },
            new AirplaneResponseDto(airplane.Id, airplane.Model, airplane.RegistrationNumber,
                airplane.Capacity, airplane.ManufactureYear,
                DateTime.UtcNow.Year - airplane.ManufactureYear, 0, 0, 0));
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] AirplaneUpdateDto dto)
    {
        var airplane = await _context.Airplanes.FindAsync(id);
        if (airplane == null) return NotFound();

        var registration = dto.RegistrationNumber.ToUpperInvariant();

        // "a.Id != id" so re-saving an unchanged registration doesn't
        // conflict with itself
        if (await _context.Airplanes.AnyAsync(a =>
                a.RegistrationNumber == registration && a.Id != id))
            return Conflict($"Registration {registration} belongs to another aircraft.");

        if (dto.ManufactureYear > DateTime.UtcNow.Year)
            return BadRequest("Manufacture year cannot be in the future.");

        // same capacity guard as the PATCH below - a full update can lower
        // Capacity just as easily as the dedicated endpoint can
        if (dto.Capacity < airplane.Capacity)
        {
            var problem = await FindOversoldFlightAsync(id, dto.Capacity);
            if (problem != null) return Conflict(problem);
        }

        airplane.Model = dto.Model;
        airplane.RegistrationNumber = registration;
        airplane.Capacity = dto.Capacity;
        airplane.ManufactureYear = dto.ManufactureYear;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Airplanes.Where(a => a.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/capacity")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateCapacity(int id, [FromBody] CapacityUpdateDto dto)
    {
        var airplane = await _context.Airplanes.FindAsync(id);
        if (airplane == null) return NotFound();

        // edge case first, and this is the one that actually matters on
        // this table. Capacity is what TicketsController checks before
        // selling a seat - so lowering it below the tickets already sold on
        // a scheduled flight strands passengers holding valid tickets for
        // seats the aircraft no longer has.
        // no db constraint covers this: Capacity is just an int, and the
        // tickets live two tables away. FlightsController guards the case
        // where you SWAP to a smaller plane, but nothing guarded the plane
        // shrinking underneath an existing flight - new fix
        if (dto.Capacity < airplane.Capacity)
        {
            var problem = await FindOversoldFlightAsync(id, dto.Capacity);
            if (problem != null) return Conflict(problem);
        }

        airplane.Capacity = dto.Capacity;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Airplanes.Where(a => a.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var airplane = await _context.Airplanes.FindAsync(id);
        if (airplane == null) return NotFound();

        // Flight.AirplaneId is Restrict - we set that deliberately so
        // retiring an aircraft can't erase its flight history. so this
        // throws DbUpdateException unless we check first.
        // same shape as the route guard in FlightsController
        var flightCount = await _context.Flights.CountAsync(f => f.AirplaneId == id);
        if (flightCount > 0)
            return Conflict($"Cannot delete {airplane.RegistrationNumber} — " +
                            $"{flightCount} flight(s) reference it. The flight history is permanent.");

        _context.Airplanes.Remove(airplane);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entity) ============
    [HttpGet]
    [Authorize(Roles = "Admin,Staff")]   // fleet composition and utilisation
                                          // is internal operational data
    public async Task<IActionResult> GetAll()
    {
        // OrderBy moved INSIDE the ToDto call, onto the entities - chained
        // onto the result it compiles and throws at runtime. new fix
        return Ok(await ToDto(
            _context.Airplanes
                .OrderBy(a => a.Model)
                .ThenBy(a => a.RegistrationNumber))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetById(int id)
    {
        var airplane = await ToDto(_context.Airplanes.Where(a => a.Id == id))
            .FirstOrDefaultAsync();
        return airplane == null ? NotFound() : Ok(airplane);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Filter(
        [FromQuery] string? model,
        [FromQuery] int? minYear,
        [FromQuery] int? maxYear,
        [FromQuery] int? minCapacity,
        [FromQuery] bool? idleOnly,
        [FromQuery] DateTime? freeOn)
    {
        var query = _context.Airplanes.AsQueryable();

        if (!string.IsNullOrWhiteSpace(model))
            query = query.Where(a => a.Model.Contains(model));

        if (minYear.HasValue)
            query = query.Where(a => a.ManufactureYear >= minYear.Value);

        if (maxYear.HasValue)
            query = query.Where(a => a.ManufactureYear <= maxYear.Value);

        if (minCapacity.HasValue)
            query = query.Where(a => a.Capacity >= minCapacity.Value);

        // nothing scheduled at all
        if (idleOnly == true)
            query = query.Where(a => !a.Flights.Any(f =>
                f.DepartureTime > DateTime.UtcNow && f.Status != "Cancelled"));

        // "which aircraft is free on this date" - the query scheduling runs
        // before creating a flight, and it's the same overlap test
        // FlightsController uses to reject a clash. worth having on both
        // sides: this one finds a plane, that one stops a bad assignment
        if (freeOn.HasValue)
        {
            var dayStart = freeOn.Value.Date;
            var dayEnd = dayStart.AddDays(1);
            query = query.Where(a => !a.Flights.Any(f =>
                f.DepartureTime < dayEnd &&
                f.ArrivalTime > dayStart &&
                f.Status != "Cancelled"));
        }

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderBy(a => a.Model)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byModel = await _context.Airplanes
            .GroupBy(a => a.Model)
            .Select(g => new
            {
                Model = g.Key,
                Count = g.Count(),
                TotalSeats = g.Sum(a => a.Capacity),
                AverageCapacity = Math.Round(g.Average(a => (decimal)a.Capacity), 1),
                AverageAgeYears = Math.Round(
                    g.Average(a => (decimal)(DateTime.UtcNow.Year - a.ManufactureYear)), 1),
                TotalFlights = g.Sum(a => a.Flights.Count)
            })
            .OrderByDescending(s => s.Count)
            .ToListAsync();

        // utilisation per aircraft - hours flown and how full it went.
        // the two numbers a fleet manager actually looks at
        var utilisation = await _context.Airplanes
            .Select(a => new
            {
                a.Id,
                a.RegistrationNumber,
                a.Model,
                a.Capacity,
                FlightCount = a.Flights.Count,
                // DateDiff because plain datetime subtraction doesn't
                // translate to SQL - same trap as elsewhere
                TotalMinutesFlown = a.Flights.Sum(f =>
                    EF.Functions.DateDiffMinute(f.DepartureTime, f.ArrivalTime)),
                SeatsOffered = a.Flights.Count * a.Capacity,
                SeatsSold = a.Flights.Sum(f => f.Tickets.Count)
            })
            .OrderByDescending(s => s.TotalMinutesFlown)
            .ToListAsync();

        // load factor calculated AFTER ToListAsync, in memory - dividing
        // inside the query risks a divide-by-zero in SQL for an aircraft
        // that has never flown. same reasoning as FlightsController stats
        var withLoadFactor = utilisation.Select(u => new
        {
            u.Id, u.RegistrationNumber, u.Model, u.Capacity,
            u.FlightCount, u.TotalMinutesFlown, u.SeatsOffered, u.SeatsSold,
            LoadFactorPercent = u.SeatsOffered > 0
                ? Math.Round((decimal)u.SeatsSold / u.SeatsOffered * 100, 1)
                : 0m
        });

        var hasFleet = await _context.Airplanes.AnyAsync();

        return Ok(new
        {
            FleetSize = await _context.Airplanes.CountAsync(),
            TotalSeats = await _context.Airplanes.SumAsync(a => a.Capacity),
            // Average throws on an empty table, Sum and Count don't
            AverageFleetAge = hasFleet
                ? Math.Round(await _context.Airplanes
                    .AverageAsync(a => (decimal)(DateTime.UtcNow.Year - a.ManufactureYear)), 1)
                : 0m,
            ByModel = byModel,
            Utilisation = withLoadFactor
        });
    }


    // shared by the PUT and the PATCH - both can lower Capacity, so the
    // check lives in one place rather than being written twice and
    // drifting apart
    private async Task<string?> FindOversoldFlightAsync(int airplaneId, int newCapacity)
    {
        var oversold = await _context.Flights
            .Where(f => f.AirplaneId == airplaneId
                     && f.Status != "Cancelled"
                     && f.DepartureTime > DateTime.UtcNow
                     && f.Tickets.Count > newCapacity)
            .Select(f => new { f.FlightNumber, Sold = f.Tickets.Count })
            .FirstOrDefaultAsync();

        return oversold == null
            ? null
            : $"Cannot reduce capacity to {newCapacity} — flight {oversold.FlightNumber} " +
              $"already has {oversold.Sold} seats sold.";
    }
}