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
    // seat numbers used to be accepted verbatim - "!!" and "999ZZ" both got a
    // 201. MaxLength(5) was the only guard, and the per-flight unique index
    // happily enforced uniqueness on nonsense. capacity is checked by COUNT, so
    // you could fill a plane entirely with seats that don't physically exist.
    //
    // Airplane only stores Capacity, not a seat map, so rows are derived
    // assuming the 6-abreast A-F layout the fleet and the seed data both use.
    // if a widebody with a different layout is ever added this is the one place
    // that needs revisiting
    private const int SeatsPerRow = 6;   // A B C D E F

    private static readonly System.Text.RegularExpressions.Regex SeatPattern =
        new(@"^(?<row>[1-9][0-9]{0,2})(?<letter>[A-F])$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // returns null when the seat is fine, otherwise the message to send back
    private static string? ValidateSeat(string seat, int capacity, string flightNumber)
    {
        var match = SeatPattern.Match(seat);
        if (!match.Success)
            return $"Seat '{seat}' is not a valid seat number. " +
                   "Use a row followed by a letter A-F, for example 12C.";

        var row = int.Parse(match.Groups["row"].Value);
        var lastRow = (int)Math.Ceiling(capacity / (double)SeatsPerRow);

        if (row > lastRow)
            return $"Seat {seat} does not exist on flight {flightNumber} — " +
                   $"that aircraft has {capacity} seats, so rows run 1 to {lastRow}.";

        return null;
    }

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


    // --- ownership ---
    // [Authorize] alone only asks "are you SOMEBODY". these ask "is this YOURS",
    // which is the question that actually matters on a user-owned record.
    // a Ticket's owner is two hops away - Ticket -> Booking -> UserId - which is
    // why this needs a query rather than a field comparison like the one in
    // BookingsController.
    //
    // this controller previously had no ownership check at all, on any endpoint.
    // that meant any logged-in passenger could read the entire airline's
    // manifest, reseat a stranger, rename the passenger on someone else's
    // ticket, add a ticket to a stranger's booking (they fly, the other person
    // pays) or DELETE any ticket in the system. the scoping pattern here is the
    // one BookingsController.GetAll already uses - it just never got copied over

    private bool IsStaff => User.IsInRole("Admin") || User.IsInRole("Staff");

    // null for staff/admin (meaning "no scoping needed"), otherwise the caller's id
    private int? CallerId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(raw, out var id) ? id : null;
    }

    // does the caller own the booking this ticket hangs off?
    private async Task<bool> CanAccessTicket(int ticketId)
    {
        if (IsStaff) return true;
        var uid = CallerId();
        if (uid == null) return false;
        return await _context.Tickets
            .AnyAsync(t => t.Id == ticketId && t.Booking.UserId == uid.Value);
    }

    // used by Create - you may only add tickets to a booking you own
    private async Task<bool> CanAccessBooking(int bookingId)
    {
        if (IsStaff) return true;
        var uid = CallerId();
        if (uid == null) return false;
        return await _context.Bookings
            .AnyAsync(b => b.Id == bookingId && b.UserId == uid.Value);
    }

    // narrows a ticket query to the caller's own tickets unless they're staff.
    // same shape as BookingsController.GetAll's scoping block
    private IQueryable<Ticket> ScopeToCaller(IQueryable<Ticket> query)
    {
        if (IsStaff) return query;
        var uid = CallerId();
        if (uid == null) return query.Where(_ => false);
        return query.Where(t => t.Booking.UserId == uid.Value);
    }


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
        var booking = await _context.Bookings
            .Include(b => b.Payment)
            .FirstOrDefaultAsync(b => b.Id == dto.BookingId);
        if (booking == null) return BadRequest($"Booking {dto.BookingId} not found.");

        // ownership BEFORE anything else is written. without this a passenger
        // could post tickets onto a stranger's booking - and since
        // booking.TotalAmount += price below, the stranger gets the bill
        if (!await CanAccessBooking(dto.BookingId)) return Forbid();

        // a booking stops accepting tickets once it's been settled or cancelled.
        // TotalAmount is the figure the payment matched, so adding a ticket
        // afterwards pushes the total ABOVE the completed payment and the
        // booking looks underpaid forever - it's the drift the IntegrityCheck
        // in BookingsController.GetStats exists to catch.
        // the real-world equivalent is simply making another booking
        if (booking.Payment is { Status: "Completed" })
            return Conflict("This booking is already paid. Make a new booking for " +
                            "additional tickets, or ask staff to reissue this one.");

        if (booking.Status == "Cancelled")
            return Conflict("Cannot add tickets to a cancelled booking.");

        var flight = await _context.Flights
            .Include(f => f.Airplane)
            .FirstOrDefaultAsync(f => f.Id == dto.FlightId);
        if (flight == null) return BadRequest($"Flight {dto.FlightId} not found.");

        var seatClass = await _context.SeatClasses.FindAsync(dto.SeatClassId);
        if (seatClass == null) return BadRequest($"SeatClass {dto.SeatClassId} not found.");

        var seat = dto.SeatNumber.Trim().ToUpper();   // 14c and 14C are the same seat

        // the seat has to actually exist on this aircraft
        var seatError = ValidateSeat(seat, flight.Airplane.Capacity, flight.FlightNumber);
        if (seatError != null) return BadRequest(seatError);

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
    // staff-only, and that's a business rule rather than a security one.
    // this endpoint changes PassengerName and SeatClassId:
    //   - a name change is identity-critical and fee-bearing at every real
    //     airline. leaving it self-service is the buy-cheap / rename / resell
    //     path, and it detaches the ticket from the passport on the profile
    //   - a seat-class change re-prices the ticket and moves booking.TotalAmount,
    //     which on a paid booking would leave the total and the completed
    //     payment disagreeing
    // a passenger who wants a different seat uses PATCH {id}/seat below, which
    // is genuinely self-service. anything else goes through the desk
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Update(int id, [FromBody] TicketUpdateDto dto)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        var seatClass = await _context.SeatClasses.FindAsync(dto.SeatClassId);
        if (seatClass == null) return BadRequest($"SeatClass {dto.SeatClassId} not found.");

        // same unique-index check as Create, but excluding this ticket -
        // without "t.Id != id" you'd conflict with yourself when saving
        // the seat unchanged. easy one to miss
        var seat = dto.SeatNumber.Trim().ToUpper();

        var seatFlight = await _context.Flights
            .Include(f => f.Airplane)
            .FirstAsync(f => f.Id == ticket.FlightId);
        var seatError = ValidateSeat(seat, seatFlight.Airplane.Capacity, seatFlight.FlightNumber);
        if (seatError != null) return BadRequest(seatError);

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
    // the ONE ticket write a passenger keeps, and deliberately so - picking your
    // own seat is self-service at every airline, it costs nothing and it moves
    // no money. staff can still do it on a customer's behalf at the desk
    [HttpPatch("{id}/seat")]
    [Authorize]
    public async Task<IActionResult> UpdateSeatNumber(int id, [FromBody] SeatUpdateDto dto)
    {
        // a DTO instead of [FromBody] string - with a bare string, swagger
        // makes you type "14C" WITH quotes and everyone gets a 400 first try
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();
        if (!await CanAccessTicket(id)) return Forbid();

        var seat = dto.SeatNumber.Trim().ToUpper();

        var seatFlight = await _context.Flights
            .Include(f => f.Airplane)
            .FirstAsync(f => f.Id == ticket.FlightId);

        // you can move seat right up until the aircraft goes, but not after.
        // without this a passenger could reseat themselves on a flight that
        // landed last week, which rewrites history rather than a seat map
        if (seatFlight.Status is "Departed" or "Landed")
            return Conflict($"Flight {seatFlight.FlightNumber} has already " +
                            $"{seatFlight.Status.ToLower()} — seats can no longer be changed.");

        if (seatFlight.Status == "Cancelled")
            return Conflict($"Flight {seatFlight.FlightNumber} is cancelled.");

        var seatError = ValidateSeat(seat, seatFlight.Airplane.Capacity, seatFlight.FlightNumber);
        if (seatError != null) return BadRequest(seatError);

        if (await _context.Tickets.AnyAsync(t =>
                t.FlightId == ticket.FlightId && t.SeatNumber == seat && t.Id != id))
            return Conflict($"Seat {seat} is already taken on this flight.");

        ticket.SeatNumber = seat;
        await _context.SaveChangesAsync();

        return Ok(await ToDto(_context.Tickets.Where(t => t.Id == id)).FirstAsync());
    }


    // ============ 4. DELETE ============
    // staff-only. a passenger does not delete a ticket - they cancel the
    // BOOKING, which is a state change that leaves the record and its payment
    // intact and lets refund rules run. deleting a ticket instead just makes
    // the row vanish and silently drops booking.TotalAmount, which on a paid
    // booking leaves the total below its own completed payment
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Delete(int id)
    {
        var ticket = await _context.Tickets
            .Include(t => t.Booking).ThenInclude(b => b.Payment)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        // even for staff: money already taken means the total can't just drop
        // out from under it. refund first, then the booking can be unwound
        if (ticket.Booking.Payment is { Status: "Completed" })
            return Conflict("This ticket's booking has a completed payment. " +
                            "Refund it before removing tickets, or cancel the booking instead.");

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
        // [Authorize] made this "logged in only", but a logged-in PASSENGER
        // was still getting every passenger name and seat in the airline.
        // scoped now, same as BookingsController.GetAll: staff see everything,
        // a passenger sees only the tickets on their own bookings
        //
        // OrderBy sits INSIDE the ToDto call, on the entities - chaining it
        // onto the result would throw at runtime, see the note on ToDto
        return Ok(await ToDto(
            ScopeToCaller(_context.Tickets)
                .OrderBy(t => t.Flight.DepartureTime)
                .ThenBy(t => t.SeatNumber))
            .ToListAsync());
    }


    // ============ 6. GET by id ============
    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        // 404 rather than 403 for someone else's ticket, deliberately.
        // returning 403 for "exists but not yours" and 404 for "doesn't
        // exist" lets anyone map which ticket ids are real just by walking
        // the range - so a ticket you can't see is indistinguishable from
        // one that isn't there
        if (!await CanAccessTicket(id)) return NotFound();

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
        // scoped FIRST, before any of the filters below. passengerName in
        // particular was a people-search over the whole airline - anyone could
        // type a name and get that person's flights, dates and seat numbers
        var query = ScopeToCaller(_context.Tickets);

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