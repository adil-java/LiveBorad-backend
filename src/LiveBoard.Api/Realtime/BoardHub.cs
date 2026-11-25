using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Features;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LiveBoard.Api.Realtime;

/// <summary>
/// The hub carries only ephemeral collaboration signals (presence, "is editing", typing, drawing previews and
/// voice/video call signalling).
/// All persistent changes go through the REST API, which validates and saves them and then broadcasts
/// through <see cref="BoardEvents"/>. This keeps a single, testable write path.
/// </summary>
[Authorize]
public sealed class BoardHub(PresenceTracker presence, BoardRepository boards, BoardEvents events, TimeProvider clock) : Hub<IBoardClient>
{
    /// <summary>Calls are peer-to-peer (every participant sends to every other), which stays smooth up to about this size.</summary>
    public const int MaxCallParticipants = 6;

    /// <summary>Offers/answers are a few KB; SignalR's default message limit is 32 KB.</summary>
    private const int MaxSignalLength = 30_000;

    private static readonly HashSet<string> SignalTypes = ["offer", "answer", "candidate"];

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
        await Clients.Caller.CallParticipantsChanged(boardId, presence.CallOn(boardId)); // so a running call shows up
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

    // ---- Voice/video calls ---------------------------------------------------------------------
    // Media flows directly between browsers (WebRTC). The server only tracks who is in the call and relays
    // the signalling messages browsers need to connect. Any member may join, including viewers.

    /// <summary>
    /// Joins the board's call and returns the participants already in it. The joiner sends each of them an offer;
    /// existing participants only answer, so two peers never both start a negotiation.
    /// </summary>
    public async Task<IReadOnlyList<CallParticipantDto>> JoinCall(bool micOn, bool cameraOn)
    {
        var entry = RequireJoined();
        var existing = presence.TryJoinCall(entry, MaxCallParticipants, clock.GetUtcNow().UtcDateTime)
            ?? throw new HubException($"This call is full ({MaxCallParticipants} people).");

        entry.MicOn = micOn;
        entry.CameraOn = cameraOn;
        entry.ScreenSharing = false;
        await Clients.Group(GroupName(entry.BoardId)).CallParticipantsChanged(entry.BoardId, presence.CallOn(entry.BoardId));

        if (existing.Count == 0)
            await events.LogAsync(entry.BoardId, Context.User!, ActivityType.CallStarted, "started a call", null, Context.ConnectionAborted);
        return existing;
    }

    public async Task LeaveCall()
    {
        var entry = presence.Get(Context.ConnectionId);
        if (entry is null || !entry.InCall)
            return;
        presence.LeaveCall(entry);
        await Clients.Group(GroupName(entry.BoardId)).CallParticipantsChanged(entry.BoardId, presence.CallOn(entry.BoardId));
    }

    /// <summary>Mic/camera/screen-share flags, so others can show "muted" or a name instead of a blank tile.</summary>
    public async Task UpdateCallState(bool micOn, bool cameraOn, bool screenSharing)
    {
        var entry = RequireInCall();
        entry.MicOn = micOn;
        entry.CameraOn = cameraOn;
        entry.ScreenSharing = screenSharing;
        await Clients.Group(GroupName(entry.BoardId)).CallParticipantsChanged(entry.BoardId, presence.CallOn(entry.BoardId));
    }

    /// <summary>Relays an offer, answer or ICE candidate to one other participant of the same call.</summary>
    public async Task SendSignal(string targetConnectionId, string type, string payload)
    {
        var entry = RequireInCall();
        if (!SignalTypes.Contains(type))
            throw new HubException("Unknown signal type.");
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxSignalLength)
            throw new HubException("Signal payload is empty or too large.");

        var target = presence.Get(targetConnectionId);
        if (target is null || !target.InCall || target.BoardId != entry.BoardId || target.ConnectionId == entry.ConnectionId)
            return; // the peer left meanwhile; its departure is announced separately

        await Clients.Client(targetConnectionId).CallSignal(entry.BoardId, Context.ConnectionId, type, payload);
    }

    private PresenceEntry RequireJoined() =>
        presence.Get(Context.ConnectionId) ?? throw new HubException("Join a board first.");

    private PresenceEntry RequireInCall()
    {
        var entry = RequireJoined();
        return entry.InCall ? entry : throw new HubException("Join the call first.");
    }

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
        if (entry.InCall)
            await group.CallParticipantsChanged(entry.BoardId, presence.CallOn(entry.BoardId));
        await group.PresenceChanged(entry.BoardId, presence.UsersOn(entry.BoardId));
    }
}
