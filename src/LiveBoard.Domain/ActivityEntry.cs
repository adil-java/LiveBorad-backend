namespace LiveBoard.Domain;

public enum ActivityType
{
    BoardCreated,
    BoardRenamed,
    ColumnAdded,
    ColumnRenamed,
    ColumnRemoved,
    CardCreated,
    CardEdited,
    CardMoved,
    CardDeleted,
    MemberAdded,
    MemberInvited,
    MemberRoleChanged,
    MemberRemoved,
    DrawingCleared,
}

/// <summary>
/// An immutable entry in a board's activity feed. The actor's display name is copied in at write time
/// so the feed renders without joining users, and stays accurate even if the user later leaves.
/// </summary>
public sealed class ActivityEntry
{
    private ActivityEntry()
    {
        ActorId = string.Empty;
        ActorName = string.Empty;
        Summary = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid BoardId { get; private set; }
    public string ActorId { get; private set; }
    public string ActorName { get; private set; }
    public ActivityType Type { get; private set; }
    public string Summary { get; private set; }
    public Guid? CardId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static ActivityEntry Create(Guid boardId, string actorId, string actorName, ActivityType type, string summary, Guid? cardId, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        BoardId = boardId,
        ActorId = actorId,
        ActorName = actorName,
        Type = type,
        Summary = summary,
        CardId = cardId,
        CreatedAt = now,
    };
}
