using System.Net;
using System.Net.Http.Json;
using LiveBoard.Api.Features;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;

namespace LiveBoard.Api.Tests;

public class CardApiTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Viewer_CannotCreateCards_ButEditorCan()
    {
        var owner = await CreateUserAsync();
        var viewer = await CreateUserAsync();
        var editor = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];

        var denied = await viewer.Http.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new CreateCardRequest(column.Id, "Nope", null), Json);
        var card = await CreateCardAsync(editor, board.Id, column.Id, "Allowed");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, card.Version);
        Assert.Contains((await GetBoardAsync(viewer, board.Id)).Cards, c => c.Id == card.Id); // viewers can still read
    }

    [Fact]
    public async Task NewCards_AreAppendedToTheEndOfTheColumn()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];

        var first = await CreateCardAsync(owner, board.Id, column.Id, "First");
        var second = await CreateCardAsync(owner, board.Id, column.Id, "Second");

        Assert.True(second.Position > first.Position);
    }

    [Fact]
    public async Task Edit_WithStaleVersion_Returns409AndTheCurrentCard()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        var card = await CreateCardAsync(owner, board.Id, column.Id, "Original");
        var path = $"/api/boards/{board.Id}/cards/{card.Id}";

        // Two people opened version 1. Alice saves first...
        var alice = await ReadAsync<CardDto>(await owner.Http.PutAsJsonAsync(path, new EditCardRequest("Alice's title", null, 1), Json));
        // ...then Bob saves based on the version he saw.
        var bob = await owner.Http.PutAsJsonAsync(path, new EditCardRequest("Bob's title", null, 1), Json);
        var current = await ReadAsync<CardDto>(bob, HttpStatusCode.Conflict);

        Assert.Equal(2, alice.Version);
        Assert.Equal("Alice's title", current.Title); // Bob is shown the latest state instead of silently overwriting it
        Assert.Equal(2, current.Version);
    }

    [Fact]
    public async Task ConcurrentEdits_ExactlyOneWins()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        var card = await CreateCardAsync(owner, board.Id, column.Id, "Race");
        var path = $"/api/boards/{board.Id}/cards/{card.Id}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            owner.Http.PutAsJsonAsync(path, new EditCardRequest($"Edit {i}", null, 1), Json)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task Move_BetweenTwoCards_InAnotherColumn_PlacesItBetweenThem()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var columns = (await GetBoardAsync(owner, board.Id)).Columns;
        var (todo, doing) = (columns[0], columns[1]);

        var moving = await CreateCardAsync(owner, board.Id, todo.Id, "Moving");
        var top = await CreateCardAsync(owner, board.Id, doing.Id, "Top");
        var bottom = await CreateCardAsync(owner, board.Id, doing.Id, "Bottom");

        var moved = await ReadAsync<CardDto>(await owner.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{moving.Id}/move", new MoveCardRequest(doing.Id, top.Id, bottom.Id, moving.Version), Json));

        var order = (await GetBoardAsync(owner, board.Id)).Cards
            .Where(c => c.ColumnId == doing.Id).OrderBy(c => c.Position).Select(c => c.Title);
        Assert.Equal(["Top", "Moving", "Bottom"], order);
        Assert.Equal(2, moved.Version);
    }

    [Fact]
    public async Task Move_ToTopAndToEmptyColumn()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var columns = (await GetBoardAsync(owner, board.Id)).Columns;
        var a = await CreateCardAsync(owner, board.Id, columns[0].Id, "A");
        var b = await CreateCardAsync(owner, board.Id, columns[0].Id, "B");

        var toTop = await ReadAsync<CardDto>(await owner.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{b.Id}/move", new MoveCardRequest(columns[0].Id, null, a.Id, b.Version), Json));
        var toEmpty = await ReadAsync<CardDto>(await owner.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{a.Id}/move", new MoveCardRequest(columns[2].Id, null, null, a.Version), Json));

        Assert.True(toTop.Position < a.Position);
        Assert.Equal(columns[2].Id, toEmpty.ColumnId);
    }

    [Fact]
    public async Task Move_WithNeighbourFromAnotherColumn_Returns409BecauseTheClientViewIsStale()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var columns = (await GetBoardAsync(owner, board.Id)).Columns;
        var card = await CreateCardAsync(owner, board.Id, columns[0].Id, "Card");
        var elsewhere = await CreateCardAsync(owner, board.Id, columns[2].Id, "Elsewhere");

        var response = await owner.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{card.Id}/move", new MoveCardRequest(columns[1].Id, elsewhere.Id, null, card.Version), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Move_RelativeToItself_Returns400()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        var card = await CreateCardAsync(owner, board.Id, column.Id, "Card");

        var response = await owner.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/cards/{card.Id}/move", new MoveCardRequest(column.Id, card.Id, null, card.Version), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ManyMovesIntoTheSameGap_TriggerARebalance_AndKeepOrder()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        var top = await CreateCardAsync(owner, board.Id, column.Id, "Top");
        var bottom = await CreateCardAsync(owner, board.Id, column.Id, "Bottom");
        // Repeatedly insert directly below "Top", halving the gap each time until it must be respaced (~40 halvings).
        var below = bottom;
        for (var i = 0; i < 45; i++)
        {
            var filler = await CreateCardAsync(owner, board.Id, column.Id, $"Filler {i}");
            below = await ReadAsync<CardDto>(await owner.Http.PostAsJsonAsync(
                $"/api/boards/{board.Id}/cards/{filler.Id}/move", new MoveCardRequest(column.Id, top.Id, below.Id, filler.Version), Json));
        }

        var positions = (await GetBoardAsync(owner, board.Id)).Cards
            .Where(c => c.ColumnId == column.Id).OrderBy(c => c.Position).ToList();
        Assert.Equal("Top", positions[0].Title);
        Assert.Equal("Filler 44", positions[1].Title);
        Assert.Equal(positions.Count, positions.Select(c => c.Position).Distinct().Count());
        Assert.True(positions.Zip(positions.Skip(1)).All(p => p.Second.Position - p.First.Position >= Ranking.MinGap));
    }

    [Fact]
    public async Task Delete_RemovesCard()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        var card = await CreateCardAsync(owner, board.Id, column.Id, "Temp");

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync($"/api/boards/{board.Id}/cards/{card.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.DeleteAsync($"/api/boards/{board.Id}/cards/{card.Id}")).StatusCode);
    }

    [Fact]
    public async Task CardIds_FromAnotherBoard_AreNotReachable()
    {
        var owner = await CreateUserAsync();
        var boardA = await CreateBoardAsync(owner, "A");
        var boardB = await CreateBoardAsync(owner, "B");
        var column = (await GetBoardAsync(owner, boardA.Id)).Columns[0];
        var card = await CreateCardAsync(owner, boardA.Id, column.Id, "Belongs to A");

        var response = await owner.Http.PutAsJsonAsync($"/api/boards/{boardB.Id}/cards/{card.Id}", new EditCardRequest("x", null, 1), Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
