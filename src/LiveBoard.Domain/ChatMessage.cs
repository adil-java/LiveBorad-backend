namespace LiveBoard.Domain;

/// <summary>
/// A message in a board's chat. Like <see cref="ActivityEntry"/>, the author's display name is copied in
/// so history renders without joining users and stays readable after the author leaves the board.
/// </summary>
public sealed class ChatMessage
{
    public const int MaxTextLength = 2000;

    private ChatMessage()
    {
        AuthorId = string.Empty;
        AuthorName = string.Empty;
        Text = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid BoardId { get; private set; }
    public string AuthorId { get; private set; }
    public string AuthorName { get; private set; }
    public string Text { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static ChatMessage Create(Guid boardId, string authorId, string authorName, string text, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        BoardId = boardId,
        AuthorId = authorId,
        AuthorName = authorName,
        Text = Guard.RequiredText(text, "Message", MaxTextLength),
        CreatedAt = now,
    };

    /// <summary>Authors can delete their own messages; owners can moderate any message.</summary>
    public bool CanBeDeletedBy(string userId, BoardRole role) => AuthorId == userId || role.CanManage();
}
