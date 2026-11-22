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

    public PresenceUserDto ToUser() => new(UserId, DisplayName);
}

/// <summary>
/// Tracks which connection is viewing which board. A connection views at most one board; a user may have
/// several connections (tabs/devices) and appears once in the presence list.
/// In-memory, so it is per-instance: scaling out to several API instances requires a shared store (e.g. Redis).
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<string, PresenceEntry> _byConnection = new();

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

    public IReadOnlyList<PresenceEntry> ConnectionsOf(Guid boardId, string userId) =>
        _byConnection.Values.Where(e => e.BoardId == boardId && e.UserId == userId).ToList();
}
