# Rihla Airways ✈

**Airline Booking & Flight Management System**
Spark to Code 2026 — Final Capstone · Project 11

A full-stack airline management system: ASP.NET Core Web API with EF Core
Code-First, SQL Server, JWT authentication, MailKit email notifications, and a
Bootstrap frontend.

---

## Tech stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core Web API (.NET 10) |
| ORM | Entity Framework Core 10 — Code-First |
| Database | SQL Server 2022 (Docker container) |
| Auth | JWT Bearer tokens, BCrypt password hashing |
| Email | MailKit / SMTP |
| Docs | Swagger (Swashbuckle) + Postman collection |
| Frontend | HTML, CSS, JavaScript, Bootstrap 5 |

---

## Running the project

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- A code editor — JetBrains Rider or Visual Studio for the backend,
  VS Code with the **Live Server** extension for the frontend

### 1. Start the database

From the repository root:

```bash
docker compose up -d
```

This starts SQL Server 2022 on port 1433 in a container named `rihla-sqlserver`.
Data persists between restarts via a named volume.

Verify it's running:

```bash
docker ps
```

### 2. Run the API

```bash
cd backend/RihlaAirways.Api
dotnet run --launch-profile http
```

The API starts on **http://localhost:5251**.

On first run it applies migrations and seeds the database automatically — 8
airports, 4 airplanes, 12 crew, 3 seat classes, 10 routes, 25 flights, and a set
of bookings, tickets, payments and baggage. The seeder is idempotent, so
restarting won't duplicate anything.

Swagger UI: **http://localhost:5251/swagger**

> **Opening the solution:** open `RihlaAirways.slnx`, not the folder. Opening the
> folder puts Rider/VS in file-system mode with no project model, and every symbol
> shows "cannot resolve" even though `dotnet build` succeeds.

### 3. Run the frontend

Open `frontend/` in VS Code, right-click `html/login.html` → **Open with Live
Server**. It serves on `http://localhost:5500`.

Opening the HTML file directly (`file://`) will not work — browsers block `fetch`
from `file://` origins, so every API call fails on CORS before it leaves the page.

---

## Test accounts

All seeded accounts use the password `Rihla2026!`

| Email | Role |
|---|---|
| `safa@rihla.om` | Admin |
| `ops@rihla.om` | Staff |
| `nasser@example.om` | Passenger |
| `fatma@example.om` | Passenger |
| `hamed@example.om` | Passenger |
| `zainab@example.om` | Passenger |

Registering a new account through the UI always creates a **Passenger** — the role
is set server-side and is not accepted from the client. Staff and Admin accounts
are created by an existing Admin via `POST /api/users`.

---

## Entity Relationship Diagram

![ERD](docs/Airline_ERD_Chen.png)

### Mapping notes

**13 entities**, including the `FlightCrew` junction table.

All five relationship types are present:

| Type | Example |
|---|---|
| One-to-many | `User → Booking`, `Flight → Ticket`, `Route → Flight` |
| One-to-one | `User → PassengerProfile`, `Booking → Payment` |
| Many-to-many with payload | `Flight ↔ Crew` via `FlightCrew` (payload: `DutyRole`) |
| Two FKs to one table | `Route → Airport` (origin and destination) |
| Weak entity | `Baggage`, owned by `Ticket` |

**Decisions worth noting:**

- **`Baggage` is a weak entity** in the conceptual model — a bag cannot exist
  without its ticket. It is *mapped* with a surrogate primary key plus a unique
  index on `(TicketId, BaggageNumber)`, which preserves the partial-key identity
  while keeping single-column keys for controller routes. `BaggageNumber` is a
  per-ticket sequence assigned server-side.

- **`Route` points at `Airport` twice.** EF Core cannot resolve which navigation
  belongs to which FK by convention, so `Route.cs` uses `[ForeignKey]` and
  `[InverseProperty]` to disambiguate. Both FKs use `DeleteBehavior.Restrict` —
  cascading on both would produce a "multiple cascade paths" error and SQL Server
  would reject the migration.

- **`Ticket → Booking/Flight`** is the same class of problem: `Booking` cascades,
  `Flight` restricts. A flight with sold tickets is cancelled, not deleted.

- **`PassengerProfile.Age`** is `[NotMapped]` — derived from `DateOfBirth`, never
  stored. Because it has no column, it cannot be used in a `Where` or a `Select`
  that EF Core translates; age filters convert the bound to a date instead.

- **`Crew` is deliberately not linked to `User`.** A crew member is a schedulable
  resource, not an account holder. Crew carry travel documents because they clear
  immigration on international routes.

- **Derived values are server-owned.** `Ticket.Price` is calculated from route
  distance × seat-class multiplier. `Booking.TotalAmount` is written only by
  `TicketsController`. `Baggage.Fee` is calculated against
  `SeatClass.BaggageAllowanceKg`. None are accepted from the client.
  `GET /api/bookings/stats` includes an integrity check proving
  `TotalAmount == SUM(Ticket.Price)` across all bookings.

