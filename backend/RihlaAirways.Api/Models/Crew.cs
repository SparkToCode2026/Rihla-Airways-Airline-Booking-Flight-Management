//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;

namespace RihlaAirways.Api.Models;
// pilots and cabin crew. deliberately separate from User -
// a crew member is airline staff being scheduled, not someone
// who logs in and books a seat. different lifecycle, different table
public class Crew
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    
    // Pilot / CoPilot / FlightAttendant - the person's job title.
    // Note: FlightCrew has a DutyRole too and they are NOT the same.
    // this one is permanent, DutyRole is per-flight (a pilot can be
    // scheduled as CoPilot on a given flight)
    [Required]
    [MaxLength(50)]
    public string Role { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(50)]
    public string LicenseNumber { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(50)]
    public string PassportNumber { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Nationality { get; set; } = string.Empty;
    
    // crew clear immigration like everyone else on international routes -
    // this is NOT duplication of PassengerProfile, it's the same real-world
    // document being tracked for a different reason. PassengerProfile exists
    // so a customer can be ticketed, this exists so a crew member can
    // legally enter the destination country
    public DateOnly DateOfBirth { get; set; }
    
    // --- navigation ---
    
    // note this is NOT List<Flight> - it points at the join entity.
    // that's the whole reason FlightCrew exists as its own class:
    // the relationship carries data (DutyRole), so we can't let EF Core
    // hide the join table.( many-to-many )
    // getting from a crew member to their flights means going
    // .Include(c => c.FlightCrews).ThenInclude(fc => fc.Flight)
    public List<FlightCrew> FlightCrews { get; set; } = new List<FlightCrew>();
}