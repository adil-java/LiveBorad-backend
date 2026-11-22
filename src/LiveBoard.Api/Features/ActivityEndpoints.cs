using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class ActivityEndpoints
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;

    public static RouteGroupBuilder MapActivityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/boards/{boardId:guid}/activity", ListAsync)
            .WithTags("Activity")
            .WithSummary("Activity feed, newest first. Page with ?before=<createdAt of the oldest entry you have>. Requires Viewer.");
        return api;
    }

    private static async Task<Ok<List<ActivityDto>>> ListAsync(
        Guid boardId, DateTime? before, int? limit, ClaimsPrincipal user, BoardAccess access, ActivityRepository activity, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Viewer, ct);
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var entries = await activity.ListAsync(boardId, before?.ToUniversalTime(), take, ct);
        return TypedResults.Ok(entries.Select(e => e.ToDto()).ToList());
    }
}
