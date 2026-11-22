using System.Net;
using System.Net.Http.Json;
using LiveBoard.Api.Features;
using LiveBoard.Api.Realtime;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LiveBoard.Api.Tests;

public class ChatApiTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    private static async Task<ChatMessageDto> SendAsync(TestUser user, Guid boardId, string text, string? connectionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{boardId}/chat")
        {
            Content = JsonContent.Create(new SendChatMessageRequest(text), options: Json),
        };
        if (connectionId is not null)
            request.Headers.Add(BoardEvents.ConnectionIdHeader, connectionId);
        return await ReadAsync<ChatMessageDto>(await user.Http.SendAsync(request), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Viewer_CanChat_AndHistoryIsNewestFirstWithPaging()
    {
        var owner = await CreateUserAsync("alice");
        var viewer = await CreateUserAsync("bob");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);

        await SendAsync(owner, board.Id, "first");
        await Task.Delay(5); // distinct timestamps for the keyset cursor
        await SendAsync(viewer, board.Id, "second");
        await Task.Delay(5);
        var third = await SendAsync(owner, board.Id, "  third  ");

        Assert.Equal("third", third.Text);
        Assert.Equal("alice", third.AuthorName);

        var page1 = await ReadAsync<List<ChatMessageDto>>(await viewer.Http.GetAsync($"/api/boards/{board.Id}/chat?limit=2"));
        Assert.Equal(["third", "second"], page1.Select(m => m.Text));
        Assert.Equal("bob", page1[1].AuthorName);

        var before = Uri.EscapeDataString(page1[^1].CreatedAt.ToString("O"));
        var page2 = await ReadAsync<List<ChatMessageDto>>(await viewer.Http.GetAsync($"/api/boards/{board.Id}/chat?limit=2&before={before}"));
        Assert.Equal(["first"], page2.Select(m => m.Text));
    }

    [Fact]
    public async Task BlankOrTooLongMessages_AreRejected()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);

        var blank = await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/chat", new SendChatMessageRequest("   "), Json);
        var tooLong = await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/chat", new SendChatMessageRequest(new string('x', ChatMessage.MaxTextLength + 1)), Json);

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task NonMember_CannotReadOrSend()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await SendAsync(owner, board.Id, "private");

        var read = await stranger.Http.GetAsync($"/api/boards/{board.Id}/chat");
        var send = await stranger.Http.PostAsJsonAsync($"/api/boards/{board.Id}/chat", new SendChatMessageRequest("hi"), Json);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
    }

    [Fact]
    public async Task AuthorAndOwner_CanDelete_OtherMembersCannot()
    {
        var owner = await CreateUserAsync();
        var editor = await CreateUserAsync();
        var other = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);
        await AddMemberAsync(owner, board.Id, other, BoardRole.Editor);

        var mine = await SendAsync(editor, board.Id, "mine");
        var moderated = await SendAsync(editor, board.Id, "rude");

        Assert.Equal(HttpStatusCode.Forbidden, (await other.Http.DeleteAsync($"/api/boards/{board.Id}/chat/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.Http.DeleteAsync($"/api/boards/{board.Id}/chat/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync($"/api/boards/{board.Id}/chat/{moderated.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.DeleteAsync($"/api/boards/{board.Id}/chat/{mine.Id}")).StatusCode);

        var history = await ReadAsync<List<ChatMessageDto>>(await owner.Http.GetAsync($"/api/boards/{board.Id}/chat"));
        Assert.Empty(history);
    }

    [Fact]
    public async Task Messages_ArePushedToOthers_ButNotEchoedToTheSender()
    {
        var owner = await CreateUserAsync("alice");
        var viewer = await CreateUserAsync("bob");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);

        await using var aliceHub = await ConnectAsync(owner);
        await using var bobHub = await ConnectAsync(viewer);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        await bobHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var bobReceives = NextEvent<ChatMessageDto>(bobHub, "ChatMessageAdded");
        var aliceReceives = NextEvent<ChatMessageDto>(aliceHub, "ChatMessageAdded");

        await SendAsync(owner, board.Id, "from alice", connectionId: aliceHub.ConnectionId);
        Assert.Equal("from alice", (await bobReceives).Text);

        await SendAsync(viewer, board.Id, "from bob", connectionId: bobHub.ConnectionId);
        // Alice's first event is Bob's message, which proves her own was not echoed back to her.
        Assert.Equal("from bob", (await aliceReceives).Text);

        var deleted = NextEvent<Guid, Guid>(bobHub, "ChatMessageDeleted");
        var history = await ReadAsync<List<ChatMessageDto>>(await owner.Http.GetAsync($"/api/boards/{board.Id}/chat"));
        await owner.Http.DeleteAsync($"/api/boards/{board.Id}/chat/{history[^1].Id}");
        Assert.Equal(history[^1].Id, (await deleted).Item2);
    }

    [Fact]
    public async Task TypingIndicator_ReachesOthers()
    {
        var owner = await CreateUserAsync("alice");
        var viewer = await CreateUserAsync("bob");
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);

        await using var aliceHub = await ConnectAsync(owner);
        await using var bobHub = await ConnectAsync(viewer);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        await bobHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        var typing = NextEvent<Guid, PresenceUserDto>(aliceHub, "ChatTyping");
        await bobHub.InvokeAsync("Typing");

        var (boardId, user) = await typing;
        Assert.Equal(board.Id, boardId);
        Assert.Equal("bob", user.DisplayName);
    }

    [Fact]
    public async Task DeletingBoard_DeletesItsChat()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await SendAsync(owner, board.Id, "soon gone");

        await owner.Http.DeleteAsync($"/api/boards/{board.Id}");

        var chat = Factory.Services.GetRequiredService<Data.ChatRepository>();
        Assert.Empty(await chat.ListAsync(board.Id, null, 10, CancellationToken.None));
    }
}
