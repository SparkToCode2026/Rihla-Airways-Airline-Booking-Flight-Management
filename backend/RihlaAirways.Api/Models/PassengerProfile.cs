//namespace DefaultNamespace;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace RihlaAirways.Api.Models;


public class PassengerProfile
{
    public int Id { get; set; }
    
    // this is the one-to-one. the FK lives HERE and not on User (total)
    //a User can exist without a profile, a profile can never exist without a User
    
    public int UserId { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string PassportNumber { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(100)]
    public string Nationality { get; set; } = string.Empty;
    
    
    // DateOnly not DateTime 
    public DateOnly DateOfBirth { get; set; }
    
    [Required]
    [MaxLength(20)]
    public string PhoneNumber { get; set; } = string.Empty;
    
    // this is the dotted oval from the ERD - a DERIVED attribute.
    // [NotMapped] means EF Core skips it entirely, no column, no migration.
    // storing age as a column would be wrong because it goes stale every
    // single birthday and nothing in the system would ever update it.
    // note : if their birthday hasn't happened yet this year,
    // subtracting the years alone overshoots by one
    [NotMapped]
    public int Age
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var age = today.Year - DateOfBirth.Year;
            if (DateOfBirth > today.AddYears(-age)) age--;
            return age;
        }
    }
    
    // --- navigation ---

    // singular, not a list - this is the other end of the one-to-one.
    // compare to User.PassengerProfile which is nullable (User? here is not).
    // a profile row without a user is meaningless, so this side is required
    public User User { get; set; } = null!;
}