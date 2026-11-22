namespace LiveBoard.Domain;

/// <summary>
/// A card on a board. Stored in its own collection so that concurrent edits to different cards never
/// contend on the board document. <see cref="Version"/> implements optimistic concurrency: clients send
/// the version they last saw, and a mismatch means someone else changed the card first.
/// </summary>
public sealed class Card
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 5000;

    private Card()
    {
        Title = string.Empty;
        CreatedBy = string.Empty;
        UpdatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid BoardId { get; private set; }
    public Guid ColumnId { get; private set; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public double Position { get; private set; }
    public int Version { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public string UpdatedBy { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public static Card Create(Guid boardId, Guid columnId, string title, string? description, double position, string userId, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        BoardId = boardId,
        ColumnId = columnId,
        Title = Guard.RequiredText(title, "Title", MaxTitleLength),
        Description = Guard.OptionalText(description, "Description", MaxDescriptionLength),
        Position = position,
        Version = 1,
        CreatedBy = userId,
        CreatedAt = now,
        UpdatedBy = userId,
        UpdatedAt = now,
    };

    public void Edit(string title, string? description, int expectedVersion, string userId, DateTime now)
    {
        EnsureVersion(expectedVersion);
        Title = Guard.RequiredText(title, "Title", MaxTitleLength);
        Description = Guard.OptionalText(description, "Description", MaxDescriptionLength);
        Bump(userId, now);
    }

    public void MoveTo(Guid columnId, double position, int expectedVersion, string userId, DateTime now)
    {
        EnsureVersion(expectedVersion);
        ColumnId = columnId;
        Position = position;
        Bump(userId, now);
    }

    private void EnsureVersion(int expectedVersion)
    {
        if (expectedVersion != Version)
            throw new ConcurrencyConflictException(Id, expectedVersion, Version);
    }

    private void Bump(string userId, DateTime now)
    {
        Version++;
        UpdatedBy = userId;
        UpdatedAt = now;
    }
}
