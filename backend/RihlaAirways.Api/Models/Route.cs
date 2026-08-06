//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace RihlaAirways.Api.Models;

// a reusable path between two airports (like MCT to DXB)
// the distinction matters: one Route has many Flights
//which is why Flights is a collection down below

public class Route
{
    public int Id { get; set; }
    
    
    // edge case first, this is the tricky bit of the whole schema:
    // TWO FKs pointing at the SAME table. EF Core cannot guess which
    // navigation property belongs to which FK - by convention it matches
    // on name, and both "OriginAirport" and "DestinationAirport" are
    // equally valid candidates for both collections on Airport.
    // so we spell it out with [InverseProperty] on the nav props below.
    // without those two attributes the migration either fails or wires
    // the FKs to the wrong collections and you get very confusing data
    public int OriginAirportId { get; set; }
    public int DestinationAirportId { get; set; }
    
    
    public int DistanceKm { get; set; }
    
    // nt keeps simple when we compute arrival estimates
    public int EstimatedDurationMin { get; set; }
    
    // --- navigation ---
    
    // [InverseProperty] points at the collection on the OTHER side that
    // this nav belongs to. read it as: "my OriginAirport's DepartingRoutes
    // collection is where I live". compare the two lines - the ONLY
    // difference is which collection each one claims
    [ForeignKey(nameof(OriginAirportId))]
    [InverseProperty(nameof(Airport.DepartingRoutes))]
    public Airport OriginAirport { get; set; } = null!;
    
    [ForeignKey(nameof(DestinationAirportId))]
    [InverseProperty(nameof(Airport.ArrivingRoutes))]
    public Airport DestinationAirport { get; set; } = null!;

    public List<Flight> Flights { get; set; } = new List<Flight>();

    
}

// Rule:
/*
 FK is RouteId — that's the real SQL column. 
 Route is just a C# convenience so you can write flight.Route.DistanceKm.
 E'g => public Route Route { get; set; } = null!;   // navigation — the object in C#
 
[ForeignKey] only when two navigations point at the same class. Like Route => Airport (origin + destination) and nothing else.
 */