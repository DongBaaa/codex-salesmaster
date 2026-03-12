using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using SalesMaster.Server.Api.Domain;
using SalesMaster.Shared.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SalesMaster.Server.Api.Security;

public interface IJwtTokenFactory
{
    LoginResponse Create(UserAccount user);
}

public sealed class JwtTokenFactory : IJwtTokenFactory
{
    private readonly JwtOptions _jwtOptions;

    public JwtTokenFactory(IOptions<JwtOptions> jwtOptions)
    {
        _jwtOptions = jwtOptions.Value;
    }

    public LoginResponse Create(UserAccount user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new("office", user.OfficeCode ?? string.Empty)
        };

        claims.AddRange(user.Permissions.Select(permission => new Claim("perm", permission.Permission)));

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        var tokenValue = new JwtSecurityTokenHandler().WriteToken(token);

        return new LoginResponse
        {
            AccessToken = tokenValue,
            ExpiresAtUtc = expiresAt,
            User = new UserSessionDto
            {
                UserId = user.Id,
                Username = user.Username,
                Role = user.Role,
                OfficeCode = user.OfficeCode ?? string.Empty,
                Permissions = user.Permissions.Select(x => x.Permission).Distinct().ToList()
            }
        };
    }
}
