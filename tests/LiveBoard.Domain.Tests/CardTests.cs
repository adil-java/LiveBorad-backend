using LiveBoard.Domain;

namespace LiveBoard.Domain.Tests;

public class CardTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static Card NewCard() => Card.Create(Guid.NewGuid(), Guid.NewGuid(), "Write tests", null, 1024, "alice", Now);

    [Fact]
    public void Create_StartsAtVersionOne() => Assert.Equal(1, NewCard().Version);

    [Fact]
    public void Edit_WithCurrentVersion_UpdatesAndBumpsVersion()
    {
        var card = NewCard();
        card.Edit("Write more tests", "  details  ", expectedVersion: 1, "bob", Now.AddMinutes(1));

        Assert.Equal("Write more tests", card.Title);
        Assert.Equal("details", card.Description);
        Assert.Equal(2, card.Version);
        Assert.Equal("bob", card.UpdatedBy);
    }

    [Fact]
    public void Edit_WithStaleVersion_ThrowsAndLeavesCardUnchanged()
    {
        var card = NewCard();
        card.Edit("Second", null, 1, "bob", Now);

        var ex = Assert.Throws<ConcurrencyConflictException>(() => card.Edit("Stale edit", null, 1, "carol", Now));

        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal("Second", card.Title);
        Assert.Equal(2, card.Version);
    }

    [Fact]
    public void Edit_BlankDescription_IsStoredAsNull()
    {
        var card = NewCard();
        card.Edit("Title", "   ", 1, "bob", Now);
        Assert.Null(card.Description);
    }

    [Fact]
    public void Edit_TooLongTitle_Throws() =>
        Assert.Throws<DomainValidationException>(() => NewCard().Edit(new string('x', Card.MaxTitleLength + 1), null, 1, "bob", Now));

    [Fact]
    public void MoveTo_ChangesColumnAndPosition()
    {
        var card = NewCard();
        var column = Guid.NewGuid();
        card.MoveTo(column, 512, 1, "bob", Now);

        Assert.Equal(column, card.ColumnId);
        Assert.Equal(512, card.Position);
        Assert.Equal(2, card.Version);
    }

    [Fact]
    public void MoveTo_WithStaleVersion_Throws() =>
        Assert.Throws<ConcurrencyConflictException>(() => NewCard().MoveTo(Guid.NewGuid(), 512, 7, "bob", Now));

    [Fact]
    public void UserProfile_FallsBackToEmailLocalPart_ForDisplayName() =>
        Assert.Equal("dana", UserProfile.Create("uid", "Dana@Example.com", null, true, Now).DisplayName);
}
