//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;
namespace RihlaAirways.Api.Models;

// the physical aircraft. one airplane flies many flights over time,
// so Flights is a collection - not a single Flight
public class Airplane
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string Model { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(20)]
    public string RegistrationNumber { get; set; } = string.Empty;
    
    // total seats. worth knowing for later: when we validate a booking we
    // compare tickets-sold against this number, so a wrong value here
    // silently lets you oversell a flight
    public int Capacity { get; set; }
    
    
    // int not DateTime - we only ever care about the year, and storing a
    // full date would force us to invent a fake month and day
    public int ManufactureYear { get; set; }
    
    // --- navigation ---
    public List<Flight> Flights { get; set; } = new List<Flight>();
    
}