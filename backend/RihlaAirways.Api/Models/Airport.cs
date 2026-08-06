//namespace DefaultNamespace;
namespace RihlaAirways.Api.Models;
using System.ComponentModel.DataAnnotations;
// pure lookup table - airports exist independently of everything else,
// which is why this one has no FKs at all
public class Airport
{
    public int Id { get; set; }
    
    // IATA code - MCT, DXB, LHR. always 3 chars, always uppercase.
    [Required]
    [MaxLength(3)]
    public string Code { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(100)]
    public string City { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;
    
    // --- navigation ---
    
    
    
    // edge case first, because this is the part that surprises people:
    // Route points at Airport TWICE (origin + destination), so EF Core
    // cannot figure out on its own which collection belongs to which FK.
    // two separate collections + [InverseProperty] on the Route side.
    // [InverseProperty] =>explicitly links pairs of navigation properties
    // when multiple relationships exist between the same two entities.
    public List<Route> DepartingRoutes { get; set; } = new List<Route>();
    public List<Route> ArrivingRoutes { get; set; } = new List<Route>();
    
}