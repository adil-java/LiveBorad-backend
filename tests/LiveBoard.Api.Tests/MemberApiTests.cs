using System.Net;
using System.Net.Http.Json;
using LiveBoard.Api.Features;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;

namespace LiveBoard.Api.Tests;

public class MemberApiTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task InvitingSomeoneWithoutAnAccount_CreatesPendingInvite_AcceptedOnFirstSignIn()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var email = $"newcomer-{Guid.NewGuid():N}@example.com";

        var invited = await ReadAsync<MembersDto>(await owner.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/members", new InviteMemberRequest(email.ToUpperInvariant(), BoardRole.Editor), Json));

        var newcomer = NewUser(Guid.NewGuid().ToString("N"), email, "newcomer", emailVerified: true);
        var me = await SyncAsync(newcomer);
        var detail = await GetBoardAsync(newcomer, board.Id);

        Assert.Contains(invited.PendingInvites, i => i.Email == email && i.Role == BoardRole.Editor);
        Assert.Contains(board.Id, me.JoinedBoardIds);
        Assert.Equal(BoardRole.Editor, detail.MyRole);
        Assert.Empty((await GetBoardAsync(owner, board.Id)).PendingInvites);
    }

    [Fact]
    public async Task UnverifiedEmail_CannotClaimAnInvite()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var email = $"victim-{Guid.NewGuid():N}@example.com";
        await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/members", new InviteMemberRequest(email, BoardRole.Owner), Json);

        // Someone signs up with that address but never verifies it.
        var impostor = NewUser(Guid.NewGuid().ToString("N"), email, "impostor", emailVerified: false);
        var me = await SyncAsync(impostor);

        Assert.Empty(me.JoinedBoardIds);
        Assert.Equal(HttpStatusCode.NotFound, (await impostor.Http.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task PendingInvites_AreOnlyVisibleToOwners()
    {
        var owner = await CreateUserAsync();
        var viewer = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, viewer, BoardRole.Viewer);
        await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/members", new InviteMemberRequest("someone@example.com", BoardRole.Viewer), Json);

        Assert.NotEmpty((await GetBoardAsync(owner, board.Id)).PendingInvites);
        Assert.Empty((await GetBoardAsync(viewer, board.Id)).PendingInvites);
    }

    [Fact]
    public async Task Owner_CanChangeRoles_AndRemoveMembers()
    {
        var owner = await CreateUserAsync();
        var member = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, member, BoardRole.Viewer);

        var promoted = await ReadAsync<MembersDto>(await owner.Http.PatchAsJsonAsync(
            $"/api/boards/{board.Id}/members/{member.Id}", new ChangeRoleRequest(BoardRole.Editor), Json));
        var removed = await owner.Http.DeleteAsync($"/api/boards/{board.Id}/members/{member.Id}");

        Assert.Contains(promoted.Members, m => m.UserId == member.Id && m.Role == BoardRole.Editor);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task Member_CanLeave_ButTheLastOwnerCannot()
    {
        var owner = await CreateUserAsync();
        var member = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, member, BoardRole.Viewer);

        var left = await member.Http.DeleteAsync($"/api/boards/{board.Id}/members/{member.Id}");
        var ownerLeaves = await owner.Http.DeleteAsync($"/api/boards/{board.Id}/members/{owner.Id}");

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, ownerLeaves.StatusCode);
    }

    [Fact]
    public async Task NonOwner_CannotInvite()
    {
        var owner = await CreateUserAsync();
        var editor = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await AddMemberAsync(owner, board.Id, editor, BoardRole.Editor);

        var response = await editor.Http.PostAsJsonAsync(
            $"/api/boards/{board.Id}/members", new InviteMemberRequest("friend@example.com", BoardRole.Owner), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
