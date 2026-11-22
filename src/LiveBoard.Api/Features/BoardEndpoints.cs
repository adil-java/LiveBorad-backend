using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class BoardEndpoints
{
    public static RouteGroupBuilder MapBoardEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Boards");

        boards.MapGet("/", ListAsync).WithSummary("Boards the caller is a member of.");
        boards.MapPost("/", CreateAsync).WithSummary("Create a board with default columns; the caller becomes its owner.");
        boards.MapGet("/{boardId:guid}", GetAsync).WithSummary("Full board: columns, cards, members. Requires Viewer.");
        boards.MapPatch("/{boardId:guid}", RenameAsync).WithSummary("Rename a board. Requires Owner.");
        boards.MapDelete("/{boardId:guid}", DeleteAsync).WithSummary("Delete a board and all its cards. Requires Owner.");

        return api;
    }

    private static async Task<Ok<List<BoardSummaryDto>>> ListAsync(ClaimsPrincipal user, BoardRepository boards, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var list = await boards.ListForMemberAsync(userId, ct);
        return TypedResults.Ok(list.Select(b => b.ToSummary(userId)).ToList());
    }

    private static async Task<Created<BoardSummaryDto>> CreateAsync(
        CreateBoardRequest request, ClaimsPrincipal user, BoardRepository boards, BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        var board = Board.Create(request.Name, user.GetUserId(), clock.GetUtcNow().UtcDateTime);
        await boards.InsertAsync(board, ct);
        await events.LogAsync(board.Id, user, ActivityType.BoardCreated, $"created the board \"{board.Name}\"", null, ct);
        return TypedResults.Created($"/api/boards/{board.Id}", board.ToSummary(user.GetUserId()));
    }

    private static async Task<Ok<BoardDetailDto>> GetAsync(
        Guid boardId, ClaimsPrincipal user, BoardAccess access, CardRepository cards, UserRepository users, CancellationToken ct)
    {
        var (board, role) = await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Viewer, ct);
        var boardCards = await cards.ListByBoardAsync(boardId, ct);
        var profiles = await users.GetManyAsync(board.Members.Select(m => m.UserId), ct);
        return TypedResults.Ok(board.ToDetail(role, boardCards, profiles));
    }

    private static async Task<Ok<BoardSummaryDto>> RenameAsync(
        Guid boardId, RenameBoardRequest request, ClaimsPrincipal user, BoardRepository boards, BoardEvents events, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var board = await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, userId, BoardRole.Owner);
            b.Rename(request.Name);
        }, ct);

        await events.Others(boardId).BoardRenamed(boardId, board.Name);
        await events.LogAsync(boardId, user, ActivityType.BoardRenamed, $"renamed the board to \"{board.Name}\"", null, ct);
        return TypedResults.Ok(board.ToSummary(userId));
    }

    private static async Task<NoContent> DeleteAsync(
        Guid boardId, ClaimsPrincipal user, BoardAccess access, BoardRepository boards, CardRepository cards,
        ActivityRepository activity, BoardEvents events, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Owner, ct);

        // Delete the board first so it disappears for everyone at once; orphaned cards are then cleaned up.
        await boards.DeleteAsync(boardId, ct);
        await cards.DeleteByBoardAsync(boardId, ct);
        await activity.DeleteByBoardAsync(boardId, ct);

        await events.Others(boardId).BoardDeleted(boardId);
        return TypedResults.NoContent();
    }
}
