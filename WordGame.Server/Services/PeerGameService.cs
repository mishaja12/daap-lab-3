using Grpc.Core;
using WordGame.Grpc;
using WordGame.Server.Networking;

namespace WordGame.Server.Services;

public class PeerGameService : PeerGame.PeerGameBase
{
    private readonly PeerGameEngine _engine;
    private readonly ILogger<PeerGameService> _logger;

    public PeerGameService(PeerGameEngine engine, ILogger<PeerGameService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public override async Task<PublishEventResponse> PublishEvent(SignedGameEventRequest request, ServerCallContext context)
    {
        var result = await _engine.ApplySignedEventAsync(request);
        if (!result.Accepted)
        {
            _logger.LogDebug("Rejected event {EventId}: {Reason}", request.EventId, result.Message);
        }

        return new PublishEventResponse
        {
            Accepted = result.Accepted,
            Message = result.Message
        };
    }
}
