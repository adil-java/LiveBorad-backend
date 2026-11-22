using LiveBoard.Api.Features;
using LiveBoard.Domain;

namespace LiveBoard.Api.Realtime;

/// <summary>
/// Server-to-client events. A strongly typed hub makes every event name and payload compile-time checked.
/// Every event carries the board ID so clients can ignore events for a board they have navigated away from.
/// </summary>
public interface IBoardClient
{
    Task BoardRenamed(Guid boardId, string name);
    Task BoardDeleted(Guid boardId);

    Task ColumnsChanged(Guid boardId, IReadOnlyList<ColumnDto> columns);

    Task CardCreated(CardDto card);
    Task CardUpdated(CardDto card);
    Task CardMoved(CardDto card);
    Task CardDeleted(Guid boardId, Guid cardId);
    Task CardsRepositioned(Guid boardId, IReadOnlyList<CardPositionDto> positions);

    Task MembersChanged(Guid boardId, IReadOnlyList<MemberDto> members);
    Task AccessRevoked(Guid boardId);

    Task PresenceChanged(Guid boardId, IReadOnlyList<PresenceUserDto> users);
    Task CardEditing(Guid boardId, Guid cardId, PresenceUserDto user, bool isEditing);

    Task ActivityAdded(ActivityDto activity);

    Task ChatMessageAdded(ChatMessageDto message);
    Task ChatMessageDeleted(Guid boardId, Guid messageId);
    Task ChatTyping(Guid boardId, PresenceUserDto user);

    Task StrokeAdded(StrokeDto stroke);
    /// <summary>Points drawn since the previous progress message for this stroke, so others see it being drawn.</summary>
    Task StrokeProgress(Guid boardId, Guid strokeId, PresenceUserDto user, StrokeTool tool, string color, double width, IReadOnlyList<double> points);
    Task StrokesErased(Guid boardId, IReadOnlyList<Guid> strokeIds);
    Task DrawingCleared(Guid boardId);
}
