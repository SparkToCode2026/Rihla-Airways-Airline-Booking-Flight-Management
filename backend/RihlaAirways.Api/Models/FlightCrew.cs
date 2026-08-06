//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;
namespace RihlaAirways.Api.Models;
//(M:N)
// the join between Flight and Crew. this exists as its own class
public class FlightCrew
{
    // surrogate PK, not a composite (FlightId, CrewId) key.
    public int Id { get; set; }
    
    public int FlightId { get; set; }
    public int CrewId { get; set; }

    // Note: this is NOT the same as Crew.Role.
    // Crew.Role is the permanent job title (Pilot, FlightAttendant).
    // DutyRole is what they're doing on THIS flight, so a Pilot can be
    // rostered as CoPilot on a given leg. that difference is the entire
    // reason this table has a payload and can't be a plain many-to-many
    [Required]
    [MaxLength(50)]
    public string DutyRole { get; set; } = string.Empty;

    // --- navigation ---
    public Flight Flight { get; set; } = null!;
    public Crew Crew { get; set; } = null!;
    
    
}