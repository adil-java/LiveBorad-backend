using LiveBoard.Domain;

namespace LiveBoard.Api.Features;

// ---- Requests ----------------------------------------------------------------------------------

public sealed record CreateBoardRequest(string Name);
public sealed record RenameBoardRequest(string Name);
public sealed record ColumnNameRequest(string Name);
public sealed record CreateCardRequest(Guid ColumnId, string Title, string? Description);
public sealed record EditCardRequest(string Title, string? Description, int Version);

/// <summary>
/// Move a card into <see cref="ColumnId"/> between two neighbours as the client saw them.
/// <see cref="BeforeCardId"/> is the card that will sit directly above, <see cref="AfterCardId"/> directly below.
/// Both null appends to the end of the column.
/// </summary>
public sealed record MoveCardRequest(Guid ColumnId, Guid? BeforeCardId, Guid? AfterCardId, int Version);

public sealed record InviteMemberRequest(string Email, BoardRole Role);
public sealed record ChangeRoleRequest(BoardRole Role);

// ---- Responses / real-time payloads ------------------------------------------------------------

public sealed record MeDto(string UserId, string Email, string DisplayName, bool EmailVerified, IReadOnlyList<Guid> JoinedBoardIds);

public sealed record BoardSummaryDto(Guid Id, string Name, BoardRole Role, int MemberCount, DateTime CreatedAt);

public sealed record BoardDetailDto(
    Guid Id,
    string Name,
    BoardRole MyRole,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<CardDto> Cards,
    IReadOnlyList<MemberDto> Members,
    IReadOnlyList<InviteDto> PendingInvites);

public sealed record ColumnDto(Guid Id, string Name, double Position);

public sealed record CardDto(
    Guid Id,
    Guid BoardId,
    Guid ColumnId,
    string Title,
    string? Description,
    double Position,
    int Version,
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record CardPositionDto(Guid CardId, double Position);

public sealed record MemberDto(string UserId, string DisplayName, string? Email, BoardRole Role);

public sealed record InviteDto(string Email, BoardRole Role, DateTime InvitedAt);

/// <summary>Pending invites are only included for owners; other roles get an empty list.</summary>
public sealed record MembersDto(IReadOnlyList<MemberDto> Members, IReadOnlyList<InviteDto> PendingInvites);

public sealed record PresenceUserDto(string UserId, string DisplayName);

public sealed record ActivityDto(Guid Id, string ActorId, string ActorName, ActivityType Type, string Summary, Guid? CardId, DateTime CreatedAt);
