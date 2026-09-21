using System.Net;

namespace GoldenTicket.CompanionHost;

internal static class CompanionAddresses
{
    // The desktop address and reconnect QR use the same direct browser-game origin.
    internal static string Game(IPAddress address, int httpPort) =>
        new UriBuilder(Uri.UriSchemeHttp, address.ToString(), httpPort, "/companion/").Uri.AbsoluteUri;
}
