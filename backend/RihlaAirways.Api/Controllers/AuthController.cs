using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public record RegisterDto(
        [Required][MaxLength(100)] string Name,
        [Required][EmailAddress][MaxLength(150)] string Email,
        [Required][MinLength(8)] string Password);

    public record LoginDto(
        [Required][EmailAddress] string Email,
        [Required] string Password);

    public record AuthResponseDto(
        string Token, int UserId, string Name, string Email,
        string Role, DateTime ExpiresAt);


    [HttpPost("register")]
    [AllowAnonymous]
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


    [HttpPost("login")]
    [AllowAnonymous]
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