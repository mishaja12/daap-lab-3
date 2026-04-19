namespace WordGame.Server.Networking;

public class NodeRuntimeOptions
{
    public bool EnableRegistry { get; set; } = true;
    public string RegistryUrl { get; set; } = "http://localhost:5118";
    public string NodeEndpoint { get; set; } = "http://localhost:5118";
    public int PeerRefreshSeconds { get; set; } = 3;
    public int HeartbeatSeconds { get; set; } = 2;
    public int ReconnectSeconds { get; set; } = 3;
    public int RoundIntervalSeconds { get; set; } = 5;
    public int RoundDurationSeconds { get; set; } = 14;
    public int MaxEventSkewSeconds { get; set; } = 30;
    public int ProcessedEventTtlSeconds { get; set; } = 120;
    public int MaxSubmissionsPerRound { get; set; } = 100;
}
