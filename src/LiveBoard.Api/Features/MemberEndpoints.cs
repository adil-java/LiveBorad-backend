using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class MemberEndpoints
{
    public static RouteGroupBuilder MapMemberEndpoints(this RouteGroupBuilder api)
    {
        var members = api.MapGroup("/boards/{boardId:guid}/members").WithTags("Members");

        members.MapGet("/", ListAsync).WithSummary("Members, plus pending invites for owners. Requires Viewer.");
        members.MapPost("/", InviteAsync)
            .WithSummary("Add a user by email. Requires Owner.")
            .WithDescription("Users with a verified account are added immediately; anyone else gets a pending invite that is accepted when they sign in with that verified email.");
        members.MapPatch("/{userId}", ChangeRoleAsync).WithSummary("Change a member's role. Requires Owner.");
        members.MapDelete("/{userId}", RemoveAsync).WithSummary("Remove a member (Owner), or leave the board yourself.");
        members.MapDelete("/invites/{email}", CancelInviteAsync).WithSummary("Cancel a pending invite. Requires Owner.");

        return api;
    }

    private static async Task<Ok<MembersDto>> ListAsync(
        Guid boardId, ClaimsPrincipal user, BoardAccess access, UserRepository users, CancellationToken ct)
    {
        var (board, role) = await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Viewer, ct);
        return TypedResults.Ok(await ToMembersAsync(board, role, users, ct));
    }

    private static async Task<Ok<MembersDto>> InviteAsync(
        Guid boardId, InviteMemberRequest request, ClaimsPrincipal user, BoardRepository boards, UserRepository users,
        BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = string.IsNullOrEmpty(email) ? null : await users.FindVerifiedByEmailAsync(email, ct);

        var board = await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, userId, BoardRole.Owner);
            if (existing is not null)
                b.AddMember(existing.Id, request.Role, now);
            else
                b.Invite(email, request.Role, userId, now);
        }, ct);

        var result = await ToMembersAsync(board, BoardRole.Owner, users, ct);
        if (existing is not null)
        {
            await events.Others(boardId).MembersChanged(boardId, result.Members);
            await events.LogAsync(boardId, user, ActivityType.MemberAdded, $"added {existing.DisplayName} as {request.Role}", null, ct);
        }
        else
        {
            await events.LogAsync(boardId, user, ActivityType.MemberInvited, $"invited {email} as {request.Role}", null, ct);
        }
        return TypedResults.Ok(result);
    }

    private static async Task<Ok<MembersDto>> ChangeRoleAsync(
        Guid boardId, string userId, ChangeRoleRequest request, ClaimsPrincipal user, BoardRepository boards, UserRepository users,
        BoardEvents events, CancellationToken ct)
    {
        var callerId = user.GetUserId();
        var board = await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, callerId, BoardRole.Owner);
            b.ChangeMemberRole(userId, request.Role);
        }, ct);

        events.UpdateLiveRole(boardId, userId, request.Role);
        var result = await ToMembersAsync(board, BoardRole.Owner, users, ct);
        await events.Others(boardId).MembersChanged(boardId, result.Members);
        var name = result.Members.FirstOrDefault(m => m.UserId == userId)?.DisplayName ?? "a member";
        await events.LogAsync(boardId, user, ActivityType.MemberRoleChanged, $"made {name} {request.Role}", null, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<NoContent> RemoveAsync(
        Guid boardId, string userId, ClaimsPrincipal user, BoardRepository boards, UserRepository users, BoardEvents events, CancellationToken ct)
    {
        var callerId = user.GetUserId();
        var leaving = callerId == userId;
        var board = await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, callerId, leaving ? BoardRole.Viewer : BoardRole.Owner);
            b.RemoveMember(userId);
        }, ct);

        await events.RevokeAccessAsync(boardId, userId);
        var profiles = await users.GetManyAsync(board.Members.Select(m => m.UserId).Append(userId), ct);
        await events.Everyone(boardId).MembersChanged(boardId, board.ToMembers(BoardRole.Viewer, profiles).Members);

        var name = profiles.TryGetValue(userId, out var p) ? p.DisplayName : "a member";
        await events.LogAsync(boardId, user, ActivityType.MemberRemoved, leaving ? "left the board" : $"removed {name}", null, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> CancelInviteAsync(
        Guid boardId, string email, ClaimsPrincipal user, BoardRepository boards, CancellationToken ct)
    {
        var callerId = user.GetUserId();
        await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, callerId, BoardRole.Owner);
            b.CancelInvite(email);
        }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<MembersDto> ToMembersAsync(Board board, BoardRole viewerRole, UserRepository users, CancellationToken ct) =>
        board.ToMembers(viewerRole, await users.GetManyAsync(board.Members.Select(m => m.UserId), ct));
}
