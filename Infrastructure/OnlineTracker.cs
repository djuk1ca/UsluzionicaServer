using System.Collections.Concurrent;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// In-memory praćenje online korisnika i njihovih SignalR konekcija.
///
/// Singleton servis — postoji jedna instanca za ceo životni vek aplikacije.
/// Svaki korisnik može biti konektovan sa više uređaja/tabova istovremeno,
/// pa čuvamo skup (HashSet) connectionId-ova po userId-u.
///
/// Nije perzistentno — restartom servera svi se "odjavljuju".
/// </summary>
public sealed class OnlineTracker
{
    // userId → skup aktivnih connectionId-ova
    private readonly ConcurrentDictionary<string, HashSet<string>> _connections = new();

    // Lokot za thread-safe operacije nad HashSet-om (ConcurrentDictionary je thread-safe
    // za add/remove ključeva, ali ne i za mutacije vrednosti HashSet)
    private readonly object _lock = new();

    /// <summary>Registruje novu konekciju pri OnConnectedAsync.</summary>
    public void Register(string userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set))
            {
                set = [];
                _connections[userId] = set;
            }
            set.Add(connectionId);
        }
    }

    /// <summary>
    /// Uklanja konekciju pri OnDisconnectedAsync.
    /// Vraća true ako je korisnik POTPUNO offline (nema više konekcija).
    /// </summary>
    public bool Unregister(string userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set)) return true;

            set.Remove(connectionId);

            if (set.Count != 0) return false; // još ima aktivnih konekcija

            _connections.TryRemove(userId, out _);
            return true; // korisnik je potpuno offline
        }
    }

    public bool IsOnline(string userId) => _connections.ContainsKey(userId);

    /// <summary>Vraća snapshot connectionId-ova za datog korisnika.</summary>
    public IReadOnlyList<string> GetConnections(string userId)
    {
        lock (_lock)
        {
            return _connections.TryGetValue(userId, out var set)
                ? set.ToList()
                : [];
        }
    }
}
