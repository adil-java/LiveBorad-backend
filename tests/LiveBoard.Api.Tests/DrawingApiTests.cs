using System.Net;
using System.Net.Http.Json;
using LiveBoard.Api.Features;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LiveBoard.Api.Tests;

public class DrawingApiTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    private static AddStrokeRequest Line(Guid? id = null, string color = "#1F2937", double width = 4, params double[] points) =>
        new(id ?? Guid.NewGuid(), StrokeTool.Pen, color, width, points.Length > 0 ? points : [10, 10, 50, 50, 90, 20]);

    private static Task<HttpResponseMessage> PostStroke(TestUser user, Guid boardId, AddStrokeRequest stroke) =>
        user.Http.PostAsJsonAsync($"/api/boards/{boardId}/drawing/strokes", stroke, Json);

    private static async Task<List<StrokeDto>> ListAsync(TestUser user, Guid boardId) =>
        await ReadAsync<List<StrokeDto>>(await user.Http.GetAsync($"/api/boards/{boardId}/drawing"));

    [Fact]
    public async Task Editor_SavesStrokes_AndEveryMemberSeesThemInDrawingOrder()
    {
        var owner = await CreateUserAsync();
        var viewer = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);

        var first = await ReadAsync<StrokeDto>(await PostStroke(owner, board.Id, Line()), HttpStatusCode.Created);
        await Task.Delay(5);
        var second = await ReadAsync<StrokeDto>(await PostStroke(owner, board.Id, Line(color: "#EF4444")), HttpStatusCode.Created);

        Assert.Equal("#1f2937", first.Color); // normalized
        Assert.Equal(owner.Id, first.AuthorId);
        Assert.Equal([first.Id, second.Id], (await ListAsync(viewer, board.Id)).Select(s => s.Id));
    }

    [Fact]
    public async Task Points_AreClampedToTheCanvas_AndRounded()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);

        var stroke = await ReadAsync<StrokeDto>(
            await PostStroke(owner, board.Id, Line(points: [-20, 5.04, 2000, 1500.7])), HttpStatusCode.Created);

        Assert.Equal([0, 5, Stroke.CanvasWidth, Stroke.CanvasHeight], stroke.Points);
    }

    [Theory]
    [InlineData("red", 4)]
    [InlineData("#12345", 4)]
    [InlineData("#123456", 0)]
    [InlineData("#123456", 100)]
    public async Task InvalidColorOrWidth_IsRejected(string color, double width)
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);

        var response = await PostStroke(owner, board.Id, Line(color: color, width: width));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OddNumberOfCoordinates_IsRejected()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);

        var response = await PostStroke(owner, board.Id, Line(points: [1, 2, 3]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_CannotDraw_EraseOrClear()
    {
        var owner = await CreateUserAsync();
        var viewer = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);
        var stroke = await ReadAsync<StrokeDto>(await PostStroke(owner, board.Id, Line()), HttpStatusCode.Created);

        var draw = await PostStroke(viewer, board.Id, Line());
        var erase = await viewer.Http.PostAsJsonAsync($"/api/boards/{board.Id}/drawing/erase", new EraseStrokesRequest([stroke.Id]), Json);
        var clear = await viewer.Http.DeleteAsync($"/api/boards/{board.Id}/drawing");

        Assert.Equal(HttpStatusCode.Forbidden, draw.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, erase.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clear.StatusCode);
        Assert.Single(await ListAsync(owner, board.Id));
    }

    [Fact]
    public async Task ResendingTheSameStroke_ReturnsTheSavedOne_ButAnotherUserCannotReuseItsId()
    {
        var owner = await CreateUserAsync();
        var editor = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);
        var request = Line();

        var created = await PostStroke(owner, board.Id, request);
        var retried = await PostStroke(owner, board.Id, request);
        var hijack = await PostStroke(editor, board.Id, request);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, hijack.StatusCode);
        Assert.Single(await ListAsync(owner, board.Id));
    }

    [Fact]
    public async Task Erase_OnlyTouchesThisBoardsStrokes()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var otherBoard = await CreateBoardAsync(owner, "Other");
        var keep = await ReadAsync<StrokeDto>(await PostStroke(owner, board.Id, Line()), HttpStatusCode.Created);
        var erase = await ReadAsync<StrokeDto>(await PostStroke(owner, board.Id, Line()), HttpStatusCode.Created);
        var elsewhere = await ReadAsync<StrokeDto>(await PostStroke(owner, otherBoard.Id, Line()), HttpStatusCode.Created);

        var response = await owner.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/drawing/erase", new EraseStrokesRequest([erase.Id, elsewhere.Id]), Json);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([keep.Id], (await ListAsync(owner, board.Id)).Select(s => s.Id));
        Assert.Equal([elsewhere.Id], (await ListAsync(owner, otherBoard.Id)).Select(s => s.Id));
    }

    [Fact]
    public async Task StrokesErasesAndClears_ArePushedLive_AndClearingIsLogged()
    {
        var owner = await CreateUserAsync("alice");
        var viewer = await CreateUserAsync("bob");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);

        await using var bobHub = await ConnectAsync(viewer);
        await bobHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var added = NextEvent<StrokeDto>(bobHub, "StrokeAdded");
        var a = await ReadAsync<StrokeDto>(await PostStroke(owner, board.Id, Line()), HttpStatusCode.Created);
        Assert.Equal(a.Id, (await added).Id);

        var erased = NextEvent<Guid, List<Guid>>(bobHub, "StrokesErased");
        await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/drawing/erase", new EraseStrokesRequest([a.Id, Guid.NewGuid()]), Json);
        Assert.Equal([a.Id], (await erased).Item2); // only strokes that existed

        await PostStroke(owner, board.Id, Line());
        var cleared = NextEvent<Guid>(bobHub, "DrawingCleared");
        var logged = NextEvent<ActivityDto>(bobHub, "ActivityAdded", e => e.Type == ActivityType.DrawingCleared);
        await owner.Http.DeleteAsync($"/api/boards/{board.Id}/drawing");

        Assert.Equal(board.Id, await cleared);
        Assert.Equal("cleared the whiteboard", (await logged).Summary);
        Assert.Empty(await ListAsync(owner, board.Id));
    }

    [Fact]
    public async Task StrokeInProgress_IsRelayedToOthers_ButNotFromViewers()
    {
        var owner = await CreateUserAsync("alice");
        var viewer = await CreateUserAsync("bob");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);

        await using var aliceHub = await ConnectAsync(owner);
        await using var bobHub = await ConnectAsync(viewer);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        await bobHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var received = new TaskCompletionSource<(Guid StrokeId, string User, string Color, double[] Points)>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobHub.On<Guid, Guid, PresenceUserDto, StrokeTool, string, double, double[]>("StrokeProgress",
            (_, strokeId, user, _, color, _, points) => received.TrySetResult((strokeId, user.DisplayName, color, points)));

        var strokeId = Guid.NewGuid();
        await aliceHub.InvokeAsync("DrawProgress", strokeId, StrokeTool.Pen, "#2563EB", 4.0, new double[] { 1, 2, 3, 4 });

        var progress = await received.Task.WaitAsync(EventTimeout);
        Assert.Equal(strokeId, progress.StrokeId);
        Assert.Equal("alice", progress.User);
        Assert.Equal("#2563eb", progress.Color);
        Assert.Equal([1, 2, 3, 4], progress.Points);

        await Assert.ThrowsAsync<HubException>(() =>
            bobHub.InvokeAsync("DrawProgress", Guid.NewGuid(), StrokeTool.Pen, "#000000", 4.0, new double[] { 1, 2 }));
        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.InvokeAsync("DrawProgress", Guid.NewGuid(), StrokeTool.Pen, "not-a-color", 4.0, new double[] { 1, 2 }));
    }

    [Fact]
    public async Task DeletingBoard_DeletesItsDrawing()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await PostStroke(owner, board.Id, Line());

        await owner.Http.DeleteAsync($"/api/boards/{board.Id}");

        var strokes = Factory.Services.GetRequiredService<Data.StrokeRepository>();
        Assert.Equal(0, await strokes.CountAsync(board.Id, CancellationToken.None));
    }
}
