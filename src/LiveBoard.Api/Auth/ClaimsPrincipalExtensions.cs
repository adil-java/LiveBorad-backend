using System.Security.Claims;
using System.Text.Json;

namespace LiveBoard.Api.Auth;

public static class FirebaseClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string EmailVerified = "email_verified";
    public const string ProviderId = "provider_id";
    public const string Firebase = "firebase";
}

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(FirebaseClaims.UserId)
        ?? throw new InvalidOperationException("Authenticated user has no 'sub' claim.");

    public static string? GetEmail(this ClaimsPrincipal user) => user.FindFirstValue(FirebaseClaims.Email);

    /// <summary>
    /// A guest is signed in with Firebase anonymous auth (used to join rooms by code) or has no email.
    /// Guests may only use rooms; everything else requires the <see cref="AuthPolicies.Registered"/> policy.
    /// </summary>
    public static bool IsGuest(this ClaimsPrincipal user)
    {
        if (user.FindFirstValue(FirebaseClaims.ProviderId) == "anonymous" || string.IsNullOrEmpty(user.GetEmail()))
            return true;

        var firebase = user.FindFirstValue(FirebaseClaims.Firebase);
        if (string.IsNullOrEmpty(firebase))
            return false;
        try
        {
            using var json = JsonDocument.Parse(firebase);
            return json.RootElement.TryGetProperty("sign_in_provider", out var provider) && provider.GetString() == "anonymous";
        }
        catch (JsonException)
        {
            return false;
        }
    }

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

public static class AuthPolicies
{
    /// <summary>Signed in with a real account (not a guest). Applied to all of /api except room lookup.</summary>
    public const string Registered = "Registered";
}
