using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Models;

// edge case first - this alias is not optional. the Web SDK turns on implicit
// usings, which pulls in Microsoft.AspNetCore.Routing, and that namespace has
// its own Route class. without this line "DbSet<Route>" is ambiguous and the
// file won't compile. same fix will be needed in RoutesController later
using Route = RihlaAirways.Api.Models.Route;

namespace RihlaAirways.Api.Data;

// this is the bridge between our c# code and the sql server container
public class AppDbContext : DbContext
{
    // the constructor
    // the options come from Program.cs where we pass in the connection string -
    // we never hardcode the connection here, that's what appsettings is for
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // --- DbSets: one per model, 13 total ---
    // these are PROPERTIES on the class, not statements inside the constructor.
    // each one becomes a table, and it's also what you query against:
    // _context.Flights.Where(...) only exists because of the line below.
    // "= null!" tells the compiler EF Core assigns these at runtime so we
    // don't get a nullable warning on every single one

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<PassengerProfile> PassengerProfiles { get; set; } = null!;
    public DbSet<Airport> Airports { get; set; } = null!;
    public DbSet<Route> Routes { get; set; } = null!;
    public DbSet<Airplane> Airplanes { get; set; } = null!;
    public DbSet<Flight> Flights { get; set; } = null!;
    public DbSet<Booking> Bookings { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<SeatClass> SeatClasses { get; set; } = null!;
    public DbSet<Ticket> Tickets { get; set; } = null!;
    public DbSet<Baggage> Baggages { get; set; } = null!;
    public DbSet<Crew> Crews { get; set; } = null!;
    public DbSet<FlightCrew> FlightCrews { get; set; } = null!;

 
   /*
Job 3 — OnModelCreating
 here's why it exists:
Attributes like [Required] and [MaxLength(150)] sit on a single property. But some rules involve more than one property, or two tables:
"Email must be unique" → about the table, not the property
"Delete a booking → delete its tickets" → about two tables
"(FlightId, SeatNumber) unique together" → about two properties
*/ 
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ============================================================
        // IDENTITY
        // ============================================================

        modelBuilder.Entity<User>(entity =>
        {
            // [Required] only means "not null" - it does NOT stop duplicate
            // emails. this index is the actual thing that makes login as Safa
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<PassengerProfile>(entity =>
        {
            // THIS is what makes the 1:1 a 1:1. without it EF Core happily
            // creates 5 profiles for the same UserId and you get a silent 1:M
            entity.HasIndex(p => p.UserId).IsUnique();

            entity.HasOne(p => p.User)
                  .WithOne(u => u.PassengerProfile)
                  .HasForeignKey<PassengerProfile>(p => p.UserId)
                  .OnDelete(DeleteBehavior.Cascade); // parent deleted → child deleted too
            // cascade is right here - a profile is meaningless without its user
        });


        // ============================================================
        // NETWORK & FLEET
        // ============================================================

        modelBuilder.Entity<Airport>(entity =>
        {
            entity.HasIndex(a => a.Code).IsUnique();
        });

        modelBuilder.Entity<Route>(entity =>
        {
            // one route row per airport pair - stops three duplicate MCT-DXB rows
            entity.HasIndex(r => new { r.OriginAirportId, r.DestinationAirportId })
                  .IsUnique();

            // CASCADE PATH SQL is a chain of connected foreign key rules 
            //When you change or delete a parent row, the database automatically updates or deletes matching rows in other tables down the path.
            // #1. two FKs into the same table - if both cascaded,
            // sql server sees two delete routes into Routes and rejects the
            // whole migration with "may cause cycles or multiple cascade paths".
            // Restrict is also just correct: closing an airport must not
            // silently erase every route through it.
            // note the [InverseProperty] attributes in Route.cs already told
            // EF Core WHICH nav goes with which FK - this part is only about
            // what happens on delete
            entity.HasOne(r => r.OriginAirport)
                  .WithMany(a => a.DepartingRoutes)
                  .HasForeignKey(r => r.OriginAirportId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.DestinationAirport)
                  .WithMany(a => a.ArrivingRoutes)
                  .HasForeignKey(r => r.DestinationAirportId)
                  .OnDelete(DeleteBehavior.Restrict); // parent can't be deleted while children exist
        });

        modelBuilder.Entity<Airplane>(entity =>
        {
            // the tail number identifies the individual aircraft.
            // Model is NOT unique - many planes share "Boeing 737-800"
            entity.HasIndex(a => a.RegistrationNumber).IsUnique();
        });

        modelBuilder.Entity<Flight>(entity =>
        {
            // FlightNumber alone is not unique - RA204 flies again tomorrow.
            // the PAIR is what has to be unique
            entity.HasIndex(f => new { f.FlightNumber, f.DepartureTime })
                  .IsUnique();

            // not unique, just fast - these two get filtered on constantly
            // (departure board, reminder emails, status filter case)
            entity.HasIndex(f => f.DepartureTime);
            entity.HasIndex(f => f.Status);

            // retiring an airplane or closing a route must not delete
            // the flight history that references them
            entity.HasOne(f => f.Route)
                  .WithMany(r => r.Flights)
                  .HasForeignKey(f => f.RouteId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(f => f.Airplane)
                  .WithMany(a => a.Flights)
                  .HasForeignKey(f => f.AirplaneId)
                  .OnDelete(DeleteBehavior.Restrict);
        });


        // ============================================================
        // BOOKING & REVENUE
        // ============================================================

        modelBuilder.Entity<Booking>(entity =>
        {
            // undeclared decimals get a build warning and silent truncation
            entity.Property(b => b.TotalAmount).HasPrecision(18, 2);

            entity.HasIndex(b => b.Status);

            // restrict, not cascade - deleting a user must not wipe the
            // financial history. if we ever need user deletion it's a
            // soft-delete flag, not a real DELETE
            entity.HasOne(b => b.User)
                  .WithMany(u => u.Bookings)
                  .HasForeignKey(b => b.UserId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(p => p.Amount).HasPrecision(18, 2);

            // same job as the PassengerProfile index - this is what
            // enforces one payment per booking
            entity.HasIndex(p => p.BookingId).IsUnique();

            entity.HasOne(p => p.Booking)
                  .WithOne(b => b.Payment)
                  .HasForeignKey<Payment>(p => p.BookingId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SeatClass>(entity =>
        {
            // (5,2) not (18,2) - a multiplier is like 2.50, it never needs
            // 18 digits. sizing it honestly documents the intent
            entity.Property(s => s.PriceMultiplier).HasPrecision(5, 2);

            entity.HasIndex(s => s.Name).IsUnique();
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.Property(t => t.Price).HasPrecision(18, 2);

            // the constraint that stops us selling seat 14C twice on the
            // same flight. without this nothing in the system prevents it
            entity.HasIndex(t => new { t.FlightId, t.SeatNumber }).IsUnique();

            // cascade: tickets belong to the booking's lifecycle
            entity.HasOne(t => t.Booking)
                  .WithMany(b => b.Tickets)
                  .HasForeignKey(t => t.BookingId)
                  .OnDelete(DeleteBehavior.Cascade);

            // CASCADE PATH #2. Ticket already cascades from Booking above,
            // so a second cascade from Flight is what sql server rejects.
            // and semantically Restrict is what we want anyway - a flight
            // with sold tickets gets Status = "Cancelled", it does not
            // get deleted out of the database
            entity.HasOne(t => t.Flight)
                  .WithMany(f => f.Tickets)
                  .HasForeignKey(t => t.FlightId)
                  .OnDelete(DeleteBehavior.Restrict);

            // lookup table - never deleted out from under live tickets
            entity.HasOne(t => t.SeatClass)
                  .WithMany(s => s.Tickets)
                  .HasForeignKey(t => t.SeatClassId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Baggage>(entity =>
        {
            // weight is (6,2) - 9999.99 kg is already absurd for one bag,
            // no reason to size it like money
            entity.Property(b => b.WeightKg).HasPrecision(6, 2);
            entity.Property(b => b.Fee).HasPrecision(18, 2);

            // THIS is the weak entity. Baggage is conceptually weak - a bag
            // can't exist without its ticket - but we gave it a surrogate Id
            // so controller routes stay single-key. this composite index is
            // what preserves the partial-key identity: ticket 5 and ticket 6
            // can both have a bag 1, but ticket 5 can't have two bag 1s
            entity.HasIndex(b => new { b.TicketId, b.BaggageNumber }).IsUnique();

            entity.HasOne(b => b.Ticket)
                  .WithMany(t => t.Baggages)
                  .HasForeignKey(b => b.TicketId)
                  .OnDelete(DeleteBehavior.Cascade);
            // cascade: the bag dies with its ticket, that's what "weak" means
        });


        // ============================================================
        // CREW
        // ============================================================

        modelBuilder.Entity<Crew>(entity =>
        {
            entity.HasIndex(c => c.LicenseNumber).IsUnique();
        });

        modelBuilder.Entity<FlightCrew>(entity =>
        {
            // no double-assigning the same person to the same flight.
            // this index is doing the job a composite PK would have done -
            // we chose a surrogate Id instead so the controller can take
            // one int instead of passing two keys around
            entity.HasIndex(fc => new { fc.FlightId, fc.CrewId }).IsUnique();

            entity.HasOne(fc => fc.Flight)
                  .WithMany(f => f.FlightCrews)
                  .HasForeignKey(fc => fc.FlightId)
                  .OnDelete(DeleteBehavior.Cascade);

            // restrict on the Crew side - when someone leaves the airline
            // we keep the historical roster of who flew what.
            // note Flight and Crew are unrelated parents so there's no shared
            // cascade path here, unlike the Ticket case above
            entity.HasOne(fc => fc.Crew)
                  .WithMany(c => c.FlightCrews)
                  .HasForeignKey(fc => fc.CrewId)
                  .OnDelete(DeleteBehavior.Restrict);

        });
    } 
}