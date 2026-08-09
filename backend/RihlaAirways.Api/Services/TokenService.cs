//namespace DefaultNamespace;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RihlaAirways.Api.Models;

namespace RihlaAirways.Api.Services;
// builds the signed token. kept out of the controller because a controller
// has no business knowing about signing keys, and the email service will
// eventually want to generate links with tokens too
public class TokenService
{
    private readonly IConfiguration _config;

    public TokenService(IConfiguration config) => _config = config;

    public string CreateToken(User user)
    {
        // these claims ARE what every [Authorize] check reads back out later.
        // NameIdentifier is the load-bearing one - it's exactly what
        // User.FindFirstValue(ClaimTypes.NameIdentifier) returns inside the
        // CanAccess helpers in Bookings/PassengerProfiles/Baggages.
        // drop it and every ownership check silently returns 403.
        // Role is what feeds [Authorize(Roles = "Admin")]
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            // unique id per token - only matters if we ever add revocation,
            // but it costs nothing to include now
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(
                int.Parse(_config["Jwt:ExpiryMinutes"] ?? "120")),
            signingCredentials: new SigningCredentials(
                key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    
}