using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using RihlaAirways.Api.Data;
using RihlaAirways.Api.Models;
using RihlaAirways.Api.Services;
namespace RihlaAirways.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase

{
    
    private readonly AppDbContext _context;
    private readonly TokenService _tokens;
    private readonly IConfiguration _config;

    public AuthController(AppDbContext context, TokenService tokens, IConfiguration config)
    {
        _context = context;
        _tokens = tokens;
        _config = config;
    }
    // no Role field here, deliberately. self-registration ALWAYS creates a
    // Passenger - if the client could pick, anyone could sign up as Admin
    // and every [Authorize(Roles = "Admin")] in the project would be
    // decorative. staff accounts get created by an existing admin through
    // UsersController.Create
    // MinLength(8) on its own accepted "aaaaaaaa". the regex asks for a mix of
    // letters and digits, which is the cheapest rule that rules out the
    // passwords people actually pick. deliberately NOT demanding a symbol -
    // length plus variety beats a symbol nobody remembers
    public record RegisterDto(
        [Required][MaxLength(100)] string Name,
        [Required][EmailAddress][MaxLength(150)] string Email,
        [Required]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
        [MaxLength(128)]
        [RegularExpression(@"^(?=.*[A-Za-z])(?=.*\d).+$",
            ErrorMessage = "Password must contain at least one letter and one number.")]
        string Password);

    public record LoginDto(
        [Required][EmailAddress] string Email,
        [Required] string Password);

    public record AuthResponseDto(
        string Token, int UserId, string Name, string Email,
        string Role, DateTime ExpiresAt);


    // 10/min per IP - looser than login on purpose. this defends against
    // bulk-creating junk accounts, not against guessing a known password, and
    // one person fixing a validation error a few times in a row is normal
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
            return Conflict("An account with that email already exists.");

        var user = new User
        {
            Name = dto.Name,
            Email = dto.Email,
            // BCrypt generates its own salt and embeds it in the output -
            // that's why there's no separate salt column in the schema.
            // it's also deliberately SLOW, which is the whole point: sha256
            // can be computed billions of times a second so a leaked table
            // gets cracked fast, bcrypt makes that expensive
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
            Role = "Passenger",
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return Ok(BuildResponse(user));
    }


    // 5 attempts per minute per IP. BCrypt already makes each guess expensive,
    // but nothing stopped an attacker making unlimited guesses - and every
    // seeded account in this project shares one password, so a working
    // dictionary attack would open all of them at once.
    // note this throttles by IP, not by email: limiting per-email would let
    // someone lock a specific user out of their own account
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);

        // edge case worth being deliberate about: the SAME message whether
        // the email doesn't exist or the password is wrong. saying "no
        // account with that email" would let anyone probe which addresses
        // are registered - that's user enumeration, and it's a real finding
        // in a pen test report
        if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
            return Unauthorized("Invalid email or password.");

        return Ok(BuildResponse(user));
    }


    // "am I still logged in, and as who" - the frontend calls this on load
    // to decide whether to show the login page or the dashboard
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id == null) return Unauthorized();

        var user = await _context.Users
            .Where(u => u.Id == int.Parse(id))
            .Select(u => new { u.Id, u.Name, u.Email, u.Role, u.CreatedAt })
            .FirstOrDefaultAsync();

        return user == null ? NotFound() : Ok(user);
    }


    private AuthResponseDto BuildResponse(User user) => new(
        _tokens.CreateToken(user), user.Id, user.Name, user.Email, user.Role,
        DateTime.UtcNow.AddMinutes(int.Parse(_config["Jwt:ExpiryMinutes"] ?? "120")));//2hrs
    
}