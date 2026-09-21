using System.Net;
using GoldenTicket.CompanionHost.Networking;

namespace GoldenTicket.Domain.Tests;

/// <summary>Selected Private LAN address and subnet policy for the browser game host.</summary>
public sealed class CompanionNetworkingTests
{
    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("127.0.0.1", false)]
    public void OnlyPrivateAddressesMayBeServedOn(string address, bool expected) =>
        Assert.Equal(expected, LanInterfaces.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("192.168.1.99", 24, true)]
    [InlineData("::ffff:192.168.1.99", 24, true)]
    [InlineData("192.168.2.99", 24, false)]
    [InlineData("10.0.0.1", 24, false)]
    [InlineData("8.8.8.8", 8, false)]
    [InlineData("192.168.1.49", 30, true)]
    [InlineData("192.168.1.54", 30, false)]
    [InlineData("192.168.1.50", 0, false)]
    public void PeersMustBelongToTheSelectedSubnet(string peer, int prefixLength, bool allowed) =>
        Assert.Equal(allowed, LanInterfaces.IsInSubnet(IPAddress.Parse(peer), IPAddress.Parse("192.168.1.50"), prefixLength));
}
