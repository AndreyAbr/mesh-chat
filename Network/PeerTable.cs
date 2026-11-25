using System.Collections.Concurrent;
using MeshChat.Protocol;

namespace MeshChat.Network;

public sealed class PeerTable
{
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);
    private readonly HashSet<string>? _allowedPeers;

    public PeerTable(IEnumerable<string>? allowedPeers = null)
    {
        if (allowedPeers is not null)
        {
            var set = allowedPeers
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToHashSet(StringComparer.Ordinal);
            if (set.Count > 0)
                _allowedPeers = set;
        }
    }

    public bool IsAllowed(string nodeId) => _allowedPeers is null || _allowedPeers.Contains(nodeId);

    public bool UpsertPeer(string nodeId, string address, int tcpPort, DateTime utcNow)
    {
        if (!IsAllowed(nodeId)) return false;

        bool isNew = !_peers.ContainsKey(nodeId);
        _peers[nodeId] = new PeerInfo(nodeId, address, tcpPort, utcNow);
        return isNew;
    }

    public List<PeerInfo> GetActivePeers() =>
        _peers.Values.OrderBy(p => p.NodeId, StringComparer.Ordinal).ToList();

    public bool TryGetPeer(string nodeId, out PeerInfo? peer) =>
        _peers.TryGetValue(nodeId, out peer);

    public List<PeerInfo> PurgeExpired(TimeSpan ttl, DateTime utcNow)
    {
        var expired = new List<PeerInfo>();
        foreach (var kv in _peers)
        {
            if (utcNow - kv.Value.LastSeenUtc > ttl)
            {
                if (_peers.TryRemove(kv.Key, out var removed))
                    expired.Add(removed);
            }
        }
        return expired;
    }
}
