//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;
namespace RihlaAirways.Api.Models;

public class Flight
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(10)]
    public string FlightNumber { get; set; } = string.Empty; // e'g RA204
    
    public int RouteId { get; set; }  // the FK — the actual column in SQL
    
    public int AirplaneId { get; set; }
    
    public DateTime DepartureTime { get; set; }
    public DateTime ArrivalTime { get; set; }
    
    // Scheduled / Delayed / Cancelled / Departed / Arrived.
    // same string-not-enum reasoning as Booking.Status.
    // this field is the trigger for the flight-status email later,
    // and it's what the filter + status-change controller cases hang off
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Scheduled";

    // --- navigation ---
    public Route Route { get; set; } = null!;
    public Airplane Airplane { get; set; } = null!;
    
    // both plain one-to-many, no attributes needed - one FK each,
    // names match convention, EF Core wires them up on its own.
    // compare to Route.cs where two FKs hit the same table and we
    // had to spell everything out with [InverseProperty]
    
    
    public List<Ticket> Tickets { get; set; } = new List<Ticket>();
    
    // join entity again, not List<Crew> - because the assignment
    // carries DutyRole. mirror image of Crew.FlightCrews
    public List<FlightCrew> FlightCrews { get; set; } = new List<FlightCrew>();
}