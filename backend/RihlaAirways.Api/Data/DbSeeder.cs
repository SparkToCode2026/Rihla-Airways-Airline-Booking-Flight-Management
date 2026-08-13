using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Models;
using Route = RihlaAirways.Api.Models.Route;   // same alias reason as RoutesController

namespace RihlaAirways.Api.Data;

// seeds a full working dataset so every endpoint returns something real.
// without this, GET /stats returns empty arrays and the frontend has
// nothing to render - and you can't tell a working Include() from a
// broken one when both give you []
public static class DbSeeder
{
    // same formula TicketsController uses. duplicated on purpose rather than
    // shared - if the seeder called the controller's method we'd be testing
    // the seeder against itself. this way a mismatch shows up as a failing
    // integrity check, which is what we want
    private static decimal BaseFare(int distanceKm) => 20m + (distanceKm * 0.08m);

    public static async Task SeedAsync(AppDbContext db)
    {
        // runs on EVERY startup, unlike the bulk seed below which only runs
        // once on a fresh database. without this, a database that already
        // has data (from an earlier run, before an Admin ever existed) would
        // never get one - there's no self-service way to become Admin, by
        // design, so something has to guarantee at least one exists
        await EnsureAdminAsync(db);

        // guard - checking Airports specifically because it's the
        // first table we write. if anything exists we assume a previous run
        // finished and bail out. it means "dotnet run" is safe to
        // call repeatedly, which is what the team needs
        if (await db.SeatClasses.AnyAsync()) return;

        var now = DateTime.UtcNow;

        // ---------- layer 1: no FKs ----------

        var seatClasses = new List<SeatClass>
        {
            new() { Name = "Economy",  PriceMultiplier = 1.00m, BaggageAllowanceKg = 25 },
            new() { Name = "Business", PriceMultiplier = 2.50m, BaggageAllowanceKg = 40 },
            new() { Name = "First",    PriceMultiplier = 4.00m, BaggageAllowanceKg = 50 }
        };
        db.SeatClasses.AddRange(seatClasses);

        var airports = new List<Airport>
        {
            new() { Code = "MCT", Name = "Muscat International",        City = "Muscat",    Country = "Oman" },
            new() { Code = "SLL", Name = "Salalah International",       City = "Salalah",   Country = "Oman" },
            new() { Code = "DXB", Name = "Dubai International",         City = "Dubai",     Country = "United Arab Emirates" },
            new() { Code = "DOH", Name = "Hamad International",         City = "Doha",      Country = "Qatar" },
            new() { Code = "KWI", Name = "Kuwait International",        City = "Kuwait City", Country = "Kuwait" },
            new() { Code = "BOM", Name = "Chhatrapati Shivaji Maharaj", City = "Mumbai",    Country = "India" },
            new() { Code = "IST", Name = "Istanbul Airport",            City = "Istanbul",  Country = "Turkey" },
            new() { Code = "LHR", Name = "Heathrow",                    City = "London",    Country = "United Kingdom" }
        };
        // MCT may already exist from manual testing - filter it out so the
        // unique index on Code doesn't blow up mid-seed
        var existingCodes = await db.Airports.Select(a => a.Code).ToListAsync();
        airports = airports.Where(a => !existingCodes.Contains(a.Code)).ToList();
        db.Airports.AddRange(airports);

        var airplanes = new List<Airplane>
        {
            new() { Model = "Boeing 737-800",  RegistrationNumber = "A4O-BA", Capacity = 162, ManufactureYear = 2015 },
            new() { Model = "Boeing 737 MAX 8", RegistrationNumber = "A4O-MB", Capacity = 178, ManufactureYear = 2019 },
            new() { Model = "Airbus A330-300", RegistrationNumber = "A4O-DC", Capacity = 289, ManufactureYear = 2012 },
            new() { Model = "Embraer E175",    RegistrationNumber = "A4O-EE", Capacity = 88,  ManufactureYear = 2021 }
        };
        db.Airplanes.AddRange(airplanes);

        // 4 Pilot + 3 CoPilot so there are enough licence holders for the
        // cockpit-duty rule in FlightCrewsController - a Captain assignment
        // is rejected unless Crew.Role is Pilot or CoPilot
        var crews = new List<Crew>
        {
            new() { Name = "Khalid Al-Balushi", Role = "Pilot",     LicenseNumber = "OM-P-1001", PassportNumber = "OM8842011", Nationality = "Omani",   DateOfBirth = new DateOnly(1981, 3, 14) },
            new() { Name = "Aisha Al-Hinai",    Role = "Pilot",     LicenseNumber = "OM-P-1002", PassportNumber = "OM8842012", Nationality = "Omani",   DateOfBirth = new DateOnly(1985, 7, 2) },
            new() { Name = "Rashid Al-Amri",    Role = "Pilot",     LicenseNumber = "OM-P-1003", PassportNumber = "OM8842013", Nationality = "Omani",   DateOfBirth = new DateOnly(1979, 11, 30) },
            new() { Name = "Sara Fernandes",    Role = "Pilot",     LicenseNumber = "PT-P-2044", PassportNumber = "PT5510923", Nationality = "Portuguese", DateOfBirth = new DateOnly(1988, 1, 21) },
            new() { Name = "Yusuf Al-Rawahi",   Role = "CoPilot",   LicenseNumber = "OM-C-3001", PassportNumber = "OM8842014", Nationality = "Omani",   DateOfBirth = new DateOnly(1992, 5, 9) },
            new() { Name = "Priya Nair",        Role = "CoPilot",   LicenseNumber = "IN-C-3002", PassportNumber = "IN7729401", Nationality = "Indian",  DateOfBirth = new DateOnly(1990, 9, 17) },
            new() { Name = "Omar Haddad",       Role = "CoPilot",   LicenseNumber = "JO-C-3003", PassportNumber = "JO3320188", Nationality = "Jordanian", DateOfBirth = new DateOnly(1993, 2, 4) },
            new() { Name = "Maryam Al-Saidi",   Role = "Attendant", LicenseNumber = "OM-A-4001", PassportNumber = "OM8842015", Nationality = "Omani",   DateOfBirth = new DateOnly(1995, 6, 12) },
            new() { Name = "Layla Karim",       Role = "Attendant", LicenseNumber = "OM-A-4002", PassportNumber = "OM8842016", Nationality = "Omani",   DateOfBirth = new DateOnly(1996, 10, 25) },
            new() { Name = "Ana Sousa",         Role = "Attendant", LicenseNumber = "PT-A-4003", PassportNumber = "PT5510924", Nationality = "Portuguese", DateOfBirth = new DateOnly(1994, 4, 8) },
            new() { Name = "Daniel Okoye",      Role = "Attendant", LicenseNumber = "NG-A-4004", PassportNumber = "NG9911220", Nationality = "Nigerian", DateOfBirth = new DateOnly(1997, 12, 1) },
            new() { Name = "Imran Sheikh",      Role = "Engineer",  LicenseNumber = "PK-E-5001", PassportNumber = "PK4483001", Nationality = "Pakistani", DateOfBirth = new DateOnly(1983, 8, 19) }
        };
        db.Crews.AddRange(crews);

        // password for every seeded account is Rihla2026!
        // BCrypt is deliberately slow, so hashing once and reusing the same
        // hash keeps the seed from taking several seconds
        var sharedHash = BCrypt.Net.BCrypt.HashPassword("Rihla2026!");

        var users = new List<User>
        {
            new() { Name = "Nasser Al-Kindi", Email = "nasser@example.om", PasswordHash = sharedHash, Role = "Passenger", CreatedAt = now.AddDays(-60) },
            new() { Name = "Fatma Al-Zadjali", Email = "fatma@example.om", PasswordHash = sharedHash, Role = "Passenger", CreatedAt = now.AddDays(-45) },
            new() { Name = "Hamed Al-Mahrouqi", Email = "hamed@example.om", PasswordHash = sharedHash, Role = "Passenger", CreatedAt = now.AddDays(-30) },
            new() { Name = "Zainab Al-Farsi",  Email = "zainab@example.om", PasswordHash = sharedHash, Role = "Passenger", CreatedAt = now.AddDays(-12) },
            new() { Name = "Ops Desk",         Email = "ops@rihla.om",      PasswordHash = sharedHash, Role = "Staff",     CreatedAt = now.AddDays(-90) }
            // Admin isn't seeded here - EnsureAdminAsync above handles it on
            // every startup, not just a fresh database, so it works whether
            // this bulk seed runs or not
        };
        var existingEmails = await db.Users.Select(u => u.Email).ToListAsync();
        users = users.Where(u => !existingEmails.Contains(u.Email)).ToList();
        db.Users.AddRange(users);

        // first save - everything above has no FKs, so it can all go in one
        // round trip. after this the Ids are populated and layer 2 can
        // reference them
        await db.SaveChangesAsync();

        // re-read airports from the db rather than using the local list -
        // MCT might have come from an earlier manual POST, so the local
        // list could be missing it
        var apt = await db.Airports.ToDictionaryAsync(a => a.Code, a => a.Id);

        // ---------- layer 2: Route ----------

        // (origin, destination, km, minutes). distances are roughly real -
        // the fare formula keys off them so nonsense distances would give
        // nonsense prices
        var routeSpecs = new (string From, string To, int Km, int Min)[]
        {
            ("MCT", "SLL",  865,  95),
            ("SLL", "MCT",  865,  95),
            ("MCT", "DXB",  340,  75),
            ("DXB", "MCT",  340,  75),
            ("MCT", "DOH",  700, 100),
            ("MCT", "KWI", 1200, 140),
            ("MCT", "BOM", 1900, 165),
            ("BOM", "MCT", 1900, 165),
            ("MCT", "IST", 3100, 330),
            ("MCT", "LHR", 5800, 480)
        };

        var routes = routeSpecs
            .Select(s => new Route
            {
                OriginAirportId = apt[s.From],
                DestinationAirportId = apt[s.To],
                DistanceKm = s.Km,
                EstimatedDurationMin = s.Min
            })
            .ToList();
        db.Routes.AddRange(routes);
        await db.SaveChangesAsync();

        // ---------- layer 3: Flight ----------

        // built in a loop rather than hand-listed, so the airplane overlap
        // rule holds BY CONSTRUCTION. each aircraft flies one leg per day
        // and the next leg starts the following day - no chance of two
        // flights sharing a plane in the same window, which is exactly what
        // FlightsController.Create rejects
        var flights = new List<Flight>();
        var planeIds = airplanes.Select(a => a.Id).ToList();
        var flightNo = 200;

        for (int day = -10; day <= 14; day++)
        {
            var routeIndex = (day + 10) % routes.Count;
            var route = routes[routeIndex];
            var planeId = planeIds[(day + 10) % planeIds.Count];

            var departure = now.Date.AddDays(day).AddHours(6 + ((day + 10) % 3) * 5);
            var arrival = departure.AddMinutes(route.EstimatedDurationMin);

            // past flights are Landed, today/future are Scheduled. gives the
            // filter and stats endpoints a realistic mix instead of every
            // row having the same status
            var status = arrival < now ? "Landed"
                       : departure < now ? "Departed"
                       : "Scheduled";

            // one delayed and one cancelled so the state machine and the
            // status filter have something to find
            if (day == 3) status = "Delayed";
            if (day == 7) status = "Cancelled";

            flights.Add(new Flight
            {
                FlightNumber = $"RA{flightNo++}",
                RouteId = route.Id,
                AirplaneId = planeId,
                DepartureTime = departure,
                ArrivalTime = arrival,
                Status = status
            });
        }
        db.Flights.AddRange(flights);
        await db.SaveChangesAsync();

        // ---------- layer 4: profiles, bookings, tickets ----------

        var passengers = await db.Users.Where(u => u.Role == "Passenger").ToListAsync();

        var profiles = new List<PassengerProfile>();
        var passportSeed = 1000;
        foreach (var u in passengers)
        {
            // skip anyone who already has one - PassengerProfiles.UserId is
            // unique, that index is what makes the 1:1 a 1:1
            if (await db.PassengerProfiles.AnyAsync(p => p.UserId == u.Id)) continue;

            profiles.Add(new PassengerProfile
            {
                UserId = u.Id,
                PassportNumber = $"OM{passportSeed++}555",
                Nationality = "Omani",
                DateOfBirth = new DateOnly(1990 + (passportSeed % 8), ((passportSeed % 12) + 1), 15),
                PhoneNumber = $"+9689{passportSeed}123"
            });
        }
        db.PassengerProfiles.AddRange(profiles);
        await db.SaveChangesAsync();

        // bookings + tickets together, because Booking.TotalAmount is derived
        // from its tickets. that invariant is owned by TicketsController in
        // the running app, so the seeder has to honour it too or the
        // IntegrityCheck in BookingsController.GetStats will light up
        var bookableFlights = flights
            .Where(f => f.Status != "Cancelled")
            .ToList();

        var econ = seatClasses[0];
        var biz = seatClasses[1];
        var first = seatClasses[2];

        var rnd = new Random(2026);   // fixed seed = same data every time,
                                      // which makes bugs reproducible
        var bookings = new List<Booking>();
        var tickets = new List<Ticket>();
        var seatCounter = new Dictionary<int, int>();   // flightId -> next seat row

        for (int i = 0; i < 18; i++)
        {
            var user = passengers[i % passengers.Count];
            var flight = bookableFlights[rnd.Next(bookableFlights.Count)];
            var route = routes.First(r => r.Id == flight.RouteId);

            var booking = new Booking
            {
                UserId = user.Id,
                BookingDate = flight.DepartureTime.AddDays(-rnd.Next(3, 25)),
                Status = "Pending",
                TotalAmount = 0m
            };
            bookings.Add(booking);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();   // need the booking Id for the tickets

            // 1-3 tickets per booking so the ticket-count aggregates aren't
            // all 1
            var ticketCount = rnd.Next(1, 4);
            decimal total = 0m;

            for (int t = 0; t < ticketCount; t++)
            {
                var sc = rnd.Next(10) switch
                {
                    < 7 => econ,      // most people fly economy
                    < 9 => biz,
                    _   => first
                };

                // seat numbers have to be unique per flight -
                // UX_Ticket_FlightSeat enforces it. a running counter per
                // flight guarantees no collision without needing a lookup
                if (!seatCounter.ContainsKey(flight.Id)) seatCounter[flight.Id] = 1;
                var row = seatCounter[flight.Id]++;
                var seat = $"{row}{(char)('A' + (row % 6))}";

                var price = Math.Round(BaseFare(route.DistanceKm) * sc.PriceMultiplier, 2);
                total += price;

                tickets.Add(new Ticket
                {
                    BookingId = booking.Id,
                    FlightId = flight.Id,
                    SeatClassId = sc.Id,
                    SeatNumber = seat,
                    Price = price,
                    PassengerName = user.Name
                });
            }

            booking.TotalAmount = total;
        }

        db.Tickets.AddRange(tickets);
        await db.SaveChangesAsync();

        // ---------- layer 5: payments, baggage, crew rosters ----------

        var payments = new List<Payment>();
        foreach (var b in bookings)
        {
            var roll = rnd.Next(10);

            // leave a couple unpaid so the unpaidOnly filter in
            // BookingsController has something to return
            if (roll == 0) continue;

            var status = roll switch
            {
                1 => "Pending",
                2 => "Failed",
                _ => "Completed"
            };

            payments.Add(new Payment
            {
                BookingId = b.Id,
                // must equal TotalAmount - PaymentsController.Create rejects
                // a mismatch, so seeding one would create data the API
                // itself would refuse
                Amount = b.TotalAmount,
                PaymentDate = b.BookingDate.AddMinutes(rnd.Next(2, 90)),
                Method = roll % 3 == 0 ? "Card" : roll % 3 == 1 ? "Cash" : "BankTransfer",
                Status = status
            });

            // a completed payment confirms the booking - same rule
            // PaymentsController applies when status flips to Completed
            if (status == "Completed") b.Status = "Confirmed";
        }
        db.Payments.AddRange(payments);

        var baggages = new List<Baggage>();
        foreach (var t in tickets)
        {
            var bagCount = rnd.Next(0, 3);
            var sc = seatClasses.First(s => s.Id == t.SeatClassId);
            decimal runningChecked = 0m;

            for (int n = 1; n <= bagCount; n++)
            {
                var weight = Math.Round((decimal)(rnd.Next(80, 320) / 10.0), 2);
                var type = n == 1 ? "Checked" : rnd.Next(4) == 0 ? "Oversized" : "CarryOn";

                // same fee logic as BaggagesController - allowance is per
                // TICKET, so only the weight past the allowance is charged
                decimal fee = 0m;
                if (type != "CarryOn")
                {
                    var surcharge = type == "Oversized" ? 25m : 0m;
                    var after = runningChecked + weight;
                    fee = after <= sc.BaggageAllowanceKg
                        ? surcharge
                        : Math.Round(surcharge + ((after - Math.Max(runningChecked, sc.BaggageAllowanceKg)) * 5m), 2);
                    runningChecked = after;
                }

                baggages.Add(new Baggage
                {
                    TicketId = t.Id,
                    // per-ticket sequence, not a global number - this is the
                    // partial key of the weak entity, scoped by the
                    // (TicketId, BaggageNumber) unique index
                    BaggageNumber = n,
                    WeightKg = weight,
                    Type = type,
                    Fee = fee
                });
            }
        }
        db.Baggages.AddRange(baggages);

        // crew rosters. built to satisfy every rule in FlightCrewsController:
        // exactly one Captain per flight, cockpit duties only for Pilot or
        // CoPilot job titles, and no crew member on two overlapping flights.
        // the last one holds because flights are one-per-day per aircraft and
        // we rotate the crew pool by flight index
        var pilots = crews.Where(c => c.Role == "Pilot").ToList();
        var coPilots = crews.Where(c => c.Role == "CoPilot").ToList();
        var attendants = crews.Where(c => c.Role == "Attendant").ToList();

        var rosters = new List<FlightCrew>();
        for (int i = 0; i < flights.Count; i++)
        {
            var f = flights[i];
            if (f.Status == "Cancelled") continue;   // no roster on a cancelled flight

            rosters.Add(new FlightCrew { FlightId = f.Id, CrewId = pilots[i % pilots.Count].Id,        DutyRole = "Captain" });
            rosters.Add(new FlightCrew { FlightId = f.Id, CrewId = coPilots[i % coPilots.Count].Id,    DutyRole = "FirstOfficer" });
            rosters.Add(new FlightCrew { FlightId = f.Id, CrewId = attendants[i % attendants.Count].Id, DutyRole = "PurserCabinCrew" });
            rosters.Add(new FlightCrew { FlightId = f.Id, CrewId = attendants[(i + 1) % attendants.Count].Id, DutyRole = "CabinCrew" });
        }
        db.FlightCrews.AddRange(rosters);

        await db.SaveChangesAsync();
    }

    // guarantees at least one Admin login exists, on every startup - not just
    // the first one. there is deliberately no self-service way to become
    // Admin (self-registration always creates a Passenger, and only an
    // existing Admin can create another Admin/Staff account through
    // UsersController), which means a database that reached this point
    // without one already existing would be stuck forever with no way in.
    //
    // if a "admin@rihla.om" row already exists - e.g. someone registered
    // that email as a Passenger through the Register page before this ran -
    // it gets promoted in place instead of creating a duplicate, since Email
    // is a unique index and a second insert would throw
    private static async Task EnsureAdminAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync(u => u.Role == "Admin")) return;

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == "admin@rihla.om");
        if (existing != null)
        {
            existing.Role = "Admin";
        }
        else
        {
            db.Users.Add(new User
            {
                Name = "System Admin",
                Email = "admin@rihla.om",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Rihla2026!"),
                Role = "Admin",
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }
}