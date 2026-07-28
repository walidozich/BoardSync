using System.Collections.Concurrent;

namespace BoardSync.Api.Presence;

public sealed record PresenceUser(Guid Id, string DisplayName);

/// <summary>
/// Tracks which users are currently connected, keyed by user id with a set of connection
/// ids per user -- not keyed by connection id. Two tabs for the same person are one entry
/// in the roster, and closing one of them doesn't remove that person until their last
/// connection actually closes. Keying by connection id instead is the naive version and
/// gets both of those wrong.
/// </summary>
public sealed class PresenceTracker
{
    private sealed class Entry
    {
        public required string DisplayName { get; init; }
        public HashSet<string> ConnectionIds { get; } = [];
    }

    private readonly ConcurrentDictionary<Guid, Entry> _users = new();
    private readonly Lock _lock = new();

    /// <returns>true if this is the user's first active connection (a roster change).</returns>
    public bool AddConnection(Guid userId, string displayName, string connectionId)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(userId, out var existing))
            {
                existing.ConnectionIds.Add(connectionId);
                return false;
            }

            var entry = new Entry { DisplayName = displayName };
            entry.ConnectionIds.Add(connectionId);
            _users[userId] = entry;
            return true;
        }
    }

    /// <returns>true if this was the user's last active connection (a roster change).</returns>
    public bool RemoveConnection(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var entry))
            {
                return false;
            }

            entry.ConnectionIds.Remove(connectionId);
            if (entry.ConnectionIds.Count > 0)
            {
                return false;
            }

            _users.TryRemove(userId, out _);
            return true;
        }
    }

    public IReadOnlyList<PresenceUser> ConnectedUsers()
    {
        lock (_lock)
        {
            return _users.Select(kv => new PresenceUser(kv.Key, kv.Value.DisplayName)).ToList();
        }
    }
}
