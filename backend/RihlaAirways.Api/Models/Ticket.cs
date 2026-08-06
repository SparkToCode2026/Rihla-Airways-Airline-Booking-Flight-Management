//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;
namespace RihlaAirways.Api.Models;
// one seat, on one flight, for one person. the busiest model in the schema -
// three FKs, because a ticket only means something at the intersection of
// a booking (who paid), a flight (which departure) and a seat class (what fare)
public class Ticket
{
    public int Id { get; set; }
    
    //FKs
    public int BookingId { get; set; }
    public int FlightId { get; set; }
    public int SeatClassId { get; set; }

    
    // nullable-ish in practice - a ticket can exist before check-in
    // assigns a seat, but keeping it required with an empty default is
    // simpler than handling nulls everywhere in the frontend
    [Required]
    [MaxLength(5)]
    public string SeatNumber { get; set; } = string.Empty; //E'g 12A
    
    public decimal Price { get; set; }
    
    // Booking -> User -> Name, but that gives us the CURRENT name.
    [Required]
    [MaxLength(150)]
    public string PassengerName { get; set; } = string.Empty;
    
    // --- navigation ---
    public Booking Booking { get; set; } = null!;
    public Flight Flight { get; set; } = null!;
    public SeatClass SeatClass { get; set; } = null!;

    public List<Baggage> Baggages { get; set; } = new List<Baggage>();
}