using System.Net.Http.Json;
using LiveBoard.Api.Features;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace LiveBoard.Api.Tests;

public class RealtimeTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task CardCreatedByOneMember_IsPushedToOthers_ButNotEchoedToTheAuthor()
    {
        var owner = await CreateUserAsync("owner");
        var editor = await CreateUserAsync("editor");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];

        await using var ownerHub = await ConnectAsync(owner);
        await using var editorHub = await ConnectAsync(editor);
        await ownerHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        await editorHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var ownerSawCard = false;
        ownerHub.On<CardDto>("CardCreated", _ => ownerSawCard = true);
        var editorReceives = NextEvent<CardDto>(editorHub, "CardCreated");
        var ownerActivity = NextEvent<ActivityDto>(ownerHub, "ActivityAdded", a => a.Type == ActivityType.CardCreated);

        var created = await CreateCardAsync(owner, board.Id, column.Id, "Live card", connectionId: ownerHub.ConnectionId);

        var pushed = await editorReceives;
        Assert.Equal(created.Id, pushed.Id);
        Assert.Equal("Live card", pushed.Title);

        // ActivityAdded is sent after CardCreated and messages to one connection arrive in order,
        // so once the owner has the activity entry we know CardCreated was not sent to them.
        await ownerActivity;
        Assert.False(ownerSawCard);
    }

    [Fact]
    public async Task MovesAndEdits_ArePushedToOtherViewers()
    {
        var owner = await CreateUserAsync();
        var viewer = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);
        var columns = (await GetBoardAsync(owner, board.Id)).Columns;
        var card = await CreateCardAsync(owner, board.Id, columns[0].Id, "Card");

        await using var viewerHub = await ConnectAsync(viewer);
        await viewerHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var moved = NextEvent<CardDto>(viewerHub, "CardMoved");
        await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/cards/{card.Id}/move", new MoveCardRequest(columns[1].Id, null, null, 1), Json);
        Assert.Equal(columns[1].Id, (await moved).ColumnId);

        var updated = NextEvent<CardDto>(viewerHub, "CardUpdated");
        await owner.Http.PutAsJsonAsync($"/api/boards/{board.Id}/cards/{card.Id}", new EditCardRequest("Renamed", "Notes", 2), Json);
        var edit = await updated;
        Assert.Equal("Renamed", edit.Title);
        Assert.Equal(3, edit.Version);
    }

    [Fact]
    public async Task Presence_ShowsWhoIsViewing_AndUpdatesWhenSomeoneLeaves()
    {
        var owner = await CreateUserAsync("alice");
        var editor = await CreateUserAsync("bob");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);

        await using var aliceHub = await ConnectAsync(owner);
        var aliceJoin = await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var bobArrives = NextEvent<Guid, List<PresenceUserDto>>(aliceHub, "PresenceChanged", (_, users) => users.Count == 2);
        var bobHub = await ConnectAsync(editor);
        var bobJoin = await bobHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var (_, withBob) = await bobArrives;

        var bobLeaves = NextEvent<Guid, List<PresenceUserDto>>(aliceHub, "PresenceChanged", (_, users) => users.Count == 1);
        await bobHub.DisposeAsync(); // disconnecting counts as leaving
        var (_, withoutBob) = await bobLeaves;

        Assert.Equal(["alice"], aliceJoin.Select(u => u.DisplayName));
        Assert.Equal(["alice", "bob"], bobJoin.Select(u => u.DisplayName));
        Assert.Equal(["alice", "bob"], withBob.Select(u => u.DisplayName));
        Assert.Equal(["alice"], withoutBob.Select(u => u.DisplayName));
    }

    [Fact]
    public async Task EditingIndicator_IsBroadcast_AndClearedOnDisconnect()
    {
        var owner = await CreateUserAsync("alice");
        var editor = await CreateUserAsync("bob");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        var card = await CreateCardAsync(owner, board.Id, column.Id, "Card");

        await using var aliceHub = await ConnectAsync(owner);
        var bobHub = await ConnectAsync(editor);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        await bobHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var editing = new List<(Guid Card, string User, bool IsEditing)>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aliceHub.On<Guid, Guid, PresenceUserDto, bool>("CardEditing", (_, cardId, user, isEditing) =>
        {
            editing.Add((cardId, user.DisplayName, isEditing));
            (isEditing ? started : stopped).TrySetResult();
        });

        await bobHub.InvokeAsync("StartEditing", card.Id);
        await started.Task.WaitAsync(EventTimeout);
        await bobHub.DisposeAsync(); // closed the tab mid-edit
        await stopped.Task.WaitAsync(EventTimeout);

        Assert.Equal([(card.Id, "bob", true), (card.Id, "bob", false)], editing);
    }

    [Fact]
    public async Task NonMember_CannotJoinABoardsLiveUpdates()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);

        await using var hub = await ConnectAsync(stranger);

        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id));
    }

    [Fact]
    public async Task Viewer_CannotBroadcastEditingIndicators()
    {
        var owner = await CreateUserAsync();
        var viewer = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);

        await using var hub = await ConnectAsync(viewer);
        await hub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("StartEditing", Guid.NewGuid()));
    }

    [Fact]
    public async Task RemovedMember_IsCutOffFromLiveUpdatesImmediately()
    {
        var owner = await CreateUserAsync();
        var member = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, member, BoardRole.Editor);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];

        await using var memberHub = await ConnectAsync(member);
        await memberHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var revoked = NextEvent<Guid>(memberHub, "AccessRevoked");
        var leakedCard = false;
        memberHub.On<CardDto>("CardCreated", _ => leakedCard = true);

        await owner.Http.DeleteAsync($"/api/boards/{board.Id}/members/{member.Id}");
        Assert.Equal(board.Id, await revoked);

        // A later change must not reach them. Use a still-joined probe connection to know when delivery is done.
        await using var ownerHub = await ConnectAsync(owner);
        await ownerHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var probe = NextEvent<CardDto>(ownerHub, "CardCreated");
        await CreateCardAsync(owner, board.Id, column.Id, "Secret plans");
        await probe;
        Assert.False(leakedCard);
    }
}
