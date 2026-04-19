using System.Collections.Concurrent;
using System.Text.Json;
using WordGame.Grpc;
using WordGame.Server.Security;

namespace WordGame.Server.Networking;

public class PeerGameEngine : IDisposable
{
    private static readonly char[] Vowels = { 'A', 'E', 'I', 'O', 'U' };
    private static readonly char[] Consonants = { 'B', 'C', 'D', 'F', 'G', 'H', 'J', 'K', 'L', 'M', 'N', 'P', 'Q', 'R', 'S', 'T', 'V', 'W', 'X', 'Y', 'Z' };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PeerNodeManager _nodeManager;
    private readonly MessageCrypto _crypto;
    private readonly WordValidator _wordValidator;
    private readonly NodeRuntimeOptions _options;
    private readonly ILogger<PeerGameEngine> _logger;
    private readonly ConcurrentDictionary<string, long> _processedEvents = new(StringComparer.Ordinal);
    private readonly List<WordSubmissionCandidate> _roundCandidates = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string? _currentRoundId;
    private DateTimeOffset _roundStartedAt;
    private DateTimeOffset _nextRoundAt = DateTimeOffset.UtcNow;

    public PeerGameEngine(
        PeerNodeManager nodeManager,
        MessageCrypto crypto,
        WordValidator wordValidator,
        NodeRuntimeOptions options,
        ILogger<PeerGameEngine> logger)
    {
        _nodeManager = nodeManager;
        _crypto = crypto;
        _wordValidator = wordValidator;
        _options = options;
        _logger = logger;
    }

    public string CurrentLetters { get; private set; } = "";
    public bool RoundActive { get; private set; }
    public bool CanSubmit => _nodeManager.IsConnected && RoundActive;
    public List<string> StatusMessages { get; } = new();
    public event Action? StateChanged;

