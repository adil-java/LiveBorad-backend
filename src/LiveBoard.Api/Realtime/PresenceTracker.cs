using System.Collections.Concurrent;
using LiveBoard.Api.Features;
using LiveBoard.Domain;

namespace LiveBoard.Api.Realtime;

public sealed class PresenceEntry(string connectionId, Guid boardId, string userId, string displayName, BoardRole role)
{
    public string ConnectionId { get; } = connectionId;
    public Guid BoardId { get; } = boardId;
    public string UserId { get; } = userId;
    public string DisplayName { get; } = displayName;
    public BoardRole Role { get; set; } = role;
    public Guid? EditingCardId { get; set; }

    // ---- Call state (this connection's part in the board's voice/video call) ----
    public bool InCall { get; set; }
    public DateTime CallJoinedAt { get; set; }
    public bool MicOn { get; set; }
    public bool CameraOn { get; set; }
    public bool ScreenSharing { get; set; }

    public PresenceUserDto ToUser() => new(UserId, DisplayName);

    public CallParticipantDto ToCallParticipant() => new(ConnectionId, UserId, DisplayName, MicOn, CameraOn, ScreenSharing);
}

/// <summary>
/// Tracks which connection is viewing which board. A connection views at most one board; a user may have
/// several connections (tabs/devices) and appears once in the presence list.
/// In-memory, so it is per-instance: scaling out to several API instances requires a shared store (e.g. Redis).
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<string, PresenceEntry> _byConnection = new();
    private readonly Lock _callLock = new();

    public PresenceEntry? Get(string connectionId) => _byConnection.GetValueOrDefault(connectionId);

    public void Join(PresenceEntry entry) => _byConnection[entry.ConnectionId] = entry;

    public PresenceEntry? Leave(string connectionId) =>
        _byConnection.TryRemove(connectionId, out var entry) ? entry : null;

    public IReadOnlyList<PresenceUserDto> UsersOn(Guid boardId) =>
        _byConnection.Values
            .Where(e => e.BoardId == boardId)
            .GroupBy(e => e.UserId)
            .Select(g => g.First().ToUser())
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Connections in the board's call, oldest first. Calls are per connection, not per user:
    /// each browser tab in a call is its own peer.
    /// </summary>
    public IReadOnlyList<CallParticipantDto> CallOn(Guid boardId) =>
        _byConnection.Values
            .Where(e => e.BoardId == boardId && e.InCall)
            .OrderBy(e => e.CallJoinedAt)
            .Select(e => e.ToCallParticipant())
            .ToList();

    /// <summary>
    /// Adds the connection to its board's call and returns who was already in it, or null if the call is full.
    /// Atomic, so two people joining at the same moment still see each other (one of them must send the offer).
    /// </summary>
    public IReadOnlyList<CallParticipantDto>? TryJoinCall(PresenceEntry entry, int maxParticipants, DateTime now)
    {
        lock (_callLock)
        {
            var others = CallOn(entry.BoardId).Where(p => p.ConnectionId != entry.ConnectionId).ToList();
            if (!entry.InCall)
            {
                if (others.Count >= maxParticipants)
                    return null;
                entry.InCall = true;
                entry.CallJoinedAt = now;
            }
            return others;
        }
    }

    public void LeaveCall(PresenceEntry entry)
    {
        lock (_callLock)
        {
            entry.InCall = false;
            entry.MicOn = entry.CameraOn = entry.ScreenSharing = false;
        }
    }

    public IReadOnlyList<PresenceEntry> ConnectionsOf(Guid boardId, string userId) =>
        _byConnection.Values.Where(e => e.BoardId == boardId && e.UserId == userId).ToList();
}
