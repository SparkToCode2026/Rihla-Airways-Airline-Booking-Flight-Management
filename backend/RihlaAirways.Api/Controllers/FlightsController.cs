using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FlightsController : ControllerBase
{
    private readonly AppDbContext _context;

    public FlightsController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // no Status on create - a flight always starts Scheduled and moves
    // through the state machine below
    public record FlightCreateDto(
        [Required][MaxLength(10)] string FlightNumber,
        [Required] int RouteId,
        [Required] int AirplaneId,
        [Required] DateTime DepartureTime,
        [Required] DateTime ArrivalTime);

    public record FlightUpdateDto(
        [Required][MaxLength(10)] string FlightNumber,
        [Required] int AirplaneId,
        [Required] DateTime DepartureTime,
        [Required] DateTime ArrivalTime);
    // RouteId not updatable - changing where a flight goes after tickets
    // are sold is a different operation entirely. new fix

    public record StatusUpdateDto([Required] string Status);

    // --- output DTO ---
    // the original returned entities with Tickets AND FlightCrews included.
    // for 100 flights with 100 tickets each that's 10,000 nested rows in one
    // response, plus the json cycle crash, plus every passenger name on an
    // anonymous endpoint. counts are what the list view actually needs.
    //
    // never chain .OrderBy() onto the RESULT of this - positional record,
    // so EF Core can't map .DepartureTime back to a column once the values
    // are inside the constructor. sort (and page) the entities first
    public record FlightResponseDto(
        int Id, string FlightNumber, string Status,
        DateTime DepartureTime, DateTime ArrivalTime, int DurationMin,
        int RouteId, string OriginCode, string OriginCity,
        string DestinationCode, string DestinationCity, int DistanceKm,
        int AirplaneId, string AirplaneModel, string Registration,
        int Capacity, int SeatsSold, int SeatsAvailable, int CrewAssigned);

    private static readonly Func<IQueryable<Flight>, IQueryable<FlightResponseDto>> ToDto =
        q => q.Select(f => new FlightResponseDto(
            f.Id, f.FlightNumber, f.Status, f.DepartureTime, f.ArrivalTime,
            // EF.Functions.DateDiffMinute translates to SQL DATEDIFF -
            // plain (ArrivalTime - DepartureTime).TotalMinutes does NOT
            // translate and throws at runtime
            EF.Functions.DateDiffMinute(f.DepartureTime, f.ArrivalTime),
            f.RouteId,
            f.Route.OriginAirport.Code, f.Route.OriginAirport.City,
            f.Route.DestinationAirport.Code, f.Route.DestinationAirport.City,
            f.Route.DistanceKm,
            f.AirplaneId, f.Airplane.Model, f.Airplane.RegistrationNumber,
            f.Airplane.Capacity,
            f.Tickets.Count,
            f.Airplane.Capacity - f.Tickets.Count,
            f.FlightCrews.Count));

    private static readonly string[] ValidStatuses =
        { "Scheduled", "Boarding", "Departed", "Landed", "Delayed", "Cancelled" };

    // same state machine idea as PaymentsController. without it a Landed
    // flight can flip back to Scheduled, which makes the whole status
    // field meaningless. Landed and Cancelled are terminal
    private static readonly Dictionary<string, string[]> AllowedTransitions = new()
    {
        ["Scheduled"] = new[] { "Boarding", "Delayed", "Cancelled" },
        ["Delayed"]   = new[] { "Boarding", "Scheduled", "Cancelled" },
        ["Boarding"]  = new[] { "Departed", "Delayed", "Cancelled" },
        ["Departed"]  = new[] { "Landed" },
        ["Landed"]    = Array.Empty<string>(),
        ["Cancelled"] = Array.Empty<string>()
    };


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Create([FromBody] FlightCreateDto dto)
    {
        // edge cases first - Flight is the hub of the schema and has more
        // ways to produce bad data than any other table

        // 1) a flight that lands before it departs. no db constraint catches
        // this, both datetimes are individually valid
        if (dto.ArrivalTime <= dto.DepartureTime)
            return BadRequest("Arrival time must be after departure time.");

        if (dto.DepartureTime < DateTime.UtcNow)
            return BadRequest("Cannot schedule a flight in the past.");

        // 2) both FKs
        if (!await _context.Routes.AnyAsync(r => r.Id == dto.RouteId))
            return BadRequest($"Route {dto.RouteId} not found.");

        if (!await _context.Airplanes.AnyAsync(a => a.Id == dto.AirplaneId))
            return BadRequest($"Airplane {dto.AirplaneId} not found.");

        // 3) UX_Flight_NumberDeparture is unique on the pair
        var number = dto.FlightNumber.ToUpper();
        if (await _context.Flights.AnyAsync(f =>
                f.FlightNumber == number && f.DepartureTime == dto.DepartureTime))
            return Conflict($"Flight {number} already departs at that exact time.");

        // 4) the one with no db constraint behind it at all: the same
        // physical aircraft scheduled on two overlapping flights. two rows
        // that are each perfectly valid but together are impossible.
        // classic overlap test - A starts before B ends AND A ends after B starts
        var clash = await _context.Flights
            .Where(f => f.AirplaneId == dto.AirplaneId
                     && f.Status != "Cancelled"
                     && f.DepartureTime < dto.ArrivalTime
                     && f.ArrivalTime > dto.DepartureTime)
            .Select(f => f.FlightNumber)
            .FirstOrDefaultAsync();

        if (clash != null)
            return Conflict($"That airplane is already scheduled on flight {clash} during this window.");

        var flight = new Flight
        {
            FlightNumber = number,
            RouteId = dto.RouteId,
            AirplaneId = dto.AirplaneId,
            DepartureTime = dto.DepartureTime,
            ArrivalTime = dto.ArrivalTime,
            Status = "Scheduled"      // always, never from the dto
        };

        _context.Flights.Add(flight);
        await _context.SaveChangesAsync();

        var result = await ToDto(_context.Flights.Where(f => f.Id == flight.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetById), new { id = flight.Id }, result);
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Update(int id, [FromBody] FlightUpdateDto dto)
    {
        var flight = await _context.Flights.FindAsync(id);
        if (flight == null) return NotFound();

        if (flight.Status is "Departed" or "Landed")
            return Conflict($"Cannot edit a flight that has already {flight.Status.ToLower()}.");

        if (dto.ArrivalTime <= dto.DepartureTime)
            return BadRequest("Arrival time must be after departure time.");

        if (!await _context.Airplanes.AnyAsync(a => a.Id == dto.AirplaneId))
            return BadRequest($"Airplane {dto.AirplaneId} not found.");

        var number = dto.FlightNumber.ToUpper();

        // "f.Id != id" - otherwise saving unchanged values conflicts with itself
        if (await _context.Flights.AnyAsync(f =>
                f.FlightNumber == number && f.DepartureTime == dto.DepartureTime && f.Id != id))
            return Conflict($"Flight {number} already departs at that exact time.");

        // swapping to a smaller aircraft after seats are sold would strand
        // passengers with no seat. nothing in the db stops it
        var airplane = await _context.Airplanes.FindAsync(dto.AirplaneId);
        var sold = await _context.Tickets.CountAsync(t => t.FlightId == id);
        if (sold > airplane!.Capacity)
            return Conflict($"{sold} seats are sold but that airplane only holds {airplane.Capacity}.");

        var clash = await _context.Flights
            .Where(f => f.AirplaneId == dto.AirplaneId && f.Id != id
                     && f.Status != "Cancelled"
                     && f.DepartureTime < dto.ArrivalTime
                     && f.ArrivalTime > dto.DepartureTime)
            .Select(f => f.FlightNumber)
            .FirstOrDefaultAsync();

        if (clash != null)
            return Conflict($"That airplane is already scheduled on flight {clash} during this window.");

        flight.FlightNumber = number;
        flight.AirplaneId = dto.AirplaneId;
        flight.DepartureTime = dto.DepartureTime;
        flight.ArrivalTime = dto.ArrivalTime;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Flights.Where(f => f.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update (status change) ============
    [HttpPatch("{id}/status")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] StatusUpdateDto dto)
    {
        var flight = await _context.Flights.FindAsync(id);
        if (flight == null) return NotFound();

        if (!ValidStatuses.Contains(dto.Status))
            return BadRequest($"Status must be one of: {string.Join(", ", ValidStatuses)}");

        var allowed = AllowedTransitions[flight.Status];
        if (!allowed.Contains(dto.Status))
            return Conflict(allowed.Length == 0
                ? $"A {flight.Status} flight cannot change status."
                : $"Cannot go from {flight.Status} to {dto.Status}. Allowed: {string.Join(", ", allowed)}");

        flight.Status = dto.Status;
        await _context.SaveChangesAsync();

        // the spec's second email trigger lives here - flight status /
        // reminder notifications. leaving the hook so nobody has to hunt
        // for the right place in week 2
        // TODO week 2: if Delayed or Cancelled ->
        //   _emailService.SendFlightStatusAsync(id, dto.Status) for every
        //   passenger holding a ticket on this flight

        return Ok(await ToDto(_context.Flights.Where(f => f.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var flight = await _context.Flights.FindAsync(id);
        if (flight == null) return NotFound();

        // Ticket.FlightId is Restrict, so this throws DbUpdateException.
        // and that's the RIGHT outcome - a flight with sold tickets gets
        // Status = Cancelled, it does not get erased. the passengers still
        // need their booking history
        var ticketCount = await _context.Tickets.CountAsync(t => t.FlightId == id);
        if (ticketCount > 0)
            return Conflict($"Cannot delete a flight with {ticketCount} ticket(s). Cancel it instead.");

        // note FlightCrew.FlightId is Cascade, so the roster rows go
        // automatically once tickets are clear. different behaviour to
        // tickets on purpose - an unsold flight's roster is disposable
        _context.Flights.Remove(flight);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [AllowAnonymous]   // safe now - the DTO returns seat COUNTS, not the
                       // ticket rows. a departure board is public info
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        // paging because flights are the highest-volume table here and an
        // unbounded list gets slow fast. none of the other controllers
        // need this yet
        if (pageSize is < 1 or > 200) pageSize = 50;
        if (page < 1) page = 1;

        var total = await _context.Flights.CountAsync();

        // the ORDER and the PAGING both happen on the entities, before the
        // projection. it used to be ToDto(...).OrderBy(f => f.DepartureTime)
        // .Skip().Take(), which compiles and then throws at runtime - EF Core
        // can't see .DepartureTime through the record constructor.
        // and Skip/Take without an OrderBy is undefined ordering in SQL
        // anyway, so page 2 could repeat rows from page 1. new fix
        var items = await ToDto(
            _context.Flights
                .OrderBy(f => f.DepartureTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var flight = await ToDto(_context.Flights.Where(f => f.Id == id))
            .FirstOrDefaultAsync();
        return flight == null ? NotFound() : Ok(flight);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [AllowAnonymous]
    public async Task<IActionResult> Filter(
        [FromQuery] string? status,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string? originCode,
        [FromQuery] string? destinationCode,
        [FromQuery] bool? availableOnly)
    {
        var query = _context.Flights.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(f => f.Status == status);

        if (fromDate.HasValue)
            query = query.Where(f => f.DepartureTime >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(f => f.DepartureTime <= toDate.Value);

        // this is the search a passenger actually performs - MCT to DXB on
        // a date. reaches two tables deep through Route into Airport
        if (!string.IsNullOrWhiteSpace(originCode))
            query = query.Where(f => f.Route.OriginAirport.Code == originCode.ToUpper());

        if (!string.IsNullOrWhiteSpace(destinationCode))
            query = query.Where(f => f.Route.DestinationAirport.Code == destinationCode.ToUpper());

        // comparing a count across a relationship against a column on
        // another related table - and EF Core still turns it into one query
        if (availableOnly == true)
            query = query.Where(f => f.Tickets.Count < f.Airplane.Capacity
                                  && f.Status == "Scheduled");

        // same fix as GetAll - sort the entities, then project
        return Ok(await ToDto(query.OrderBy(f => f.DepartureTime)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // these OrderBys are safe after the Select - anonymous types keep
        // their property names visible to EF Core, positional records don't
        var byStatus = await _context.Flights
            .GroupBy(f => f.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                TicketsSold = g.Sum(f => f.Tickets.Count),
                Revenue = g.Sum(f => f.Tickets.Sum(t => t.Price))
            })
            .OrderByDescending(s => s.Count)
            .ToListAsync();

        // load factor per route - the number an airline actually manages by.
        // groups by a related table's columns, aggregates over two more
        var byRoute = await _context.Flights
            .GroupBy(f => new
            {
                f.RouteId,
                Origin = f.Route.OriginAirport.Code,
                Destination = f.Route.DestinationAirport.Code
            })
            .Select(g => new
            {
                g.Key.RouteId,
                g.Key.Origin,
                g.Key.Destination,
                FlightCount = g.Count(),
                TotalSeats = g.Sum(f => f.Airplane.Capacity),
                TotalSold = g.Sum(f => f.Tickets.Count),
                Revenue = g.Sum(f => f.Tickets.Sum(t => t.Price))
            })
            .OrderByDescending(s => s.Revenue)
            .ToListAsync();

        // the percentage is computed AFTER ToListAsync, in memory - dividing
        // inside the query risks a divide-by-zero in SQL on a route with no
        // seats. safer to project first, calculate second
        var withLoadFactor = byRoute.Select(r => new
        {
            r.RouteId, r.Origin, r.Destination, r.FlightCount,
            r.TotalSeats, r.TotalSold, r.Revenue,
            LoadFactorPercent = r.TotalSeats > 0
                ? Math.Round((decimal)r.TotalSold / r.TotalSeats * 100, 1)
                : 0m
        });

        return Ok(new { ByStatus = byStatus, ByRoute = withLoadFactor });
    }
}