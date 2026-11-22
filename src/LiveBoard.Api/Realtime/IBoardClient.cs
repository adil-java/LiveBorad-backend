using LiveBoard.Api.Features;

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
}
