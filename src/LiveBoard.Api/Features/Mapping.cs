using LiveBoard.Domain;

namespace LiveBoard.Api.Features;

public static class Mapping
{
    public static BoardSummaryDto ToSummary(this Board board, string userId) =>
        new(board.Id, board.Name, board.RoleOf(userId) ?? BoardRole.Viewer, board.Members.Count, board.CreatedAt);

    public static BoardDetailDto ToDetail(this Board board, BoardRole myRole, IEnumerable<Card> cards, IReadOnlyDictionary<string, UserProfile> profiles)
    {
        var members = board.ToMembers(myRole, profiles);
        return new BoardDetailDto(
            board.Id,
            board.Name,
            myRole,
            board.ToColumnDtos(),
            cards.Select(c => c.ToDto()).ToList(),
            members.Members,
            members.PendingInvites);
    }

    public static IReadOnlyList<ColumnDto> ToColumnDtos(this Board board) =>
        board.Columns.OrderBy(c => c.Position).Select(c => c.ToDto()).ToList();

    public static ColumnDto ToDto(this Column column) => new(column.Id, column.Name, column.Position);

    public static CardDto ToDto(this Card card) =>
        new(card.Id, card.BoardId, card.ColumnId, card.Title, card.Description, card.Position, card.Version, card.UpdatedBy, card.UpdatedAt);

    public static MembersDto ToMembers(this Board board, BoardRole viewerRole, IReadOnlyDictionary<string, UserProfile> profiles)
    {
        var members = board.Members
            .Select(m => profiles.TryGetValue(m.UserId, out var p)
                ? new MemberDto(m.UserId, p.DisplayName, p.Email, m.Role)
                : new MemberDto(m.UserId, "Unknown user", null, m.Role))
            .OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<InviteDto> invites = viewerRole.CanManage()
            ? board.PendingInvites.Select(i => new InviteDto(i.Email, i.Role, i.InvitedAt)).ToList()
            : [];

        return new MembersDto(members, invites);
    }

    public static ActivityDto ToDto(this ActivityEntry entry) =>
        new(entry.Id, entry.ActorId, entry.ActorName, entry.Type, entry.Summary, entry.CardId, entry.CreatedAt);

    public static ChatMessageDto ToDto(this ChatMessage message) =>
        new(message.Id, message.BoardId, message.AuthorId, message.AuthorName, message.Text, message.CreatedAt);

    public static StrokeDto ToDto(this Stroke stroke) =>
        new(stroke.Id, stroke.BoardId, stroke.AuthorId, stroke.Tool, stroke.Color, stroke.Width, stroke.Points, stroke.CreatedAt);
}
