using LiveBoard.Api.Configuration;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace LiveBoard.Api.Features;

public static class CallEndpoints
{
    /// <summary>Used when nothing is configured: Google's free public STUN servers. Enough for most home networks.</summary>
    private static readonly IceServerDto[] DefaultIceServers =
        [new(["stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"], null, null)];

    public static RouteGroupBuilder MapCallEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/calls/ice-servers", GetIceServers)
            .WithTags("Calls")
            .WithSummary("STUN/TURN servers for voice/video calls.")
            .WithDescription("Served by the API (not built into the frontend) so TURN credentials can change without a redeploy of the web app.");
        return api;
    }

    private static Ok<IReadOnlyList<IceServerDto>> GetIceServers(IOptions<CallSettings> settings)
    {
        var configured = settings.Value.IceServers
            .Where(s => s.Urls.Length > 0)
            .Select(s => new IceServerDto(s.Urls, s.Username, s.Credential))
            .ToList();
        return TypedResults.Ok<IReadOnlyList<IceServerDto>>(configured.Count > 0 ? configured : DefaultIceServers);
    }
}
