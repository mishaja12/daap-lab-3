namespace WordGame.Server.Networking;

public static class PeerEventTypes
{
    public const string RoundStarted = "round_started";
    public const string WordSubmitted = "word_submitted";
    public const string RoundResult = "round_result";
}

public record RoundStartedPayload(string RoundId, string Letters, string LeaderNodeId);

public record WordSubmittedPayload(string RoundId, string PlayerName, string Word);

public record RoundResultPayload(string RoundId, string WinnerName, string WinningWord);

public record WordSubmissionCandidate(string PlayerName, string Word, string SenderNodeId, long TimestampMs);