    public void Start()
    {
        if (_loopTask != null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), _loopCts.Token);
    }

    public async Task<(bool Accepted, string Message)> SubmitWordAsync(string playerName, string word, CancellationToken ct = default)
    {
        if (!RoundActive || string.IsNullOrWhiteSpace(_currentRoundId))
        {
            return (false, "No active round.");
        }

        var trimmed = (word ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            return (false, "Word must be at least 2 letters.");
        }

        var payload = new WordSubmittedPayload(_currentRoundId, playerName, trimmed);
        var evt = BuildSignedEvent(PeerEventTypes.WordSubmitted, payload);

        var applied = await ApplySignedEventAsync(evt);
        if (!applied.Accepted)
        {
            return applied;
        }

        await _nodeManager.BroadcastEventAsync(evt, ct);
        return (true, "Word sent to peers.");
    }

    public async Task<(bool Accepted, string Message)> ApplySignedEventAsync(SignedGameEventRequest evt)
    {
        CleanupProcessedEvents();

        if (!IsTimestampValid(evt.TimestampUnixMilliseconds))
        {
            return (false, "Event timestamp is outside allowed skew window");
        }

        if (!_processedEvents.TryAdd(evt.EventId, evt.TimestampUnixMilliseconds))
        {
            return (false, "Duplicate event");
        }

        if (!ValidateEventSignature(evt, out var err))
        {
            return (false, err ?? "Signature validation failed");
        }

        switch (evt.EventType)
        {
            case PeerEventTypes.RoundStarted:
                return ApplyRoundStarted(evt);
            case PeerEventTypes.WordSubmitted:
                return ApplyWordSubmitted(evt);
            case PeerEventTypes.RoundResult:
                return ApplyRoundResult(evt);
            default:
                return (false, "Unknown event type");
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_nodeManager.IsConnected && _nodeManager.IsLeader())
                {
                    if (!RoundActive && DateTimeOffset.UtcNow >= _nextRoundAt)
                    {
                        var payload = new RoundStartedPayload(
                            Guid.NewGuid().ToString("N")[..8],
                            GenerateLetters(),
                            _nodeManager.NodeId);

                        var startEvent = BuildSignedEvent(PeerEventTypes.RoundStarted, payload);
                        await ApplySignedEventAsync(startEvent);
                        await _nodeManager.BroadcastEventAsync(startEvent, ct);
                    }

                    if (RoundActive && DateTimeOffset.UtcNow >= _roundStartedAt.AddSeconds(Math.Max(6, _options.RoundDurationSeconds)))
                    {
                        var result = ResolveRoundResult();
                        var resultEvent = BuildSignedEvent(PeerEventTypes.RoundResult, result);
                        await ApplySignedEventAsync(resultEvent);
                        await _nodeManager.BroadcastEventAsync(resultEvent, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Peer game loop warning");
            }

            await Task.Delay(500, ct);
        }
    }

    private (bool Accepted, string Message) ApplyRoundStarted(SignedGameEventRequest evt)
    {
        var payload = JsonSerializer.Deserialize<RoundStartedPayload>(evt.PayloadJson, JsonOptions);
        if (payload == null || string.IsNullOrWhiteSpace(payload.RoundId) || string.IsNullOrWhiteSpace(payload.Letters))
        {
            return (false, "Invalid round_start payload");
        }

        var currentLeader = _nodeManager.GetLeaderNodeId();
        if (!string.Equals(evt.SenderNodeId, currentLeader, StringComparison.Ordinal))
        {
            return (false, "Only cluster leader can start rounds");
        }

        lock (_lock)
        {
            _currentRoundId = payload.RoundId;
            CurrentLetters = payload.Letters.ToUpperInvariant();
            _roundStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(evt.TimestampUnixMilliseconds);
            RoundActive = true;
            _roundCandidates.Clear();
        }

        AddStatus($"Round started ({payload.RoundId}). Letters: {CurrentLetters}");
        return (true, "Round started");
    }

    private (bool Accepted, string Message) ApplyWordSubmitted(SignedGameEventRequest evt)
    {
        var payload = JsonSerializer.Deserialize<WordSubmittedPayload>(evt.PayloadJson, JsonOptions);
        if (payload == null)
        {
            return (false, "Invalid word_submitted payload");
        }

        lock (_lock)
        {
            if (!RoundActive || string.IsNullOrWhiteSpace(_currentRoundId) || !string.Equals(_currentRoundId, payload.RoundId, StringComparison.Ordinal))
            {
                return (false, "Round mismatch");
            }

            var validation = _wordValidator.Validate(payload.Word, CurrentLetters);
            if (!validation.Valid)
            {
                return (false, validation.Error ?? "Invalid word");
            }

            _roundCandidates.Add(new WordSubmissionCandidate(
                payload.PlayerName,
                payload.Word.Trim(),
                evt.SenderNodeId,
                evt.TimestampUnixMilliseconds));

            var maxSubmissions = Math.Max(10, _options.MaxSubmissionsPerRound);
            if (_roundCandidates.Count > maxSubmissions)
            {
                _roundCandidates.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
                _roundCandidates.RemoveRange(0, _roundCandidates.Count - maxSubmissions);
            }
        }

        AddStatus($"Submission: {payload.PlayerName} -> {payload.Word}");
        return (true, "Submission accepted");
    }

    private (bool Accepted, string Message) ApplyRoundResult(SignedGameEventRequest evt)
    {
        var payload = JsonSerializer.Deserialize<RoundResultPayload>(evt.PayloadJson, JsonOptions);
        if (payload == null)
        {
            return (false, "Invalid round_result payload");
        }

        var currentLeader = _nodeManager.GetLeaderNodeId();
        if (!string.Equals(evt.SenderNodeId, currentLeader, StringComparison.Ordinal))
        {
            return (false, "Only cluster leader can finalize rounds");
        }

        lock (_lock)
        {
            if (!RoundActive || !string.Equals(_currentRoundId, payload.RoundId, StringComparison.Ordinal))
            {
                return (false, "Round mismatch");
            }

            RoundActive = false;
            CurrentLetters = "";
            _roundCandidates.Clear();
            _nextRoundAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(2, _options.RoundIntervalSeconds));
        }

        if (string.IsNullOrWhiteSpace(payload.WinnerName))
        {
            AddStatus("Round ended: no valid words.");
        }
        else
        {
            AddStatus($"Winner: {payload.WinnerName} with '{payload.WinningWord}'.");
        }

        return (true, "Round finished");
    }

    private RoundResultPayload ResolveRoundResult()
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(_currentRoundId))
            {
                return new RoundResultPayload("", "", "");
            }

            var winner = _roundCandidates
                .OrderByDescending(x => x.Word.Length)
                .ThenBy(x => x.TimestampMs)
                .ThenBy(x => x.SenderNodeId, StringComparer.Ordinal)
                .FirstOrDefault();

            return winner == null
                ? new RoundResultPayload(_currentRoundId, "", "")
                : new RoundResultPayload(_currentRoundId, winner.PlayerName, winner.Word);
        }
    }

    private SignedGameEventRequest BuildSignedEvent<T>(string eventType, T payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var payloadHash = _crypto.ComputePayloadHash(payloadJson);
        return new SignedGameEventRequest
        {
            EventId = Guid.NewGuid().ToString("N"),
            SenderNodeId = _nodeManager.NodeId,
            EventType = eventType,
            PayloadJson = payloadJson,
            PayloadHash = payloadHash,
            Signature = _crypto.SignHash(payloadHash),
            TimestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private bool ValidateEventSignature(SignedGameEventRequest evt, out string? error)
    {
        if (string.IsNullOrWhiteSpace(evt.EventId) ||
            string.IsNullOrWhiteSpace(evt.SenderNodeId) ||
            string.IsNullOrWhiteSpace(evt.PayloadJson) ||
            string.IsNullOrWhiteSpace(evt.PayloadHash) ||
            string.IsNullOrWhiteSpace(evt.Signature))
        {
            error = "Event envelope is incomplete";
            return false;
        }

        if (!_nodeManager.TryGetPeerPublicKey(evt.SenderNodeId, out var publicKey))
        {
            error = "Unknown sender node";
            return false;
        }

        var computed = _crypto.ComputePayloadHash(evt.PayloadJson);
        if (!string.Equals(computed, evt.PayloadHash, StringComparison.Ordinal))
        {
            error = "Payload hash mismatch";
            return false;
        }

        if (!_crypto.VerifyHash(evt.PayloadHash, evt.Signature, publicKey))
        {
            error = "Invalid signature";
            return false;
        }

        error = null;
        return true;
    }

    private bool IsTimestampValid(long timestampUnixMilliseconds)
    {
        var skew = Math.Max(5, _options.MaxEventSkewSeconds);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Math.Abs(now - timestampUnixMilliseconds) <= skew * 1000L;
    }

    private void CleanupProcessedEvents()
    {
        var ttlSeconds = Math.Max(30, _options.ProcessedEventTtlSeconds);
        var minTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ttlSeconds * 1000L;

        foreach (var pair in _processedEvents)
        {
            if (pair.Value < minTimestamp)
            {
                _processedEvents.TryRemove(pair.Key, out _);
            }
        }
    }

    private string GenerateLetters()
    {
        var rnd = Random.Shared;
        var letters = new List<char>
        {
            Vowels[rnd.Next(Vowels.Length)],
            Vowels[rnd.Next(Vowels.Length)]
        };

        for (var i = 0; i < 4; i++)
        {
            letters.Add(Consonants[rnd.Next(Consonants.Length)]);
        }

        return new string(letters.OrderBy(_ => rnd.Next()).ToArray());
    }

    private void AddStatus(string text)
    {
        StatusMessages.Add(text);
        if (StatusMessages.Count > 80)
        {
            StatusMessages.RemoveAt(0);
        }

        try
        {
            StateChanged?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // UI disconnected.
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            CurrentLetters = "";
            RoundActive = false;
            _roundCandidates.Clear();
            _currentRoundId = null;
            _nextRoundAt = DateTimeOffset.UtcNow;
        }

        StatusMessages.Clear();
        StateChanged?.Invoke();
    }

    public void Dispose()
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
    }
}
