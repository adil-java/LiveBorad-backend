using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using LiveBoard.Api.Features;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace LiveBoard.Api.Tests;

public class BoardApiTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Health_ReportsHealthy_WithoutAuthentication()
    {
        var response = await Factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_RequiresAuthentication()
    {
        var response = await Factory.CreateClient().GetAsync("/api/boards");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_RejectsTokenSignedWithAnotherKey()
    {
        var forged = TestTokens.Create("attacker", "a@example.com", "A", true,
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-signing-key-0123456789")));
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/boards")).StatusCode);
    }

    [Fact]
    public async Task Sync_CreatesProfileFromTokenClaims()
    {
        var user = await CreateUserAsync("dana", sync: false);

        var me = await SyncAsync(user);

        Assert.Equal(user.Id, me.UserId);
        Assert.Equal(user.Email, me.Email);
        Assert.Equal("dana", me.DisplayName);
        Assert.True(me.EmailVerified);
    }

    [Fact]
    public async Task CreateBoard_HasDefaultColumns_AndAppearsInOwnersList()
    {
        var owner = await CreateUserAsync("owner");

        var created = await CreateBoardAsync(owner, "Sprint 42");
        var board = await GetBoardAsync(owner, created.Id);
        var list = await ReadAsync<List<BoardSummaryDto>>(await owner.Http.GetAsync("/api/boards"));

        Assert.Equal(BoardRole.Owner, created.Role);
        Assert.Equal(["To do", "In progress", "Done"], board.Columns.Select(c => c.Name));
        Assert.Contains(board.Members, m => m.UserId == owner.Id && m.DisplayName == "owner");
        Assert.Contains(list, b => b.Id == created.Id && b.Name == "Sprint 42");
    }

    [Fact]
    public async Task CreateBoard_WithBlankName_Returns400Problem()
    {
        var owner = await CreateUserAsync();

        var response = await owner.Http.PostAsJsonAsync("/api/boards", new CreateBoardRequest("  "), Json);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Board name is required.", problem!.Detail);
    }

    [Fact]
    public async Task NonMember_GetsNotFound_SoBoardIdsCannotBeProbed()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.DoesNotContain(await ReadAsync<List<BoardSummaryDto>>(await stranger.Http.GetAsync("/api/boards")), b => b.Id == board.Id);
    }

    [Fact]
    public async Task OnlyOwner_CanRenameBoard()
    {
        var owner = await CreateUserAsync();
        var editor = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);

        var denied = await editor.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new RenameBoardRequest("Hijacked"), Json);
        var renamed = await ReadAsync<BoardSummaryDto>(
            await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new RenameBoardRequest("Renamed"), Json));

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("Renamed", renamed.Name);
    }

    [Fact]
    public async Task DeleteBoard_RemovesItForEveryone()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        await CreateCardAsync(owner, board.Id, column.Id, "Doomed card");

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task Columns_CanBeAddedRenamedAndRemoved_ButNotWhileHoldingCards()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var basePath = $"/api/boards/{board.Id}/columns";

        var review = await ReadAsync<ColumnDto>(await owner.Http.PostAsJsonAsync(basePath, new ColumnNameRequest("Review"), Json), HttpStatusCode.Created);
        var renamed = await ReadAsync<ColumnDto>(await owner.Http.PatchAsJsonAsync($"{basePath}/{review.Id}", new ColumnNameRequest("QA"), Json));
        await CreateCardAsync(owner, board.Id, review.Id, "Blocking card");
        var blocked = await owner.Http.DeleteAsync($"{basePath}/{review.Id}");

        var columns = (await GetBoardAsync(owner, board.Id)).Columns;
        Assert.Equal("QA", renamed.Name);
        Assert.Equal("QA", columns[^1].Name); // appended at the end
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
    }

    [Fact]
    public async Task Activity_RecordsChanges_NewestFirst()
    {
        var owner = await CreateUserAsync("owner");
        var board = await CreateBoardAsync(owner, "Feed");
        var column = (await GetBoardAsync(owner, board.Id)).Columns[0];
        await CreateCardAsync(owner, board.Id, column.Id, "First task");

        var feed = await ReadAsync<List<ActivityDto>>(await owner.Http.GetAsync($"/api/boards/{board.Id}/activity"));

        Assert.Equal([ActivityType.CardCreated, ActivityType.BoardCreated], feed.Select(a => a.Type));
        Assert.Equal("added \"First task\" to To do", feed[0].Summary);
        Assert.Equal("owner", feed[0].ActorName);
    }
}
