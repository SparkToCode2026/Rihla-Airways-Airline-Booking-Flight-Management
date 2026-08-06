//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;
namespace RihlaAirways.Api.Models;

// the ERD drew this as a weak entity (double rectangle,
// dashed partial key) but the mapping gave it a surrogate (replacement) Id instead.
// we follow the mapping - a plain int PK means every controller case
// takes one int instead of a composite key, much less friction.
// conceptually still weak though: TicketId is required, a bag can't
// exist without a ticket
public class Baggage
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    
    // BaggageNumber is int with a composite unique on (TicketId, BaggageNumber)
    public int BaggageNumber { get; set; } 
    
    public decimal WeightKg { get; set; }
    
    // CheckedIn / CarryOn / Oversized
    [Required]
    [MaxLength(30)]
    public string Type { get; set; } = string.Empty;
    
    
    // excess charge. note there's no FK to SeatClass here, but the two are
    // linked in logic: SeatClass.BaggageAllowanceKg is the free limit,
    // this is what you pay on the overage. the path is
    // Baggage -> Ticket -> SeatClass -> BaggageAllowanceKg,
    // which is a ThenInclude() when you need it
    public decimal Fee { get; set; }
    
    // --- navigation ---
    public Ticket Ticket { get; set; } = null!;
    
}