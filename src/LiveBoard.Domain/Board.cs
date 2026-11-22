namespace LiveBoard.Domain;

/// <summary>
/// Aggregate root for a board. Members, columns and pending invites are embedded because they are
/// small and always read with the board. Cards live in their own collection (see <see cref="Card"/>).
/// Every mutation increments <see cref="Version"/>, which the persistence layer uses for optimistic concurrency.
/// </summary>
public sealed class Board
{
    public const int MaxNameLength = 100;
    public const int MaxColumnNameLength = 50;
    public const int MaxColumns = 20;
    public const int MaxMembers = 50;

    private List<BoardMember> _members = [];
    private List<Column> _columns = [];
    private List<PendingInvite> _pendingInvites = [];

    private Board()
    {
        Name = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int Version { get; private set; }

    public IReadOnlyList<BoardMember> Members => _members;
    public IReadOnlyList<Column> Columns => _columns;
    public IReadOnlyList<PendingInvite> PendingInvites => _pendingInvites;

    public static Board Create(string name, string ownerId, DateTime now)
    {
        var board = new Board
        {
            Id = Guid.CreateVersion7(),
            Name = Guard.RequiredText(name, "Board name", MaxNameLength),
            CreatedBy = ownerId,
            CreatedAt = now,
            Version = 1,
        };
        board._members.Add(new BoardMember(ownerId, BoardRole.Owner, now));

        string[] defaults = ["To do", "In progress", "Done"];
        foreach (var (columnName, position) in defaults.Zip(Ranking.Spread(defaults.Length)))
            board._columns.Add(new Column(columnName, position));

        return board;
    }

    public BoardRole? RoleOf(string userId) => _members.FirstOrDefault(m => m.UserId == userId)?.Role;

    public void Rename(string name)
    {
        Name = Guard.RequiredText(name, "Board name", MaxNameLength);
        Touch();
    }

    // ---- Columns -------------------------------------------------------------------------------

    public bool HasColumn(Guid columnId) => _columns.Any(c => c.Id == columnId);

    public Column GetColumn(Guid columnId) =>
        _columns.FirstOrDefault(c => c.Id == columnId) ?? throw new EntityNotFoundException("Column not found.");

    public Column AddColumn(string name)
    {
        if (_columns.Count >= MaxColumns)
            throw new DomainValidationException($"A board can have at most {MaxColumns} columns.");

        double? last = _columns.Count == 0 ? null : _columns.Max(c => c.Position);
        var column = new Column(Guard.RequiredText(name, "Column name", MaxColumnNameLength), Ranking.Between(last, null));
        _columns.Add(column);
        Touch();
        return column;
    }

    public Column RenameColumn(Guid columnId, string name)
    {
        var column = GetColumn(columnId);
        column.Name = Guard.RequiredText(name, "Column name", MaxColumnNameLength);
        Touch();
        return column;
    }

    /// <summary>Removes a column. The caller must ensure it holds no cards.</summary>
    public void RemoveColumn(Guid columnId)
    {
        var column = GetColumn(columnId);
        if (_columns.Count == 1)
            throw new DomainValidationException("A board needs at least one column.");
        _columns.Remove(column);
        Touch();
    }

    // ---- Members & invites ---------------------------------------------------------------------

    public void AddMember(string userId, BoardRole role, DateTime now)
    {
        if (RoleOf(userId) is not null)
            throw new DomainValidationException("This user is already a member of the board.");
        if (_members.Count >= MaxMembers)
            throw new DomainValidationException($"A board can have at most {MaxMembers} members.");
        _members.Add(new BoardMember(userId, role, now));
        Touch();
    }

    public void ChangeMemberRole(string userId, BoardRole role)
    {
        var member = GetMember(userId);
        if (member.Role == BoardRole.Owner && role != BoardRole.Owner && OwnerCount == 1)
            throw new DomainValidationException("A board must keep at least one owner.");
        member.Role = role;
        Touch();
    }

    public void RemoveMember(string userId)
    {
        var member = GetMember(userId);
        if (member.Role == BoardRole.Owner && OwnerCount == 1)
            throw new DomainValidationException("A board must keep at least one owner.");
        _members.Remove(member);
        Touch();
    }

    /// <summary>Records an invite for someone who has not signed up (or verified their email) yet.</summary>
    public void Invite(string email, BoardRole role, string invitedBy, DateTime now)
    {
        var normalized = Guard.Email(email);
        _pendingInvites.RemoveAll(i => i.Email == normalized);
        _pendingInvites.Add(new PendingInvite(normalized, role, invitedBy, now));
        Touch();
    }

    public void CancelInvite(string email)
    {
        var normalized = Guard.Email(email);
        if (_pendingInvites.RemoveAll(i => i.Email == normalized) == 0)
            throw new EntityNotFoundException("Invite not found.");
        Touch();
    }

    /// <summary>Turns a pending invite for <paramref name="email"/> into a membership. Returns false if there was none.</summary>
    public bool AcceptInvite(string userId, string email, DateTime now)
    {
        var normalized = Guard.Email(email);
        var invite = _pendingInvites.FirstOrDefault(i => i.Email == normalized);
        if (invite is null)
            return false;

        _pendingInvites.Remove(invite);
        if (RoleOf(userId) is null)
            _members.Add(new BoardMember(userId, invite.Role, now));
        Touch();
        return true;
    }

    private int OwnerCount => _members.Count(m => m.Role == BoardRole.Owner);

    private BoardMember GetMember(string userId) =>
        _members.FirstOrDefault(m => m.UserId == userId) ?? throw new EntityNotFoundException("Member not found.");

    private void Touch() => Version++;
}

public sealed class BoardMember
{
    private BoardMember() => UserId = string.Empty;

    internal BoardMember(string userId, BoardRole role, DateTime joinedAt)
    {
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
    }

    public string UserId { get; private set; }
    public BoardRole Role { get; internal set; }
    public DateTime JoinedAt { get; private set; }
}

public sealed class Column
{
    private Column() => Name = string.Empty;

    internal Column(string name, double position)
    {
        Id = Guid.CreateVersion7();
        Name = name;
        Position = position;
    }

    public Guid Id { get; private set; }
    public string Name { get; internal set; }
    public double Position { get; private set; }
}

public sealed class PendingInvite
{
    private PendingInvite()
    {
        Email = string.Empty;
        InvitedBy = string.Empty;
    }

    internal PendingInvite(string email, BoardRole role, string invitedBy, DateTime invitedAt)
    {
        Email = email;
        Role = role;
        InvitedBy = invitedBy;
        InvitedAt = invitedAt;
    }

    public string Email { get; private set; }
    public BoardRole Role { get; private set; }
    public string InvitedBy { get; private set; }
    public DateTime InvitedAt { get; private set; }
}
