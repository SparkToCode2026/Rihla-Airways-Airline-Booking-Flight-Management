//namespace DefaultNamespace;

using System.ComponentModel.DataAnnotations;
namespace RihlaAirways.Api.Models;

// one purchase transaction. a booking is the CONTAINER - it can hold
// several tickets (family booking, round trip), which is why Tickets
// is a list. the money lives on Payment, not here
public class Booking
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public DateTime BookingDate { get; set; } = DateTime.UtcNow;
    
    // Pending / Confirmed / Cancelled.
    //What an enum is: a fixed list of allowed values you define yourself
    // an enum is cleaner but adds friction with json
    // a proper enum or a Roles table would be cleaner but
    // it complicates the JWT claim mapping and we don't need it for this scope
    [Required] // NOT NULL in the database
    [MaxLength(20)]
    public string Status { get; set; } = "Pending"; // default if nobody sets it
    
    public decimal TotalAmount { get; set; }
    
    // --- navigation ---
    public User User { get; set; } = null!;
    
    public List<Ticket> Tickets { get; set; } = new List<Ticket>();
    
    
    
    // one-to-one and (nullable) =>  The reference can be null if the related object is missing.
    
    public Payment? Payment { get; set; }
}