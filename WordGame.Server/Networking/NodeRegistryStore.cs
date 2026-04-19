using System.Collections.Concurrent;
using WordGame.Grpc;

namespace WordGame.Server.Networking;

public class NodeRegistryStore
{
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(12);

    public void Upsert(NodeInfo node)
    {
        _nodes[node.NodeId] = node;
    }

    public bool Touch(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        _nodes[nodeId] = CloneWithLastSeen(node, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return true;
    }

    public List<NodeInfo> GetActivePeers(string? excludeNodeId = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minLastSeen = now - (long)_ttl.TotalSeconds;

        var active = new List<NodeInfo>();
        foreach (var pair in _nodes)
        {
            if (pair.Value.LastSeenUnixSeconds < minLastSeen)
            {
                _nodes.TryRemove(pair.Key, out _);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(excludeNodeId) &&
                string.Equals(pair.Value.NodeId, excludeNodeId, StringComparison.Ordinal))
            {
                continue;
            }

            active.Add(pair.Value);
        }

        return active;
    }

    private static NodeInfo CloneWithLastSeen(NodeInfo source, long lastSeen)
    {
        return new NodeInfo
        {
            NodeId = source.NodeId,
            Endpoint = source.Endpoint,
            PublicKey = source.PublicKey,
            DictionaryHash = source.DictionaryHash,
            LastSeenUnixSeconds = lastSeen
        };
    }
}
