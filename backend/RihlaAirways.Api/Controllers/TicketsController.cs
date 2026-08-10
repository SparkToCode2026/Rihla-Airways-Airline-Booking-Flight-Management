using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TicketsController : ControllerBase
{
    private readonly AppDbContext _context;

    public TicketsController(AppDbContext context) => _context = context;

    // --- input DTOs ---
    // no Price here on purpose - see the note in Create. the client tells us
    // WHAT they're booking, the server decides what it costs
    public record TicketCreateDto(
        [Required] int BookingId,
        [Required] int FlightId,
        [Required] int SeatClassId,
        [Required][MaxLength(5)] string SeatNumber,
        [Required][MaxLength(150)] string PassengerName);

    // FlightId and BookingId are NOT updatable - moving a ticket to another
    // flight is a rebooking, not an edit, and it silently invalidates the
    // seat assignment (14C exists on one plane, might not on another).
    // that was possible in the original version - new fix
    public record TicketUpdateDto(
        [Required][MaxLength(5)] string SeatNumber,
        [Required] int SeatClassId,
        [Required][MaxLength(150)] string PassengerName);

    public record SeatUpdateDto([Required][MaxLength(5)] string SeatNumber);

    // --- output DTO ---
    // flattened on purpose. returning the entity with Include() would drag
    // Booking along, and Booking carries a User, and User carries
    // PasswordHash - so an anonymous ticket lookup would leak password
    // hashes. it also loops forever in the json serializer
    public record TicketResponseDto(
        int Id, int BookingId, int FlightId, string FlightNumber,
        DateTime DepartureTime, string OriginCode, string DestinationCode,
        int SeatClassId, string SeatClassName, string SeatNumber,
        decimal Price, string PassengerName, int BaggageCount, decimal TotalBaggageFee);

    // one definition of a Ticket on the way out. note the Route hop -
    // Flight has no direct Airport FK, so origin/destination come through
    // Flight -> Route -> OriginAirport. with Include() that's a
    // .ThenInclude() chain; in a Select it's just dotted access and EF Core
    // turns the whole thing into one JOIN.
    //
    // never chain .OrderBy() onto the RESULT of this - the dto is a
    // positional record, so once the values are inside a constructor call
    // EF Core can't map .DepartureTime back to a column and the query
    // fails at runtime. sort the entities first, then project
    private static readonly Func<IQueryable<Ticket>, IQueryable<TicketResponseDto>> ToDto =
        q => q.Select(t => new TicketResponseDto(
            t.Id, t.BookingId, t.FlightId,
            t.Flight.FlightNumber, t.Flight.DepartureTime,
            t.Flight.Route.OriginAirport.Code,
            t.Flight.Route.DestinationAirport.Code,
            t.SeatClassId, t.SeatClass.Name, t.SeatNumber,
            t.Price, t.PassengerName,
            t.Baggages.Count,
            t.Baggages.Sum(b => b.Fee)));


    // ============ 1. POST — create ============
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] TicketCreateDto dto)
    {
        // edge cases first, and there are four of them here - Ticket has
        // three FKs plus a composite unique index, so it has more ways to
        // blow up than any other table in the schema

        // 1) FK checks. without these, a bad id throws DbUpdateException
        // and the client gets a raw 500 with no explanation
        var booking = await _context.Bookings.FindAsync(dto.BookingId);
        if (booking == null) return BadRequest($"Booking {dto.BookingId} not found.");

        var flight = await _context.Flights
            .Include(f => f.Airplane)
            .FirstOrDefaultAsync(f => f.Id == dto.FlightId);
        if (flight == null) return BadRequest($"Flight {dto.FlightId} not found.");

        var seatClass = await _context.SeatClasses.FindAsync(dto.SeatClassId);
        if (seatClass == null) return BadRequest($"SeatClass {dto.SeatClassId} not found.");

        var seat = dto.SeatNumber.ToUpper();   // 14c and 14C are the same seat

        // 2) the composite unique index (FlightId, SeatNumber). this is the
        // one we added specifically to stop double-selling a seat - catching
        // it here turns a 500 into a message that says what went wrong.
        // note we compare the UPPERCASED seat, not the raw dto value -
        // otherwise posting "14c" would slip past this check and then still
        // collide at the database once we normalise it below. new fix
        if (await _context.Tickets.AnyAsync(t =>
                t.FlightId == dto.FlightId && t.SeatNumber == seat))
            return Conflict($"Seat {seat} is already taken on flight {flight.FlightNumber}.");

        // 3) capacity. no db constraint covers this one - Airplane.Capacity
        // is just an int, nothing stops us selling 200 seats on a 180-seater
        var sold = await _context.Tickets.CountAsync(t => t.FlightId == dto.FlightId);
        if (sold >= flight.Airplane.Capacity)
            return Conflict($"Flight {flight.FlightNumber} is fully booked ({sold}/{flight.Airplane.Capacity}).");

        // 4) can't sell a seat on a cancelled flight
        if (flight.Status == "Cancelled")
            return Conflict($"Flight {flight.FlightNumber} is cancelled.");

        // price is CALCULATED, not accepted from the request. the original
        // took Price straight off the dto, which let the client post
        // Price = 0 - and made SeatClass.PriceMultiplier pointless.
        // base fare from route distance, then the class multiplier.
        // awaited on its own line - the old version was
        // "await basePrice * multiplier" on one line, which happens to work
        // because await binds tighter than *, but reads like a bug
        var basePrice = await CalculateBaseFare(flight.RouteId);
        var price = Math.Round(basePrice * seatClass.PriceMultiplier, 2);

        var ticket = new Ticket
        {
            BookingId = dto.BookingId,
            FlightId = dto.FlightId,
            SeatClassId = dto.SeatClassId,
            SeatNumber = seat,
            Price = price,
            PassengerName = dto.PassengerName
        };

        _context.Tickets.Add(ticket);

        // keep the booking total in step. TotalAmount is denormalized on
        // purpose (frozen at purchase) but it still has to be right at the
        // moment we write it. this controller is the ONLY writer of that
        // field - BookingsController just recalculates from the tickets
        booking.TotalAmount += price;

        await _context.SaveChangesAsync();

        var result = await ToDto(_context.Tickets.Where(t => t.Id == ticket.Id))
            .FirstAsync();
        return CreatedAtAction(nameof(GetById), new { id = ticket.Id }, result);
    }


    // ============ 2. PUT — full update ============
    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] TicketUpdateDto dto)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        var seatClass = await _context.SeatClasses.FindAsync(dto.SeatClassId);
        if (seatClass == null) return BadRequest($"SeatClass {dto.SeatClassId} not found.");

        // same unique-index check as Create, but excluding this ticket -
        // without "t.Id != id" you'd conflict with yourself when saving
        // the seat unchanged. easy one to miss
        var seat = dto.SeatNumber.ToUpper();
        if (await _context.Tickets.AnyAsync(t =>
                t.FlightId == ticket.FlightId && t.SeatNumber == seat && t.Id != id))
            return Conflict($"Seat {seat} is already taken on this flight.");

        // changing seat class changes the price, so recalculate and adjust
        // the booking total by the DIFFERENCE, not the full amount
        if (dto.SeatClassId != ticket.SeatClassId)
        {
            var flight = await _context.Flights.FindAsync(ticket.FlightId);
            var basePrice = await CalculateBaseFare(flight!.RouteId);
            var newPrice = Math.Round(basePrice * seatClass.PriceMultiplier, 2);

            var booking = await _context.Bookings.FindAsync(ticket.BookingId);
            if (booking != null) booking.TotalAmount += newPrice - ticket.Price;

            ticket.Price = newPrice;
            ticket.SeatClassId = dto.SeatClassId;
        }

        ticket.SeatNumber = seat;
        ticket.PassengerName = dto.PassengerName;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Tickets.Where(t => t.Id == id)).FirstAsync());
    }


    // ============ 3. PATCH — second distinct update ============
    [HttpPatch("{id}/seat")]
    [Authorize]
    public async Task<IActionResult> UpdateSeatNumber(int id, [FromBody] SeatUpdateDto dto)
    {
        // a DTO instead of [FromBody] string - with a bare string, swagger
        // makes you type "14C" WITH quotes and everyone gets a 400 first try
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        var seat = dto.SeatNumber.ToUpper();
        if (await _context.Tickets.AnyAsync(t =>
                t.FlightId == ticket.FlightId && t.SeatNumber == seat && t.Id != id))
            return Conflict($"Seat {seat} is already taken on this flight.");

        ticket.SeatNumber = seat;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Tickets.Where(t => t.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        // no FK guard needed here, unlike UsersController - Baggage cascades
        // from Ticket, so the bags go automatically. that's the weak-entity
        // relationship doing its job

        var booking = await _context.Bookings.FindAsync(ticket.BookingId);
        if (booking != null) booking.TotalAmount -= ticket.Price;

        _context.Tickets.Remove(ticket);
        await _context.SaveChangesAsync();
        return NoContent();
    }


    // ============ 5. GET list (with related entities) ============
    [HttpGet]
    [Authorize]   // was AllowAnonymous - this returns every passenger name
                  // and seat on every flight in the system
    public async Task<IActionResult> GetAll()
    {
        // OrderBy sits INSIDE the ToDto call, on the entities - chaining it
        // onto the result would throw at runtime, see the note on ToDto
        return Ok(await ToDto(
            _context.Tickets
                .OrderBy(t => t.Flight.DepartureTime)
                .ThenBy(t => t.SeatNumber))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        var ticket = await ToDto(_context.Tickets.Where(t => t.Id == id))
            .FirstOrDefaultAsync();
        return ticket == null ? NotFound() : Ok(ticket);
    }


    // ============ 7. GET filter (LINQ Where) ============
    [HttpGet("filter")]
    [Authorize]
    public async Task<IActionResult> Filter(
        [FromQuery] int? flightId,
        [FromQuery] int? seatClassId,
        [FromQuery] decimal? minPrice,
        [FromQuery] string? passengerName,
        [FromQuery] string? destinationCode)
    {
        var query = _context.Tickets.AsQueryable();

        if (flightId.HasValue)
            query = query.Where(t => t.FlightId == flightId.Value);

        if (seatClassId.HasValue)
            query = query.Where(t => t.SeatClassId == seatClassId.Value);

        if (minPrice.HasValue)
            query = query.Where(t => t.Price >= minPrice.Value);

        if (!string.IsNullOrWhiteSpace(passengerName))
            query = query.Where(t => t.PassengerName.Contains(passengerName));

        // filtering on a RELATED table's property, two hops away. the spec
        // specifically asks for filters that reach across relationships,
        // and this is the clearest example in the whole project
        if (!string.IsNullOrWhiteSpace(destinationCode))
            query = query.Where(t =>
                t.Flight.Route.DestinationAirport.Code == destinationCode.ToUpper());

        return Ok(await ToDto(query.OrderBy(t => t.Flight.DepartureTime)).ToListAsync());
    }


    // ============ 8. GET sort/aggregate ============
    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> GetStats()
    {
        // this one CAN order after the Select - anonymous types (new { ... })
        // keep their property names visible to EF Core, unlike positional
        // records where the values vanish into a constructor call
        var stats = await _context.Tickets
            .GroupBy(t => new { t.FlightId, t.Flight.FlightNumber })
            .Select(g => new
            {
                FlightId = g.Key.FlightId,
                // grouping by the number too so the output is readable -
                // "flight 7 earned 4200" means nothing to a human,
                // "RA204 earned 4200" does
                FlightNumber = g.Key.FlightNumber,
                TotalTickets = g.Count(),
                TotalRevenue = g.Sum(t => t.Price),
                AveragePrice = Math.Round(g.Average(t => t.Price), 2),
                HighestFare = g.Max(t => t.Price)
            })
            .OrderByDescending(s => s.TotalRevenue)
            .ToListAsync();

        return Ok(stats);
    }


    // base fare from route distance. crude on purpose - a real airline
    // prices on demand, season, how far ahead you booked, etc. this at
    // least makes SeatClass.PriceMultiplier mean something instead of
    // letting the client name its own price
    private async Task<decimal> CalculateBaseFare(int routeId)
    {
        var route = await _context.Routes.FindAsync(routeId);
        if (route == null) return 50m;
        return 20m + (route.DistanceKm * 0.08m);
    }
}