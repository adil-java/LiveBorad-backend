using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class ColumnEndpoints
{
    public static RouteGroupBuilder MapColumnEndpoints(this RouteGroupBuilder api)
    {
        var columns = api.MapGroup("/boards/{boardId:guid}/columns").WithTags("Columns");

        columns.MapPost("/", AddAsync).WithSummary("Add a column at the end. Requires Editor.");
        columns.MapPatch("/{columnId:guid}", RenameAsync).WithSummary("Rename a column. Requires Editor.");
        columns.MapDelete("/{columnId:guid}", RemoveAsync).WithSummary("Remove an empty column. Requires Editor.");

        return api;
    }

    private static async Task<Created<ColumnDto>> AddAsync(
        Guid boardId, ColumnNameRequest request, ClaimsPrincipal user, BoardRepository boards, BoardEvents events, CancellationToken ct)
    {
        var userId = user.GetUserId();
        Column column = null!;
        var board = await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, userId, BoardRole.Editor);
            column = b.AddColumn(request.Name);
        }, ct);

        await events.Others(boardId).ColumnsChanged(boardId, board.ToColumnDtos());
        await events.LogAsync(boardId, user, ActivityType.ColumnAdded, $"added the column \"{column.Name}\"", null, ct);
        return TypedResults.Created($"/api/boards/{boardId}/columns/{column.Id}", column.ToDto());
    }

    private static async Task<Ok<ColumnDto>> RenameAsync(
        Guid boardId, Guid columnId, ColumnNameRequest request, ClaimsPrincipal user, BoardRepository boards, BoardEvents events, CancellationToken ct)
    {
        var userId = user.GetUserId();
        string oldName = string.Empty;
        Column column = null!;
        var board = await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, userId, BoardRole.Editor);
            oldName = b.GetColumn(columnId).Name;
            column = b.RenameColumn(columnId, request.Name);
        }, ct);

        await events.Others(boardId).ColumnsChanged(boardId, board.ToColumnDtos());
        await events.LogAsync(boardId, user, ActivityType.ColumnRenamed, $"renamed the column \"{oldName}\" to \"{column.Name}\"", null, ct);
        return TypedResults.Ok(column.ToDto());
    }

    private static async Task<NoContent> RemoveAsync(
        Guid boardId, Guid columnId, ClaimsPrincipal user, BoardAccess access, BoardRepository boards, CardRepository cards,
        BoardEvents events, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var (current, _) = await access.RequireAsync(boardId, userId, BoardRole.Editor, ct);
        var name = current.GetColumn(columnId).Name;

        if (await cards.CountInColumnAsync(boardId, columnId, ct) > 0)
            throw new DomainValidationException("Move or delete the cards in this column before removing it.");

        var board = await boards.UpdateAsync(boardId, b =>
        {
            BoardAccess.Require(b, userId, BoardRole.Editor);
            b.RemoveColumn(columnId);
        }, ct);

        await events.Others(boardId).ColumnsChanged(boardId, board.ToColumnDtos());
        await events.LogAsync(boardId, user, ActivityType.ColumnRemoved, $"removed the column \"{name}\"", null, ct);
        return TypedResults.NoContent();
    }
}
