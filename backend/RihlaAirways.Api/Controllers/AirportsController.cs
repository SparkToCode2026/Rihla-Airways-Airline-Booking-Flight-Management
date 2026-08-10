using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AirportsController : ControllerBase
{
    private readonly AppDbContext _context;

    public AirportsController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // StringLength(3,3) not MaxLength(3) - IATA codes are exactly three
    // characters, never two. the column is nvarchar(3) so a 4-char code
    // throws a truncation error instead of a message anyone can act on
    public record AirportCreateDto(
        [Required][StringLength(3, MinimumLength = 3)] string Code,
        [Required][MaxLength(150)] string Name,
        [Required][MaxLength(100)] string City,
        [Required][MaxLength(100)] string Country);

    public record AirportUpdateDto(
        [Required][StringLength(3, MinimumLength = 3)] string Code,
        [Required][MaxLength(150)] string Name,
        [Required][MaxLength(100)] string City,
        [Required][MaxLength(100)] string Country);

    public record CityUpdateDto([Required][MaxLength(100)] string City);

    // --- output DTO ---
    // the Includes were loading full Route rows just to render an airport
    // list. two counts is all the list view needs - and it's the only
    // place in the project where BOTH sides of the double-FK show up
    // as separate numbers, which is a nice thing to point at in the demo.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // so EF Core can't map .Country back to a column through the
    // constructor. THIS is the one that threw the "could not be translated"
    // error on GET /api/airports. sort the entities first
    public record AirportResponseDto(
        int Id, string Code, string Name, string City, string Country,
        int DepartingRouteCount, int ArrivingRouteCount, int UpcomingDepartures);

    private static readonly Func<IQueryable<Airport>, IQueryable<AirportResponseDto>> ToDto =
        q => q.Select(a => new AirportResponseDto(
            a.Id, a.Code, a.Name, a.City, a.Country,
            // these two resolve independently ONLY because [InverseProperty]
            // in Route.cs told EF Core which FK feeds which collection.
            // without it these would be identical or the model wouldn't build
            a.DepartingRoutes.Count,
            a.ArrivingRoutes.Count,
            // three hops: Airport -> DepartingRoutes -> Flights
            a.DepartingRoutes.Sum(r => r.Flights.Count(f =>
                f.DepartureTime > DateTime.UtcNow && f.Status != "Cancelled"))));


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize(Roles = "Admin")]   // the route network is reference data,
                                   // not something a passenger edits
    public async Task<IActionResult> Create([FromBody] AirportCreateDto dto)
    {
        var code = dto.Code.ToUpperInvariant();

        // format check BEFORE the database round trip - no point querying
        // for "1A!" when we're going to reject it anyway. these two were
        // the other way round, which worked but wasted a query. new fix
        if (!code.All(char.IsLetter))
            return BadRequest("IATA code must be three letters.");

        // Airports.Code is a unique index - a duplicate throws
        // DbUpdateException instead of explaining itself.
        // the ToUpperInvariant above matters here: without it "mct" and
        // "MCT" would both pass this check on a case-sensitive comparison
        // and then collide at the database
        if (await _context.Airports.AnyAsync(a => a.Code == code))
            return Conflict($"Airport code {code} is already registered.");

        var airport = new Airport
        {
            Code = code,
            Name = dto.Name,
            City = dto.City,
            Country = dto.Country
        };

        _context.Airports.Add(airport);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = airport.Id },
            new AirportResponseDto(airport.Id, airport.Code, airport.Name,
                airport.City, airport.Country, 0, 0, 0));
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] AirportUpdateDto dto)
    {
        var airport = await _context.Airports.FindAsync(id);
        if (airport == null) return NotFound();

        var code = dto.Code.ToUpperInvariant();

        if (!code.All(char.IsLetter))
            return BadRequest("IATA code must be three letters.");

        // "a.Id != id" so re-saving an unchanged code doesn't conflict
        // with itself
        if (await _context.Airports.AnyAsync(a => a.Code == code && a.Id != id))
            return Conflict($"Airport code {code} belongs to another airport.");

        airport.Code = code;
        airport.Name = dto.Name;
        airport.City = dto.City;
        airport.Country = dto.Country;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Airports.Where(a => a.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/city")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateCity(int id, [FromBody] CityUpdateDto dto)
    {
        var airport = await _context.Airports.FindAsync(id);
        if (airport == null) return NotFound();

        airport.City = dto.City;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Airports.Where(a => a.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var airport = await _context.Airports.FindAsync(id);
        if (airport == null) return NotFound();

        // edge case first, and this is the ONE place the double-FK design
        // changes how a guard is written. Route points at Airport TWICE, and
        // BOTH FKs are Restrict - which is exactly why the migration didn't
        // blow up on multiple cascade paths.
        // so checking DepartingRoutes alone isn't enough: an airport that
        // nothing departs from but everything arrives at would pass the
        // check and then throw at SaveChanges. the || is load-bearing
        var departing = await _context.Routes.CountAsync(r => r.OriginAirportId == id);
        var arriving = await _context.Routes.CountAsync(r => r.DestinationAirportId == id);

        if (departing > 0 || arriving > 0)
            return Conflict(
                $"Cannot delete {airport.Code} — it is used by {departing} departing " +
                $"and {arriving} arriving route(s). Remove those routes first.");

        _context.Airports.Remove(airport);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [AllowAnonymous]   // stays anonymous, and correctly so - unlike the
                       // passenger and crew controllers, airport data is
                       // genuinely public. the DTO returns counts, not rows
    public async Task<IActionResult> GetAll()
    {
        // OrderBy moved INSIDE the ToDto call, onto the entities. this exact
        // line was the first "could not be translated" failure we hit -
        // ToDto(...).OrderBy(a => a.Country) compiles fine and then dies at
        // runtime because EF Core can't see Country through the record
        // constructor. new fix
        return Ok(await ToDto(
            _context.Airports
                .OrderBy(a => a.Country)
                .ThenBy(a => a.City))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var airport = await ToDto(_context.Airports.Where(a => a.Id == id))
            .FirstOrDefaultAsync();
        return airport == null ? NotFound() : Ok(airport);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter(
        [FromQuery] string? country,
        [FromQuery] string? city,
        [FromQuery] string? code,
        [FromQuery] string? search,
        [FromQuery] bool? connectedOnly)
    {
        var query = _context.Airports.AsQueryable();

        if (!string.IsNullOrWhiteSpace(country))
            query = query.Where(a => a.Country.Contains(country));

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(a => a.City.Contains(city));

        // == not Contains - a code is exact, and Contains("MC") matching MCT
        // is not a search anyone wants
        if (!string.IsNullOrWhiteSpace(code))
            query = query.Where(a => a.Code == code.ToUpperInvariant());

        // one box that hits all four fields - what an airport picker in the
        // frontend actually needs
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a =>
                a.Code.Contains(search) || a.Name.Contains(search) ||
                a.City.Contains(search) || a.Country.Contains(search));

        // airports that are actually on the network. checks BOTH collections
        // for the same reason Delete does
        if (connectedOnly == true)
            query = query.Where(a => a.DepartingRoutes.Any() || a.ArrivingRoutes.Any());

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderBy(a => a.Code)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byCountry = await _context.Airports
            .GroupBy(a => a.Country)
            .Select(g => new
            {
                Country = g.Key,
                AirportCount = g.Count(),
                TotalDepartingRoutes = g.Sum(a => a.DepartingRoutes.Count),
                TotalArrivingRoutes = g.Sum(a => a.ArrivingRoutes.Count)
            })
            .OrderByDescending(s => s.AirportCount)
            .ToListAsync();

        // busiest hubs by flights through them, counting BOTH directions -
        // an airport is busy whether you're leaving it or landing at it.
        // this is the aggregate that only makes sense because of the
        // two-FK design
        var busiest = await _context.Airports
            .Select(a => new
            {
                a.Id,
                a.Code,
                a.City,
                a.Country,
                DepartingFlights = a.DepartingRoutes.Sum(r => r.Flights.Count),
                ArrivingFlights = a.ArrivingRoutes.Sum(r => r.Flights.Count),
                TotalRoutes = a.DepartingRoutes.Count + a.ArrivingRoutes.Count
            })
            .OrderByDescending(s => s.DepartingFlights + s.ArrivingFlights)
            .Take(10)
            .ToListAsync();

        // airports with no routes at all - reference data that got entered
        // and then forgotten. filtering on the ABSENCE of related rows
        var unused = await _context.Airports
            .Where(a => !a.DepartingRoutes.Any() && !a.ArrivingRoutes.Any())
            .Select(a => new { a.Id, a.Code, a.City, a.Country })
            .OrderBy(a => a.Code)
            .ToListAsync();

        return Ok(new
        {
            TotalAirports = await _context.Airports.CountAsync(),
            ByCountry = byCountry,
            BusiestHubs = busiest,
            UnusedAirports = unused
        });
    }
}