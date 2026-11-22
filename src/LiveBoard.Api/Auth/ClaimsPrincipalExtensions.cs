using System.Security.Claims;

namespace LiveBoard.Api.Auth;

public static class FirebaseClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string EmailVerified = "email_verified";
}

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(FirebaseClaims.UserId)
        ?? throw new InvalidOperationException("Authenticated user has no 'sub' claim.");

    public static string? GetEmail(this ClaimsPrincipal user) => user.FindFirstValue(FirebaseClaims.Email);

    public static bool IsEmailVerified(this ClaimsPrincipal user) =>
        string.Equals(user.FindFirstValue(FirebaseClaims.EmailVerified), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Firebase only includes "name" for some providers (e.g. Google), so fall back to the email's local part.</summary>
    public static string GetDisplayName(this ClaimsPrincipal user)
    {
        var name = user.FindFirstValue(FirebaseClaims.Name);
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();
        var email = user.GetEmail();
        return string.IsNullOrEmpty(email) ? "Anonymous" : email.Split('@')[0];
    }
}
