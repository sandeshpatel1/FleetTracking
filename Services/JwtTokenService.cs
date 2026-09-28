using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TrackingMVC.Services
{
    /// <summary>
    /// Issues the JWT that now backs authentication everywhere: it's set as an
    /// HttpOnly cookie for normal browser page loads AND handed back as a raw
    /// string from /Account/LoginApi for anything calling in as an API client
    /// (mobile app, JS, etc.) that will send it as "Authorization: Bearer ...".
    /// Same token, same validation, two ways of presenting it — see
    /// Program.cs's JwtBearerEvents.OnMessageReceived for the cookie fallback.
    /// </summary>
    public class JwtTokenService
    {
        private readonly IConfiguration _cfg;
        public JwtTokenService(IConfiguration cfg) => _cfg = cfg;

        public string GenerateToken(int userId, string username, string fullName, string role, string email)
        {
            var jwt = _cfg.GetSection("Jwt");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["SecretKey"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new(ClaimTypes.Name, username),
                new(ClaimTypes.Role, role),
                new(ClaimTypes.Email, email ?? ""),
                new("full_name", string.IsNullOrWhiteSpace(fullName) ? username : fullName),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var expiryMinutes = int.TryParse(jwt["ExpiryMinutes"], out var m) ? m : 480;

            var token = new JwtSecurityToken(
                issuer: jwt["Issuer"],
                audience: jwt["Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
