using System.Collections.Concurrent;
using Grpc.Net.Client;
using WordGame.Grpc;
using WordGame.Server.Security;

namespace WordGame.Server.Networking;

public class PeerNodeManager : IDisposable
{
    private readonly NodeRuntimeOptions _options;
    private readonly MessageCrypto _crypto;
    private readonly WordValidator _wordValidator;
    private readonly ILogger<PeerNodeManager> _logger;
    private readonly ConcurrentDictionary<string, NodeInfo> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrpcChannel> _peerChannels = new(StringComparer.Ordinal);
    private readonly object _channelLock = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private GrpcChannel? _registryChannel;
    private NodeRegistry.NodeRegistryClient? _registryClient;
    private DateTimeOffset _nextReconnectAt = DateTimeOffset.UtcNow;

    public PeerNodeManager(
        NodeRuntimeOptions options,
        MessageCrypto crypto,
        WordValidator wordValidator,
        ILogger<PeerNodeManager> logger)
    {
        _options = options;
        _crypto = crypto;
        _wordValidator = wordValidator;
        _logger = logger;
        NodeId = Guid.NewGuid().ToString("N")[..8];
        PublicKey = _crypto.ExportPublicKey();
        DictionaryHash = _wordValidator.DictionaryHash;
    }

    public string NodeId { get; }
    public string PublicKey { get; }
    public string DictionaryHash { get; }
    public bool IsConnected { get; private set; }
    public bool RegistryHealthy { get; private set; }
    public string RegistryUrl => _options.RegistryUrl;
    public string NodeEndpoint => _options.NodeEndpoint;
    public event Action? PeersChanged;

    public IReadOnlyCollection<NodeInfo> Peers => _peers.Values.ToList();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return;
        }

        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        _registryChannel = GrpcChannel.ForAddress(_options.RegistryUrl);
        _registryClient = new NodeRegistry.NodeRegistryClient(_registryChannel);

        await RegisterNodeAsync(ct);
        IsConnected = true;
        RegistryHealthy = true;

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), _loopCts.Token);
    }

    public async Task BroadcastEventAsync(SignedGameEventRequest evt, CancellationToken ct = default)
    {
        var peers = Peers.ToList();
        foreach (var peer in peers)
        {
            if (string.Equals(peer.NodeId, NodeId, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var client = GetPeerClient(peer);
                await client.PublishEventAsync(evt, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send event {EventId} to {PeerId}", evt.EventId, peer.NodeId);
            }
        }
    }

    public bool TryGetPeerPublicKey(string nodeId, out string publicKey)
    {
        if (string.Equals(nodeId, NodeId, StringComparison.Ordinal))
        {
            publicKey = PublicKey;
            return true;
        }

        if (_peers.TryGetValue(nodeId, out var node))
        {
            publicKey = node.PublicKey;
            return true;
        }

        publicKey = string.Empty;
        return false;
    }

    public bool IsLeader()
    {
        return string.Equals(GetLeaderNodeId(), NodeId, StringComparison.Ordinal);
    }

    public string GetLeaderNodeId()
    {
        var ids = Peers.Select(p => p.NodeId)
            .Append(NodeId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);

        return ids.FirstOrDefault() ?? NodeId;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var lastHeartbeat = DateTimeOffset.MinValue;
        var lastPeers = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_registryClient != null)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastHeartbeat >= TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatSeconds)))
                    {
                        var hb = await _registryClient.HeartbeatAsync(new HeartbeatRequest { NodeId = NodeId }, cancellationToken: ct);
                        if (!hb.Accepted)
                        {
                            await RegisterNodeAsync(ct);
                        }

                        lastHeartbeat = now;
                    }

                    if (now - lastPeers >= TimeSpan.FromSeconds(Math.Max(1, _options.PeerRefreshSeconds)))
                    {
                        var peersResponse = await _registryClient.GetPeersAsync(new GetPeersRequest
                        {
                            RequesterNodeId = NodeId
                        }, cancellationToken: ct);

                        UpdatePeers(peersResponse.Peers);
                        lastPeers = now;
                    }

                    RegistryHealthy = true;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                RegistryHealthy = false;
                _logger.LogWarning(ex, "Registry loop failed");
                await TryReconnectAsync(ct);
            }

            await Task.Delay(500, ct);
        }
    }

    private async Task RegisterNodeAsync(CancellationToken ct)
    {
        if (_registryClient == null)
        {
            throw new InvalidOperationException("Registry client is not initialized.");
        }

        var response = await _registryClient.RegisterNodeAsync(new RegisterNodeRequest
        {
            NodeId = NodeId,
            Endpoint = _options.NodeEndpoint,
            PublicKey = PublicKey,
            DictionaryHash = DictionaryHash
        }, cancellationToken: ct);

        if (!response.Accepted)
        {
            throw new InvalidOperationException($"Registry rejected node: {response.Message}");
        }

        UpdatePeers(response.Peers);
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextReconnectAt)
        {
            return;
        }

        _nextReconnectAt = now.AddSeconds(Math.Max(1, _options.ReconnectSeconds));

        try
        {
            _registryChannel?.Dispose();
            _registryChannel = GrpcChannel.ForAddress(_options.RegistryUrl);
            _registryClient = new NodeRegistry.NodeRegistryClient(_registryChannel);
            await RegisterNodeAsync(ct);
            RegistryHealthy = true;
            _logger.LogInformation("Reconnected to registry.");
        }
        catch (Exception ex)
        {
            RegistryHealthy = false;
            _logger.LogWarning(ex, "Registry reconnect attempt failed.");
        }
    }

    private PeerGame.PeerGameClient GetPeerClient(NodeInfo peer)
    {
        lock (_channelLock)
        {
            if (!_peerChannels.TryGetValue(peer.Endpoint, out var channel))
            {
                channel = GrpcChannel.ForAddress(peer.Endpoint);
                _peerChannels[peer.Endpoint] = channel;
            }

            return new PeerGame.PeerGameClient(channel);
        }
    }

    private void UpdatePeers(IEnumerable<NodeInfo> peers)
    {
        var snapshot = peers.ToDictionary(p => p.NodeId, p => p, StringComparer.Ordinal);
        _peers.Clear();
        foreach (var peer in snapshot.Values)
        {
            if (peer.DictionaryHash == DictionaryHash)
            {
                _peers[peer.NodeId] = peer;
            }
        }

        PeersChanged?.Invoke();
    }

    public void Disconnect()
    {
        _loopCts?.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown race.
        }

        IsConnected = false;
        RegistryHealthy = false;
        _registryChannel?.Dispose();
        _registryChannel = null;
        _registryClient = null;
        _peers.Clear();

        lock (_channelLock)
        {
            foreach (var channel in _peerChannels.Values)
            {
                channel.Dispose();
            }

            _peerChannels.Clear();
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
