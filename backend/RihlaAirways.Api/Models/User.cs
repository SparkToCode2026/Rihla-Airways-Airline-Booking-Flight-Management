//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;

namespace RihlaAirways.Api.Models;

// this is the JWT anchor for the whole project - every protected endpoint
// eventually traces back to a row in this table
public class User
{
    // properties => attributes , get, ste => read, change
    public int Id { get; set; } // EF Core: "that's the PK, done"
    
    [Required] // not null 
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty; //An actual string object with a length of 0
    
    [Required]
    [MaxLength(150)]
    public string Email { get; set; } = string.Empty;
    
    
    // never the actual password - we store a BCrypt hash and compare hashes on login
    [Required]
    [MaxLength(255)]
    public string PasswordHash { get; set; } = string.Empty;
    
    
    // Passenger / Staff / Admin - a plain string for now because that's what
    // the spec asks for. a proper enum or a Roles table would be cleaner but
    // it complicates the JWT claim mapping and we don't need it for this scope
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "Passenger"; // default if nobody sets it
    
    // set once at registration and never touched again - UtcNow not Now,
    // because the docker sql container runs on UTC 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // --- navigation ---
    
    // one-to-one, and it's optional on purpose - a Staff or Admin user
    // has no passenger profile, so this stays null for them.
    public PassengerProfile? PassengerProfile { get; set; }
    
    
    // one-to-many the other direction.
    // one-to-many the other direction. initialized to an empty list so
    // user.Bookings.Add(...) never throws on a fresh object -
    // same pattern on every collection in this project
    public List<Booking> Bookings { get; set; } = new List<Booking>();
    
}