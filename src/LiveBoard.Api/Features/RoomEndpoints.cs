using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

/// <summary>
/// Call rooms: a signed-in user creates one and shares its code or link; anyone with it can join the call,
/// including guests. Looking a room up is open to guests; creating, listing and ending rooms need an account.
/// </summary>
public static class RoomEndpoints
{
    public static RouteGroupBuilder MapRoomEndpoints(this RouteGroupBuilder rooms)
    {
        rooms.WithTags("Rooms");

        rooms.MapPost("/", CreateAsync).RequireAuthorization(AuthPolicies.Registered).WithSummary("Create a call room.");
        rooms.MapGet("/", ListAsync).RequireAuthorization(AuthPolicies.Registered).WithSummary("Rooms you own, newest first.");
        rooms.MapGet("/{code}", GetAsync)
            .WithSummary("Look up a room by its code (guests allowed).")
            .WithDescription("Accepts the code with or without dashes, in any case.");
        rooms.MapDelete("/{code}", DeleteAsync)
            .RequireAuthorization(AuthPolicies.Registered)
            .WithSummary("End a room you own: everyone in it is disconnected and the code stops working.");

        return rooms;
    }

    private static async Task<Created<RoomDto>> CreateAsync(
        CreateRoomRequest request, ClaimsPrincipal user, RoomRepository rooms, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await rooms.CountByOwnerAsync(userId, ct) >= Room.MaxRoomsPerOwner)
            throw new DomainValidationException($"You can have at most {Room.MaxRoomsPerOwner} rooms. End some you no longer use.");

        var room = Room.Create(request?.Name, userId, user.GetDisplayName(), clock.GetUtcNow().UtcDateTime);
        await rooms.InsertAsync(room, ct);
        return TypedResults.Created($"/api/rooms/{room.Code}", room.ToDto(0));
    }

    private static async Task<Ok<List<RoomDto>>> ListAsync(ClaimsPrincipal user, RoomRepository rooms, PresenceTracker presence, CancellationToken ct)
    {
        var list = await rooms.ListByOwnerAsync(user.GetUserId(), ct);
        return TypedResults.Ok(list.Select(r => r.ToDto(presence.CallOn(r.Id).Count)).ToList());
    }

    private static async Task<Ok<RoomDto>> GetAsync(string code, RoomRepository rooms, PresenceTracker presence, CancellationToken ct)
    {
        var room = await FindAsync(code, rooms, ct);
        return TypedResults.Ok(room.ToDto(presence.CallOn(room.Id).Count));
    }

    private static async Task<NoContent> DeleteAsync(
        string code, ClaimsPrincipal user, RoomRepository rooms, BoardEvents events, CancellationToken ct)
    {
        var room = await FindAsync(code, rooms, ct);
        if (room.OwnerId != user.GetUserId())
            throw new AccessDeniedException("Only the person who created this room can end it.");

        await rooms.DeleteAsync(room.Id, ct);
        await events.EndRoomAsync(room.Id);
        return TypedResults.NoContent();
    }

    private static async Task<Room> FindAsync(string code, RoomRepository rooms, CancellationToken ct)
    {
        var normalized = Room.NormalizeCode(code) ?? throw new EntityNotFoundException("Room not found.");
        return await rooms.GetByCodeAsync(normalized, ct) ?? throw new EntityNotFoundException("Room not found.");
    }
}
