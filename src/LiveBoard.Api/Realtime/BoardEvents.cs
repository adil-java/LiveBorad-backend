using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Features;
using LiveBoard.Domain;
using Microsoft.AspNetCore.SignalR;

namespace LiveBoard.Api.Realtime;

/// <summary>
/// Publishes board events after a REST write has been saved.
/// The caller's own SignalR connection is excluded (it already has the result from the HTTP response),
/// identified by the <see cref="ConnectionIdHeader"/> request header.
/// </summary>
public sealed class BoardEvents(
    IHubContext<BoardHub, IBoardClient> hub,
    IHttpContextAccessor httpContextAccessor,
    PresenceTracker presence,
    ActivityRepository activity,
    TimeProvider clock)
{
    public const string ConnectionIdHeader = "X-SignalR-Connection-Id";

    /// <summary>Everyone on the board except the caller's own connection.</summary>
    public IBoardClient Others(Guid boardId)
    {
        var group = BoardHub.GroupName(boardId);
        var connectionId = CallerConnectionId(boardId);
        return connectionId is null ? hub.Clients.Group(group) : hub.Clients.GroupExcept(group, connectionId);
    }

    /// <summary>Everyone on the board, including the caller.</summary>
    public IBoardClient Everyone(Guid boardId) => hub.Clients.Group(BoardHub.GroupName(boardId));

    public async Task LogAsync(Guid boardId, ClaimsPrincipal actor, ActivityType type, string summary, Guid? cardId, CancellationToken ct)
    {
        var entry = ActivityEntry.Create(boardId, actor.GetUserId(), actor.GetDisplayName(), type, summary, cardId, clock.GetUtcNow().UtcDateTime);
        await activity.InsertAsync(entry, ct);
        await Everyone(boardId).ActivityAdded(entry.ToDto());
    }

    /// <summary>Disconnects a removed member from the board's live updates immediately.</summary>
    public async Task RevokeAccessAsync(Guid boardId, string userId)
    {
        var connections = presence.ConnectionsOf(boardId, userId);
        foreach (var entry in connections)
        {
            presence.Leave(entry.ConnectionId);
            await hub.Groups.RemoveFromGroupAsync(entry.ConnectionId, BoardHub.GroupName(boardId));
            await hub.Clients.Client(entry.ConnectionId).AccessRevoked(boardId);
        }

        if (connections.Count > 0)
            await Everyone(boardId).PresenceChanged(boardId, presence.UsersOn(boardId));
        if (connections.Any(c => c.InCall))
            await Everyone(boardId).CallParticipantsChanged(boardId, presence.CallOn(boardId));
    }

    /// <summary>Disconnects everyone from a room that is being deleted and tells them why.</summary>
    public async Task EndRoomAsync(Guid roomId)
    {
        var group = BoardHub.GroupName(roomId);
        foreach (var entry in presence.EntriesOn(roomId))
        {
            presence.Leave(entry.ConnectionId);
            await hub.Groups.RemoveFromGroupAsync(entry.ConnectionId, group);
            await hub.Clients.Client(entry.ConnectionId).RoomEnded(roomId);
        }
    }

    /// <summary>Applies a role change to live connections so hub permission checks see it without rejoining.</summary>
    public void UpdateLiveRole(Guid boardId, string userId, BoardRole role)
    {
        foreach (var entry in presence.ConnectionsOf(boardId, userId))
            entry.Role = role;
    }

    /// <summary>
    /// Only honour the header if that connection belongs to the caller and is on this board,
    /// so a client cannot suppress events for someone else's connection.
    /// </summary>
    private string? CallerConnectionId(Guid boardId)
    {
        var context = httpContextAccessor.HttpContext;
        var connectionId = context?.Request.Headers[ConnectionIdHeader].ToString();
        if (string.IsNullOrEmpty(connectionId) || context!.User.Identity?.IsAuthenticated != true)
            return null;

        var entry = presence.Get(connectionId);
        return entry is not null && entry.BoardId == boardId && entry.UserId == context.User.GetUserId()
            ? connectionId
            : null;
    }
}
