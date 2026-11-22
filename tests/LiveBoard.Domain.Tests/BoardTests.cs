using LiveBoard.Domain;

namespace LiveBoard.Domain.Tests;

public class BoardTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static Board NewBoard() => Board.Create("Sprint 1", "owner", Now);

    [Fact]
    public void Create_SetsOwnerAndDefaultColumnsInOrder()
    {
        var board = NewBoard();

        Assert.Equal(BoardRole.Owner, board.RoleOf("owner"));
        Assert.Equal(["To do", "In progress", "Done"], board.Columns.OrderBy(c => c.Position).Select(c => c.Name));
        Assert.Equal(1, board.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_RequiresName(string? name) =>
        Assert.Throws<DomainValidationException>(() => Board.Create(name!, "owner", Now));

    [Fact]
    public void Create_TrimsName() => Assert.Equal("Roadmap", Board.Create("  Roadmap  ", "o", Now).Name);

    [Fact]
    public void EveryMutation_IncrementsVersion()
    {
        var board = NewBoard();
        board.Rename("Sprint 2");
        board.AddColumn("Review");
        board.AddMember("alice", BoardRole.Editor, Now);
        Assert.Equal(4, board.Version);
    }

    [Fact]
    public void AddColumn_AppendsAfterLastColumn()
    {
        var board = NewBoard();
        var column = board.AddColumn("Review");
        Assert.True(column.Position > board.Columns.Where(c => c.Id != column.Id).Max(c => c.Position));
    }

    [Fact]
    public void AddColumn_EnforcesLimit()
    {
        var board = NewBoard();
        while (board.Columns.Count < Board.MaxColumns)
            board.AddColumn($"C{board.Columns.Count}");
        Assert.Throws<DomainValidationException>(() => board.AddColumn("One too many"));
    }

    [Fact]
    public void RemoveColumn_KeepsAtLeastOne()
    {
        var board = NewBoard();
        board.RemoveColumn(board.Columns[0].Id);
        board.RemoveColumn(board.Columns[0].Id);
        Assert.Throws<DomainValidationException>(() => board.RemoveColumn(board.Columns[0].Id));
    }

    [Fact]
    public void UnknownColumn_IsNotFound() =>
        Assert.Throws<EntityNotFoundException>(() => NewBoard().RenameColumn(Guid.NewGuid(), "x"));

    [Fact]
    public void AddMember_Twice_Throws()
    {
        var board = NewBoard();
        board.AddMember("alice", BoardRole.Viewer, Now);
        Assert.Throws<DomainValidationException>(() => board.AddMember("alice", BoardRole.Editor, Now));
    }

    [Fact]
    public void LastOwner_CannotBeRemovedOrDemoted()
    {
        var board = NewBoard();
        Assert.Throws<DomainValidationException>(() => board.RemoveMember("owner"));
        Assert.Throws<DomainValidationException>(() => board.ChangeMemberRole("owner", BoardRole.Editor));
    }

    [Fact]
    public void Owner_CanStepDown_WhenAnotherOwnerExists()
    {
        var board = NewBoard();
        board.AddMember("alice", BoardRole.Owner, Now);
        board.ChangeMemberRole("owner", BoardRole.Viewer);
        Assert.Equal(BoardRole.Viewer, board.RoleOf("owner"));
    }

    [Fact]
    public void Invite_NormalizesEmail_AndReplacesExistingInvite()
    {
        var board = NewBoard();
        board.Invite("  Bob@Example.COM ", BoardRole.Viewer, "owner", Now);
        board.Invite("bob@example.com", BoardRole.Editor, "owner", Now);

        var invite = Assert.Single(board.PendingInvites);
        Assert.Equal("bob@example.com", invite.Email);
        Assert.Equal(BoardRole.Editor, invite.Role);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("bob@")]
    [InlineData("a@b@c")]
    public void Invite_RejectsInvalidEmail(string email) =>
        Assert.Throws<DomainValidationException>(() => NewBoard().Invite(email, BoardRole.Viewer, "owner", Now));

    [Fact]
    public void AcceptInvite_AddsMemberWithInvitedRole()
    {
        var board = NewBoard();
        board.Invite("bob@example.com", BoardRole.Editor, "owner", Now);

        Assert.True(board.AcceptInvite("bob", "BOB@example.com", Now));
        Assert.Equal(BoardRole.Editor, board.RoleOf("bob"));
        Assert.Empty(board.PendingInvites);
    }

    [Fact]
    public void AcceptInvite_WithoutInvite_ReturnsFalse() =>
        Assert.False(NewBoard().AcceptInvite("bob", "bob@example.com", Now));

    [Theory]
    [InlineData(BoardRole.Viewer, false, false)]
    [InlineData(BoardRole.Editor, true, false)]
    [InlineData(BoardRole.Owner, true, true)]
    public void RolePermissions(BoardRole role, bool canEdit, bool canManage)
    {
        Assert.Equal(canEdit, role.CanEdit());
        Assert.Equal(canManage, role.CanManage());
    }
}
