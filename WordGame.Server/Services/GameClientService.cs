using WordGame.Server.Networking;

namespace WordGame.Server.Services;

public class GameClientService : IDisposable
{
    private readonly PeerNodeManager _nodeManager;
    private readonly PeerGameEngine _engine;
    private readonly ILogger<GameClientService> _logger;

    public string NodeId => _nodeManager.NodeId;
    public string PlayerName { get; private set; } = "";
    public string CurrentLetters => _engine.CurrentLetters;
    public bool CanSubmit => _engine.CanSubmit;
    public List<string> StatusMessages => _engine.StatusMessages;
    public bool IsConnected => _nodeManager.IsConnected;
    public int PeerCount => _nodeManager.Peers.Count;
    public bool IsLeader => _nodeManager.IsLeader();
    public string DictionaryHash => _nodeManager.DictionaryHash;
    public bool RegistryHealthy => _nodeManager.RegistryHealthy;

    public event Action? OnStateChanged;

    public GameClientService(PeerNodeManager nodeManager, PeerGameEngine engine, ILogger<GameClientService> logger)
    {
        _nodeManager = nodeManager;
        _engine = engine;
        _logger = logger;
        _engine.StateChanged += NotifyStateChanged;
        _nodeManager.PeersChanged += NotifyStateChanged;
    }

    public async Task ConnectAsync(string name)
    {
        if (IsConnected)
        {
            return;
        }

        var normalizedName = string.IsNullOrWhiteSpace(name) ? "Anonymous" : name.Trim();

        try
        {
            await _nodeManager.ConnectAsync();
            _engine.Start();
            PlayerName = normalizedName;
            AddStatus($"Connected as node {_nodeManager.NodeId}");
            AddStatus($"Peers: {PeerCount}. Leader: {(IsLeader ? "yes" : "no")}");
            AddStatus($"Dictionary hash: {DictionaryHash[..12]}...");
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect");
            AddStatus($"Error: {ex.Message}");
            NotifyStateChanged();
        }
    }

    public async Task<(bool Accepted, string Message)> SubmitWordAsync(string word)
    {
        if (!IsConnected || !CanSubmit)
        {
            return (false, "Not connected or no active round.");
        }

        var result = await _engine.SubmitWordAsync(PlayerName, word);
        if (!result.Accepted)
            AddStatus(result.Message);
        return result;
    }

    public void Disconnect()
    {
        _nodeManager.Disconnect();
        _engine.Reset();
        PlayerName = "";
        NotifyStateChanged();
    }

    private void AddStatus(string msg)
    {
        StatusMessages.Add(msg);
        if (StatusMessages.Count > 50)
            StatusMessages.RemoveAt(0);
    }

    private void NotifyStateChanged()
    {
        try { OnStateChanged?.Invoke(); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose() => Disconnect();
}
