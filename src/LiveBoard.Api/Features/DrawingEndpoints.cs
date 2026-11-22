using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class DrawingEndpoints
{
    private const int MaxEraseBatch = 500;

    public static RouteGroupBuilder MapDrawingEndpoints(this RouteGroupBuilder api)
    {
        var drawing = api.MapGroup("/boards/{boardId:guid}/drawing").WithTags("Whiteboard");

        drawing.MapGet("/", ListAsync).WithSummary("Every stroke on the board's whiteboard, oldest first. Requires Viewer.");
        drawing.MapPost("/strokes", AddAsync)
            .WithSummary("Save a finished stroke. Requires Editor.")
            .WithDescription("The client chooses the stroke ID. Re-sending the same stroke (e.g. after a timeout) returns the saved one.");
        drawing.MapPost("/erase", EraseAsync).WithSummary("Delete strokes by ID. Requires Editor.");
        drawing.MapDelete("/", ClearAsync).WithSummary("Clear the whole whiteboard. Requires Editor.");

        return api;
    }

    private static async Task<Ok<List<StrokeDto>>> ListAsync(
        Guid boardId, ClaimsPrincipal user, BoardAccess access, StrokeRepository strokes, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Viewer, ct);
        var list = await strokes.ListByBoardAsync(boardId, ct);
        return TypedResults.Ok(list.Select(s => s.ToDto()).ToList());
    }

    private static async Task<Results<Created<StrokeDto>, Ok<StrokeDto>, Conflict>> AddAsync(
        Guid boardId, AddStrokeRequest request, ClaimsPrincipal user, BoardAccess access, StrokeRepository strokes,
        BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await access.RequireAsync(boardId, userId, BoardRole.Editor, ct);

        var stroke = Stroke.Create(request.Id, boardId, userId, request.Tool, request.Color, request.Width, request.Points, clock.GetUtcNow().UtcDateTime);

        if (await strokes.CountAsync(boardId, ct) >= Stroke.MaxStrokesPerBoard)
            throw new DomainValidationException($"The whiteboard is full ({Stroke.MaxStrokesPerBoard} strokes). Erase or clear some first.");

        if (!await strokes.TryInsertAsync(stroke, ct))
        {
            // Same ID already saved: a retry of our own stroke is fine; anything else is a client bug or a guess.
            var existing = await strokes.GetAsync(request.Id, ct);
            return existing is not null && existing.BoardId == boardId && existing.AuthorId == userId
                ? TypedResults.Ok(existing.ToDto())
                : TypedResults.Conflict();
        }

        var dto = stroke.ToDto();
        await events.Others(boardId).StrokeAdded(dto);
        return TypedResults.Created($"/api/boards/{boardId}/drawing/strokes/{stroke.Id}", dto);
    }

    private static async Task<NoContent> EraseAsync(
        Guid boardId, EraseStrokesRequest request, ClaimsPrincipal user, BoardAccess access, StrokeRepository strokes,
        BoardEvents events, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Editor, ct);

        var ids = (request.StrokeIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
            return TypedResults.NoContent();
        if (ids.Count > MaxEraseBatch)
            throw new DomainValidationException($"Erase at most {MaxEraseBatch} strokes at a time.");

        var deleted = await strokes.DeleteManyAsync(boardId, ids, ct);
        if (deleted.Count > 0)
            await events.Others(boardId).StrokesErased(boardId, deleted);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ClearAsync(
        Guid boardId, ClaimsPrincipal user, BoardAccess access, StrokeRepository strokes, BoardEvents events, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Editor, ct);

        if (await strokes.DeleteByBoardAsync(boardId, ct) == 0)
            return TypedResults.NoContent();

        await events.Others(boardId).DrawingCleared(boardId);
        await events.LogAsync(boardId, user, ActivityType.DrawingCleared, "cleared the whiteboard", null, ct);
        return TypedResults.NoContent();
    }
}
