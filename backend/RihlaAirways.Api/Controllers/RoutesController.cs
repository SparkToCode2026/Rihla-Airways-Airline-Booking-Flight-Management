using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using Route = RihlaAirways.Api.Models.Route; //  THIS IS THE ALIAS – DO NOT REMOVE

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RoutesController : ControllerBase
{
    private readonly AppDbContext _context;

    public RoutesController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // Range on distance and duration because both are plain ints with no
    // db constraint - nothing stops a -500 km route otherwise
    public record RouteCreateDto(
        [Required] int OriginAirportId,
        [Required] int DestinationAirportId,
        [Required][Range(1, 20000)] int DistanceKm,
        [Required][Range(1, 1440)] int EstimatedDurationMin);

    public record RouteUpdateDto(
        [Required] int OriginAirportId,
        [Required] int DestinationAirportId,
        [Required][Range(1, 20000)] int DistanceKm,
        [Required][Range(1, 1440)] int EstimatedDurationMin);

    public record DurationUpdateDto([Required][Range(1, 1440)] int EstimatedDurationMin);

    // --- output DTO ---
    // Include(r => r.Flights) was returning every flight row nested inside
    // each route, on an anonymous endpoint. we only need the count, plus
    // the airport codes flattened so nobody has to dig into a nested object
    public record RouteResponseDto(
        int Id,
        int OriginAirportId, string OriginCode, string OriginCity,
        int DestinationAirportId, string DestinationCode, string DestinationCity,
        int DistanceKm, int EstimatedDurationMin, int FlightCount);

    // this is where the two-FKs-to-one-table design finally pays off -
    // OriginAirport and DestinationAirport resolve independently because
    // [InverseProperty] in Route.cs told EF Core which is which.
    //
    // never chain .OrderBy() onto the RESULT of this - it's a positional
    // record, so EF Core can't map .OriginCode back to a column once the
    // values are inside a constructor call. sort the ENTITIES first, and
    // note the property paths differ: the dto has OriginCode, the entity
    // has OriginAirport.Code
    private static readonly Func<IQueryable<Route>, IQueryable<RouteResponseDto>> ToDto =
        q => q.Select(r => new RouteResponseDto(
            r.Id,
            r.OriginAirportId, r.OriginAirport.Code, r.OriginAirport.City,
            r.DestinationAirportId, r.DestinationAirport.Code, r.DestinationAirport.City,
            r.DistanceKm, r.EstimatedDurationMin, r.Flights.Count));


    // ============ 1. POST — create ============
    [HttpPost]
    // Admin only. opening a new city pair is network/commercial planning, not a
    // check-in desk action - and RouteId is the anchor for every Flight, fare
    // and ticket price hanging off it. Staff keep full read access + stats
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] RouteCreateDto dto)
    {
        // edge case first, and this one has no db constraint behind it:
        // origin and destination are both valid airport ids on their own,
        // so nothing at the database level stops a route from MCT to MCT.
        // has to be checked here
        if (dto.OriginAirportId == dto.DestinationAirportId)
            return BadRequest("Origin and destination airports must be different.");

        // both FKs checked - a bad id throws DbUpdateException otherwise.
        // two chances to hit it on this table
        var origin = await _context.Airports.FindAsync(dto.OriginAirportId);
        if (origin == null) return BadRequest($"Origin airport {dto.OriginAirportId} not found.");

        var destination = await _context.Airports.FindAsync(dto.DestinationAirportId);
        if (destination == null) return BadRequest($"Destination airport {dto.DestinationAirportId} not found.");

        // UX_Route_Pair is unique on (Origin, Destination). this one is easy
        // to trip in practice - MCT to DXB is exactly the kind of thing
        // someone enters twice
        if (await _context.Routes.AnyAsync(r =>
                r.OriginAirportId == dto.OriginAirportId &&
                r.DestinationAirportId == dto.DestinationAirportId))
            return Conflict($"A route from {origin.Code} to {destination.Code} already exists.");

        var route = new Route
        {
            OriginAirportId = dto.OriginAirportId,
            DestinationAirportId = dto.DestinationAirportId,
            DistanceKm = dto.DistanceKm,
            EstimatedDurationMin = dto.EstimatedDurationMin
        };

        _context.Routes.Add(route);
        await _context.SaveChangesAsync();

        var result = await ToDto(_context.Routes.Where(r => r.Id == route.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetById), new { id = route.Id }, result);
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] RouteUpdateDto dto)
    {
        var route = await _context.Routes.FindAsync(id);
        if (route == null) return NotFound();

        if (dto.OriginAirportId == dto.DestinationAirportId)
            return BadRequest("Origin and destination airports must be different.");

        if (!await _context.Airports.AnyAsync(a => a.Id == dto.OriginAirportId))
            return BadRequest($"Origin airport {dto.OriginAirportId} not found.");

        if (!await _context.Airports.AnyAsync(a => a.Id == dto.DestinationAirportId))
            return BadRequest($"Destination airport {dto.DestinationAirportId} not found.");

        // "r.Id != id" again - without it, saving a route with the same
        // endpoints conflicts with itself
        if (await _context.Routes.AnyAsync(r =>
                r.OriginAirportId == dto.OriginAirportId &&
                r.DestinationAirportId == dto.DestinationAirportId &&
                r.Id != id))
            return Conflict("Another route already connects those two airports.");

        // worth knowing: changing the endpoints of a route silently changes
        // where every existing flight on it goes. a stricter version would
        // block this once Flights exist - leaving it open for now since
        // the demo needs editable data, but flagging it
        route.OriginAirportId = dto.OriginAirportId;
        route.DestinationAirportId = dto.DestinationAirportId;
        route.DistanceKm = dto.DistanceKm;
        route.EstimatedDurationMin = dto.EstimatedDurationMin;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Routes.Where(r => r.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/duration")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateDuration(int id, [FromBody] DurationUpdateDto dto)
    {
        var route = await _context.Routes.FindAsync(id);
        if (route == null) return NotFound();

        route.EstimatedDurationMin = dto.EstimatedDurationMin;
        await _context.SaveChangesAsync();

        // note this does NOT touch Flight.ArrivalTime on existing flights.
        // that's deliberate - ArrivalTime is stored per flight precisely so
        // a schedule change on one flight doesn't ripple through the route,
        // and vice versa
        return Ok(await ToDto(_context.Routes.Where(r => r.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var route = await _context.Routes.FindAsync(id);
        if (route == null) return NotFound();

        // Flight.RouteId is Restrict - we set that so retiring a route can't
        // silently erase its flight history. so SaveChanges throws here
        // unless we check first
        var flightCount = await _context.Flights.CountAsync(f => f.RouteId == id);
        if (flightCount > 0)
            return Conflict($"Cannot delete this route — {flightCount} flight(s) use it.");

        _context.Routes.Remove(route);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [AllowAnonymous]   // safe now - the DTO exposes a flight COUNT, not the
                       // flight rows. the network map is public information
    public async Task<IActionResult> GetAll()
    {
        // OrderBy moved INSIDE the ToDto call. it used to be chained onto
        // the result as .OrderBy(r => r.OriginCode), which compiles but dies
        // at runtime - EF Core can't see through the record constructor.
        // note the path changes too: OriginCode on the dto is
        // OriginAirport.Code on the entity. new fix
        return Ok(await ToDto(
            _context.Routes
                .OrderBy(r => r.OriginAirport.Code)
                .ThenBy(r => r.DestinationAirport.Code))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var route = await ToDto(_context.Routes.Where(r => r.Id == id))
            .FirstOrDefaultAsync();
        return route == null ? NotFound() : Ok(route);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter(
        [FromQuery] int? originId,
        [FromQuery] int? destinationId,
        [FromQuery] int? maxDistance,
        [FromQuery] string? originCode,
        [FromQuery] string? destinationCountry)
    {
        var query = _context.Routes.AsQueryable();

        if (originId.HasValue)
            query = query.Where(r => r.OriginAirportId == originId.Value);

        if (destinationId.HasValue)
            query = query.Where(r => r.DestinationAirportId == destinationId.Value);

        if (maxDistance.HasValue)
            query = query.Where(r => r.DistanceKm <= maxDistance.Value);

        // filtering by CODE not id - much friendlier for the frontend,
        // "MCT" beats looking up that Muscat happens to be airport 3
        if (!string.IsNullOrWhiteSpace(originCode))
            query = query.Where(r => r.OriginAirport.Code == originCode.ToUpper());

        // reaching into the related table for the filter, which is what
        // the spec asks for on this case
        if (!string.IsNullOrWhiteSpace(destinationCountry))
            query = query.Where(r => r.DestinationAirport.Country.Contains(destinationCountry));

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderBy(r => r.DistanceKm)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // the original did .Select(r => new { ... _context.Routes.Count() ... })
        // then FirstOrDefault - a per-row projection where every field
        // ignored r completely. it produced the right numbers by accident,
        // but returned NULL on an empty table instead of zeros, had no
        // OrderBy (the spec asks for sorting on this case), and shadowed r
        // with a second r in the inner lambdas.
        // new fix - grouped per origin airport, which is an actual
        // aggregate and reaches across a relationship.
        //
        // the OrderByDescending here is safe AFTER the Select because this
        // projects to an anonymous type - those keep their property names
        // visible to EF Core, unlike the positional records in ToDto
        var byOrigin = await _context.Routes
            .GroupBy(r => new { r.OriginAirportId, r.OriginAirport.Code, r.OriginAirport.City })
            .Select(g => new
            {
                AirportId = g.Key.OriginAirportId,
                g.Key.Code,
                g.Key.City,
                RouteCount = g.Count(),
                TotalFlights = g.Sum(r => r.Flights.Count),
                AverageDistanceKm = Math.Round(g.Average(r => (decimal)r.DistanceKm), 1),
                LongestRouteKm = g.Max(r => r.DistanceKm)
            })
            .OrderByDescending(s => s.RouteCount)
            .ToListAsync();

        // network-wide totals as a separate query. AnyAsync first because
        // Average() throws on an empty table while Count() and Sum() are
        // happy with zero rows - same trap as the SeatClass stats
        var hasRoutes = await _context.Routes.AnyAsync();
        var network = new
        {
            TotalRoutes = await _context.Routes.CountAsync(),
            AverageDistanceKm = hasRoutes
                ? Math.Round(await _context.Routes.AverageAsync(r => (decimal)r.DistanceKm), 1)
                : 0m,
            LongestRouteKm = hasRoutes ? await _context.Routes.MaxAsync(r => r.DistanceKm) : 0,
            ShortestRouteKm = hasRoutes ? await _context.Routes.MinAsync(r => r.DistanceKm) : 0
        };

        return Ok(new { Network = network, ByOriginAirport = byOrigin });
    }
}