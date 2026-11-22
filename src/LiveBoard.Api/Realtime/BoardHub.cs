using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Features;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LiveBoard.Api.Realtime;

/// <summary>
/// The hub carries only ephemeral collaboration signals (presence, "is editing" indicators).
/// All persistent changes go through the REST API, which validates and saves them and then broadcasts
/// through <see cref="BoardEvents"/>. This keeps a single, testable write path.
/// </summary>
[Authorize]
public sealed class BoardHub(PresenceTracker presence, BoardRepository boards) : Hub<IBoardClient>
{
    public static string GroupName(Guid boardId) => $"board:{boardId}";

    /// <summary>Starts receiving a board's events. Returns the users currently viewing it.</summary>
    public async Task<IReadOnlyList<PresenceUserDto>> JoinBoard(Guid boardId)
    {
        var user = Context.User!;
        var board = await boards.GetAsync(boardId, Context.ConnectionAborted);
        var role = board?.RoleOf(user.GetUserId())
            ?? throw new HubException("Board not found.");

        await LeaveCurrentBoardAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(boardId));
        presence.Join(new PresenceEntry(Context.ConnectionId, boardId, user.GetUserId(), user.GetDisplayName(), role));

        var users = presence.UsersOn(boardId);
        await Clients.OthersInGroup(GroupName(boardId)).PresenceChanged(boardId, users);
        return users;
    }

    public Task LeaveBoard() => LeaveCurrentBoardAsync();

    public async Task StartEditing(Guid cardId)
    {
        var entry = presence.Get(Context.ConnectionId) ?? throw new HubException("Join a board first.");
        if (!entry.Role.CanEdit())
            throw new HubException("Viewers cannot edit cards.");

        if (entry.EditingCardId is Guid previous && previous != cardId)
            await Clients.OthersInGroup(GroupName(entry.BoardId)).CardEditing(entry.BoardId, previous, entry.ToUser(), false);

        entry.EditingCardId = cardId;
        await Clients.OthersInGroup(GroupName(entry.BoardId)).CardEditing(entry.BoardId, cardId, entry.ToUser(), true);
    }

    public async Task StopEditing(Guid cardId)
    {
        var entry = presence.Get(Context.ConnectionId);
        if (entry?.EditingCardId != cardId)
            return;

        entry.EditingCardId = null;
        await Clients.OthersInGroup(GroupName(entry.BoardId)).CardEditing(entry.BoardId, cardId, entry.ToUser(), false);
    }

    /// <summary>"Is typing" signal for the chat. Any member may chat, so any member may send it.</summary>
    public async Task Typing()
    {
        var entry = presence.Get(Context.ConnectionId) ?? throw new HubException("Join a board first.");
        await Clients.OthersInGroup(GroupName(entry.BoardId)).ChatTyping(entry.BoardId, entry.ToUser());
    }

    /// <summary>
    /// Relays part of a stroke while it is being drawn. Nothing is stored: the finished stroke is saved through
    /// the REST API, and clients drop previews that stop updating.
    /// </summary>
    public async Task DrawProgress(Guid strokeId, StrokeTool tool, string color, double width, double[] points)
    {
        var entry = presence.Get(Context.ConnectionId) ?? throw new HubException("Join a board first.");
        if (!entry.Role.CanEdit())
            throw new HubException("Viewers cannot draw.");

        double[] normalized;
        try
        {
            normalized = Stroke.NormalizePoints(points, MaxProgressPoints);
            color = Stroke.ValidColor(color);
            width = Stroke.ValidWidth(width);
        }
        catch (DomainValidationException ex)
        {
            throw new HubException(ex.Message);
        }

        await Clients.OthersInGroup(GroupName(entry.BoardId))
            .StrokeProgress(entry.BoardId, strokeId, entry.ToUser(), tool, color, width, normalized);
    }

    private const int MaxProgressPoints = 500;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveCurrentBoardAsync();
        await base.OnDisconnectedAsync(exception);
    }

    private async Task LeaveCurrentBoardAsync()
    {
        var entry = presence.Leave(Context.ConnectionId);
        if (entry is null)
            return;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(entry.BoardId));

        var group = Clients.Group(GroupName(entry.BoardId));
        if (entry.EditingCardId is Guid cardId)
            await group.CardEditing(entry.BoardId, cardId, entry.ToUser(), false);
        await group.PresenceChanged(entry.BoardId, presence.UsersOn(entry.BoardId));
    }
}
