//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;

namespace RihlaAirways.Api.Models;

// Economy / Business / First.
public class SeatClass
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;
    
    // Economy 1.0, Business 2.5, First 4.0 - final ticket price is
    // base fare * this. decimal not double, because double does binary
    // floating point and 0.1 + 0.2 stops equalling 0.3 the moment
    // money is involved. precision gets set in the DbContext
    public decimal PriceMultiplier { get; set; }
    
    // free baggage allowance in kg. Baggage.Fee is charged on the excess -
    // that's the link between this table and Baggage even though there's
    // no FK between them. not in the ERD, added during mapping - new fix
    public int BaggageAllowanceKg { get; set; }
    
    // --- navigation ---
    public List<Ticket> Tickets { get; set; } = new List<Ticket>();
    
}