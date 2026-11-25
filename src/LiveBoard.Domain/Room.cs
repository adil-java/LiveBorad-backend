using System.Security.Cryptography;

namespace LiveBoard.Domain;

/// <summary>
/// A standalone call room, reachable by anyone who has its code, including guests without an account.
/// Rooms only host a voice/video call; they never grant access to boards.
/// </summary>
public sealed class Room
{
    public const int MaxNameLength = 60;
    public const int MaxRoomsPerOwner = 50;

    // No "l": it is easy to confuse with "i" when a code is read out or typed.
    private const string CodeAlphabet = "abcdefghijkmnopqrstuvwxyz";
    private const int CodeLetters = 10;

    private Room()
    {
        Code = string.Empty;
        Name = string.Empty;
        OwnerId = string.Empty;
        OwnerName = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Shareable code like "abc-defg-hij": 10 random letters (~1.4 × 10^14 combinations), so codes can't be guessed.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }
    public string OwnerId { get; private set; }
    public string OwnerName { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static Room Create(string? name, string ownerId, string ownerName, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = NewCode(),
        Name = Guard.OptionalText(name, "Room name", MaxNameLength) ?? $"{ownerName}'s room",
        OwnerId = ownerId,
        OwnerName = ownerName,
        CreatedAt = now,
    };

    /// <summary>Gives the room a fresh code (used if a generated code happens to be taken).</summary>
    public void RegenerateCode() => Code = NewCode();

    public static string NewCode()
    {
        Span<char> letters = stackalloc char[CodeLetters];
        for (var i = 0; i < letters.Length; i++)
            letters[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return $"{letters[..3]}-{letters[3..7]}-{letters[7..]}";
    }

    /// <summary>
    /// Accepts what people actually paste or type: any case, with or without dashes or spaces, or a whole invite link.
    /// Returns null if it can't be a room code.
    /// </summary>
    public static string? NormalizeCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var text = input.Trim();
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
            text = text[(slash + 1)..]; // invite link: keep the last path segment

        var letters = new string(text.Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).ToArray());
        if (letters.Length != CodeLetters || letters.Any(c => !CodeAlphabet.Contains(c)))
            return null;
        return $"{letters[..3]}-{letters[3..7]}-{letters[7..]}";
    }

    /// <summary>Display name a guest typed: trimmed, required, and short enough for a video tile.</summary>
    public static string GuestName(string? name)
    {
        var trimmed = Guard.RequiredText(name, "Your name", 40);
        return trimmed;
    }
}
