namespace LiveBoard.Domain;

/// <summary>
/// Local copy of a Firebase user, keyed by Firebase UID. Firebase owns authentication; we keep this
/// so members can be listed with names and invited by email without calling the Firebase Admin API.
/// </summary>
public sealed class UserProfile
{
    private UserProfile()
    {
        Id = string.Empty;
        Email = string.Empty;
        DisplayName = string.Empty;
    }

    public string Id { get; private set; }
    public string Email { get; private set; }
    public string DisplayName { get; private set; }

    /// <summary>Only verified emails may claim invites, so nobody can sign up with someone else's address to take their invite.</summary>
    public bool EmailVerified { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime LastSeenAt { get; private set; }

    public static UserProfile Create(string id, string email, string? displayName, bool emailVerified, DateTime now)
    {
        var profile = new UserProfile { Id = id, CreatedAt = now };
        profile.Refresh(email, displayName, emailVerified, now);
        return profile;
    }

    public void Refresh(string email, string? displayName, bool emailVerified, DateTime now)
    {
        Email = Guard.Email(email);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Email.Split('@')[0] : displayName.Trim();
        EmailVerified = emailVerified;
        LastSeenAt = now;
    }
}
