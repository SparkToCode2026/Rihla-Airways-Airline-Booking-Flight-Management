
namespace RihlaAirways.Api.Data;
using Microsoft.EntityFrameworkCore;

// this is the bridge between our c# code and the sql server container 
public class AppDbContext : DbContext
{
    // the options come from Program.cs where we pass in the connection string -
    // we never hardcode the connection here, that's what appsettings is for
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        // DbSets go here once the ERD is done - one DbSet per model, 12 total
        // each dev adds their own 2 when they build their models, something like:
        // public DbSet<User> Users { get; set; }
        // public DbSet<Airplane> Airplanes { get; set; }
        // ... etc
    }
}