Full relational schema: [`docs/rihla_airways_schema.dbml`](docs/rihla_airways_schema.dbml)
(paste into [dbdiagram.io](https://dbdiagram.io) to view).

---

## API overview

**14 controllers · 112 endpoints.** Every controller implements at least the
8 required cases:

| # | Case | Example |
|---|---|---|
| 1 | POST create | `POST /api/airports` |
| 2 | PUT update | `PUT /api/airports/{id}` |
| 3 | PATCH second update | `PATCH /api/flights/{id}/status` |
| 4 | DELETE | `DELETE /api/airports/{id}` |
| 5 | GET list (cross-entity) | `GET /api/flights` |
| 6 | GET by id | `GET /api/airports/{id}` |
| 7 | GET filter (LINQ `Where`) | `GET /api/flights/filter?originCode=MCT` |
| 8 | GET sort / aggregate | `GET /api/bookings/stats` |

Plus `AuthController` — `register`, `login`, `me`.

### Business rules enforced

The API returns `409 Conflict` with an explanatory message, rather than an
unhandled `500`, for:

- Selling a seat already taken on a flight
- Selling beyond the aircraft's capacity
- Scheduling an aircraft on two overlapping flights
- Rostering a crew member on two overlapping flights
- Assigning a non-pilot to a cockpit duty, or a second Captain to one flight
- Deleting an airport, route, airplane, seat class or crew member still in use
- Invalid status transitions (a Landed flight cannot return to Scheduled; a
  Refunded payment cannot un-refund)
- Paying an amount that doesn't match the booking total

### Email notifications

Both spec-required triggers are implemented in `Services/EmailService.cs`:

| Trigger | Fires when |
|---|---|
| Booking confirmation (e-ticket) | `PATCH /api/payments/{id}/status` → `Completed` |
| Flight status notice | `PATCH /api/flights/{id}/status` → `Delayed` or `Cancelled` |

The flight notice fans out to every passenger holding a ticket on that flight,
deduplicated by booking.

Emails send via **Mailtrap sandbox** — a testing SMTP server that captures mail in
a web inbox rather than delivering it. Nothing reaches a real recipient. Set
`"Email:Enabled": false` in `appsettings.Development.json` to disable sending
entirely; the service logs what it would have sent.

> Mail sending is fire-and-forget with internal error handling: a mail failure is
> logged and never fails the payment or flight update it was triggered by. A
> production system would queue these and retry; that was scoped out deliberately.

---

## Postman

The collection lives in [`postman/`](postman/). Import it, then:

1. Collection → **Variables** → set `baseUrl` to `http://localhost:5251`
2. Collection → **Authorization** → Bearer Token → `{{token}}`
3. Run the **login** request — its post-response script stores the token in
   `{{token}}`, and every other request inherits it

---

## Project structure

```
rihla-airways/
├── backend/RihlaAirways.Api/
│   ├── Controllers/        14 controllers
│   ├── Models/             13 EF Core entities
│   ├── Data/               AppDbContext, DbSeeder
│   ├── Services/           TokenService, EmailService
│   ├── Migrations/
│   └── Program.cs
├── frontend/
│   ├── html/               login, register, model pages
│   ├── css/style.css
│   └── js/                 api.js, auth.js, page scripts
├── docs/                   ERD, DBML, mapping notes
├── postman/                API collection
└── docker-compose.yml
```

---

## A note on credentials in this repository

The SQL Server password, JWT signing key and Mailtrap credentials are committed in
`docker-compose.yml` and `appsettings.Development.json`. This is deliberate and
scoped to local development:

- The database runs only in a local Docker container and is not exposed beyond
  `localhost:1433`
- The JWT key signs tokens for a local API with no external consumers
- Mailtrap is a sandbox that cannot deliver mail to real recipients

Committing them means any team member can clone the repository and run the full
system without a setup handover. In a deployed environment these would move to
environment variables or a secrets manager, and the JWT key would be rotated —
`appsettings.json` (non-Development) contains none of them for that reason.

---

## Team

| Developer | Contribution |
|---|---|
| **Safa Al-Khamiasi** *(Team Lead)* | ERD, mapping and DBML · all 13 models and `AppDbContext` · migrations · review and correction of all 14 controllers · JWT authentication · MailKit email service and both triggers · seed data · Postman collection · frontend scaffolding (API layer, auth flow, shared layout, login/register) · project coordination |
| **Hamza** | Initial implementation of all 14 controllers · full frontend build and testing |

The project was assigned to six developers. Two delivered it. One further team
member was excused for a documented family medical emergency. The Git history
records the contribution of each participant.

---

## Known limitations

- Email sending is synchronous and fire-and-forget; a production system would use
  a background queue with retries.
- Mailtrap's free tier rate-limits sends, so a fan-out notification paces itself
  with a short delay between messages.
- `Ticket.Price` uses a simple distance-based fare. Real airline pricing accounts
  for demand, season and booking lead time.
- Crew deadheading (crew travelling as passengers) is not modelled. It would
  require a nullable `Crew.UserId`; scoped out deliberately.
- Status fields are strings rather than enums, so validity is enforced in the
  controllers rather than by the type system. This keeps JSON and query filters
  simple at the cost of compile-time safety.

---

**Repository:** `SparkToCode2026/Rihla-Airways-Airline-Booking-Flight-Management`