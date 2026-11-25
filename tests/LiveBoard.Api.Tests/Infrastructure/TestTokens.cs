using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LiveBoard.Api.Tests.Infrastructure;

/// <summary>Issues tokens shaped like Firebase ID tokens (same issuer format, audience and claim names).</summary>
public static class TestTokens
{
    public const string ProjectId = "test-project";

    public static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("liveboard-integration-tests-signing-key-0123456789"));

    /// <summary>Shaped like a Firebase anonymous-auth token: no email, provider "anonymous".</summary>
    public static string CreateGuest(string userId) => Sign(new Dictionary<string, object>
    {
        ["sub"] = userId,
        ["user_id"] = userId,
        ["provider_id"] = "anonymous",
        ["firebase"] = new Dictionary<string, object> { ["identities"] = new Dictionary<string, object>(), ["sign_in_provider"] = "anonymous" },
    }, null);

    public static string Create(string userId, string email, string? name, bool emailVerified, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["user_id"] = userId,
            ["email"] = email,
            ["email_verified"] = emailVerified,
        };
        if (name is not null)
            claims["name"] = name;
        return Sign(claims, key);
    }

    private static string Sign(Dictionary<string, object> claims, SecurityKey? key)
    {
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://securetoken.google.com/{ProjectId}",
            Audience = ProjectId,
            Claims = claims,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }
}
