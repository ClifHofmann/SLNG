using System.Net;
using System.Net.Sockets;

namespace SLNG.Core;

/// <summary>
/// FEAT-SEC-01: whether an IP address is one the viewer must refuse to fetch from when the URL
/// came from in-world content rather than from the user.
///
/// <para>A MOAP face carries a creator-supplied URL, and the viewer fetches it. Pointed at
/// <c>http://127.0.0.1:8080/</c>, <c>http://192.168.1.1/</c> or <c>http://169.254.169.254/</c>
/// (the cloud metadata address) that is a request the object's owner caused the viewer to make
/// against the viewer's own machine and network — a router's admin page, a local dev server, an
/// instance credential endpoint. The response never leaves the machine, but the request itself
/// is the problem, and on a private network the reply can be rendered onto the prim for the
/// person standing in front of it to read.</para>
///
/// <para>Deliberately a pure function over <see cref="IPAddress"/>, not over a host name: the
/// check has to happen on the address actually being connected to, after DNS and after every
/// redirect. Checking a host name resolves nothing — <c>evil.example.com</c> can have an A record
/// of <c>127.0.0.1</c>, and a name checked and then resolved separately can return a different
/// address the second time.</para>
/// </summary>
public static class PrivateAddressPolicy
{
    /// <summary>True for loopback, private, link-local, carrier-grade-NAT, multicast, broadcast
    /// and reserved ranges — everything that is not a routable public host.
    ///
    /// <para>Denies by range rather than allowing by range, which is the safe direction: an
    /// address family or range nobody thought about ends up denied, not fetched.</para></summary>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4 address expressed as IPv6 (::ffff:127.0.0.1) must be judged as the IPv4
        // address it is, or every v4 rule below is trivially bypassed by writing it in v6 form.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPrivateOrReservedV4(address),
            AddressFamily.InterNetworkV6 => IsPrivateOrReservedV6(address),
            // Anything else (IPX, AppleTalk, Unix sockets) is not something an http(s) URL should
            // have produced. Refuse rather than wonder.
            _ => true,
        };
    }

    private static bool IsPrivateOrReservedV4(IPAddress address)
    {
        var b = address.GetAddressBytes();

        return b[0] switch
        {
            0 => true,                                  // 0.0.0.0/8   "this network"
            10 => true,                                 // 10/8        RFC1918
            127 => true,                                // 127/8       loopback
            100 => b[1] >= 64 && b[1] <= 127,           // 100.64/10   RFC6598 carrier-grade NAT
            169 => b[1] == 254,                         // 169.254/16  link-local, incl. cloud metadata
            172 => b[1] >= 16 && b[1] <= 31,            // 172.16/12   RFC1918
            192 => (b[1] == 168)                        // 192.168/16  RFC1918
                || (b[1] == 0 && b[2] == 0)             // 192.0.0/24  IETF protocol assignments
                || (b[1] == 0 && b[2] == 2)             // 192.0.2/24  TEST-NET-1
                || (b[1] == 88 && b[2] == 99),          // 192.88.99/24 deprecated 6to4 relay anycast
            198 => (b[1] == 18 || b[1] == 19)           // 198.18/15   benchmarking
                || (b[1] == 51 && b[2] == 100),         // 198.51.100/24 TEST-NET-2
            203 => b[1] == 0 && b[2] == 113,            // 203.0.113/24 TEST-NET-3
            >= 224 => true,                             // 224/4 multicast, 240/4 reserved, 255.255.255.255
            _ => false,
        };
    }

    private static bool IsPrivateOrReservedV6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
        // IsIPv6UniqueLocal exists only on newer targets in some profiles; fc00::/7 is one bit test.
        var b = address.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC) return true;         // fc00::/7 unique local

        // :: (unspecified) -- IPAddress.IsLoopback covers ::1 but not this.
        foreach (byte x in b)
        {
            if (x != 0) return false;
        }
        return true;
    }
}
