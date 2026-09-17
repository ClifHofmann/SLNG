using System.Net;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-SEC-01. The failure mode this guards is quiet: a MOAP prim whose URL resolves to the
/// viewer's own network produces a request nobody sees and, on a private network, a reply that
/// can be rendered onto the prim. A gap here is not a crash, it is a fetch that should not have
/// happened.
/// </summary>
public class PrivateAddressPolicyTests
{
    [Theory]
    // Loopback
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    // RFC1918
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.254")]
    // Link-local, including the cloud instance-metadata address
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    // Carrier-grade NAT
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    // This-network, multicast, reserved, broadcast
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6 unique-local and unspecified
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::")]
    // Documentation / benchmarking ranges -- not routable, so not a real media host either
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.88.99.1")]
    public void RefusesPrivateAndReserved(string address)
        => Assert.True(PrivateAddressPolicy.IsPrivateOrReserved(IPAddress.Parse(address)),
            $"{address} should be refused");

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]      // example.com
    [InlineData("208.80.154.224")]     // wikimedia
    [InlineData("172.15.255.255")]     // just below 172.16/12
    [InlineData("172.32.0.1")]         // just above 172.31/12
    [InlineData("100.63.255.255")]     // just below 100.64/10
    [InlineData("100.128.0.1")]        // just above 100.127/10
    [InlineData("169.253.255.255")]    // just below 169.254/16
    [InlineData("169.255.0.1")]        // just above 169.254/16
    [InlineData("192.167.255.255")]    // just below 192.168/16
    [InlineData("192.169.0.1")]        // just above 192.168/16
    [InlineData("223.255.255.255")]    // just below the 224/4 multicast block
    [InlineData("2606:4700:4700::1111")]
    public void AllowsPublicAddresses(string address)
        => Assert.False(PrivateAddressPolicy.IsPrivateOrReserved(IPAddress.Parse(address)),
            $"{address} should be allowed");

    /// <summary>The bypass that makes a v4-only check useless: every private v4 address can be
    /// written as an IPv6-mapped address, and .NET will happily connect to it.</summary>
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void RefusesIPv4MappedPrivateAddresses(string address)
        => Assert.True(PrivateAddressPolicy.IsPrivateOrReserved(IPAddress.Parse(address)),
            $"{address} should be refused -- it is a private v4 address in v6 clothing");

    /// <summary>A mapped PUBLIC address is still public; the unwrapping must not deny everything
    /// it touches.</summary>
    [Fact]
    public void AllowsIPv4MappedPublicAddress()
        => Assert.False(PrivateAddressPolicy.IsPrivateOrReserved(IPAddress.Parse("::ffff:8.8.8.8")));

    [Fact]
    public void NullThrowsRatherThanBeingTreatedAsAllowed()
        => Assert.Throws<ArgumentNullException>(() => PrivateAddressPolicy.IsPrivateOrReserved(null!));
}
