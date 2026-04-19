using Grpc.Core;
using WordGame.Grpc;
using WordGame.Server.Networking;

namespace WordGame.Server.Services;

public class NodeRegistryService : NodeRegistry.NodeRegistryBase
{
    private readonly NodeRegistryStore _store;
    private readonly ILogger<NodeRegistryService> _logger;

    public NodeRegistryService(NodeRegistryStore store, ILogger<NodeRegistryService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public override Task<RegisterNodeResponse> RegisterNode(RegisterNodeRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.NodeId) ||
            string.IsNullOrWhiteSpace(request.Endpoint) ||
            string.IsNullOrWhiteSpace(request.PublicKey))
        {
            return Task.FromResult(new RegisterNodeResponse
            {
                Accepted = false,
                Message = "node_id, endpoint and public_key are required"
            });
        }

        _store.Upsert(new NodeInfo
        {
            NodeId = request.NodeId,
            Endpoint = request.Endpoint,
            PublicKey = request.PublicKey,
            DictionaryHash = request.DictionaryHash ?? string.Empty,
            LastSeenUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        _logger.LogInformation("Node {NodeId} registered: {Endpoint}", request.NodeId, request.Endpoint);

        var response = new RegisterNodeResponse
        {
            Accepted = true,
            Message = "registered"
        };
        response.Peers.AddRange(_store.GetActivePeers(request.NodeId));
        return Task.FromResult(response);
    }

    public override Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        var accepted = !string.IsNullOrWhiteSpace(request.NodeId) && _store.Touch(request.NodeId);
        return Task.FromResult(new HeartbeatResponse
        {
            Accepted = accepted,
            Message = accepted ? "ok" : "node not found"
        });
    }

    public override Task<GetPeersResponse> GetPeers(GetPeersRequest request, ServerCallContext context)
    {
        var response = new GetPeersResponse();
        response.Peers.AddRange(_store.GetActivePeers(request.RequesterNodeId));
        return Task.FromResult(response);
    }
}
