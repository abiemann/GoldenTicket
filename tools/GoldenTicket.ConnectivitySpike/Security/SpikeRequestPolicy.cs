using System.Net;
using GoldenTicket.ConnectivitySpike.Networking;

namespace GoldenTicket.ConnectivitySpike.Security;

/// <summary>Shared protection for the HTTPS probe and its public-certificate-only bootstrap.</summary>
internal sealed class SpikeRequestPolicy(
    IPAddress address, int prefixLength, IReadOnlyList<string> origins, Func<bool> isPrivateNetwork)
{
    internal const long MaximumRequestBytes = 16 * 1024;

    internal int Validate(HttpContext context, bool bootstrap = false)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || (!IPAddress.IsLoopback(remote) &&
            (!isPrivateNetwork() || !LanInterfaces.IsInSubnet(remote, address, prefixLength))))
            return StatusCodes.Status403Forbidden;

        var host = context.Request.Host.Value;
        if (!origins.Any(origin => string.Equals(new Uri(origin).Authority, host, StringComparison.OrdinalIgnoreCase)))
            return StatusCodes.Status421MisdirectedRequest;

        if (bootstrap) return StatusCodes.Status200OK;
        if (!context.Request.Path.StartsWithSegments("/api")) return StatusCodes.Status200OK;

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return StatusCodes.Status403Forbidden;
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-GoldenTicket-Spike"] != "1")
            return StatusCodes.Status403Forbidden;
        return StatusCodes.Status200OK;
    }
}
