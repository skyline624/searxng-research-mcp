using System.Net;
using System.Net.Sockets;

namespace ServeurIa.Infrastructure.Web;

/// <summary>
/// Which addresses the brain may connect to on behalf of a caller: public unicast addresses only. Blocked: loopback, private
/// (RFC 1918), link-local (169.254/16, which holds the cloud metadata address), carrier-grade NAT (100.64/10), unique local
/// IPv6 (fc00::/7), link-local IPv6, multicast, reserved and documentation ranges, and any IPv6 form that carries an IPv4
/// address (IPv4-mapped, NAT64, 6to4, Teredo), which is judged by the IPv4 address inside.
/// </summary>
public sealed class IpPolicy
{
    private static readonly (byte[] Net, int Bits)[] BlockedV4 =
    [
        (Bytes(0, 0, 0, 0), 8),         // "this network", 0.0.0.0
        (Bytes(10, 0, 0, 0), 8),        // private
        (Bytes(100, 64, 0, 0), 10),     // carrier-grade NAT (also Alibaba's metadata address)
        (Bytes(127, 0, 0, 0), 8),       // loopback
        (Bytes(169, 254, 0, 0), 16),    // link-local, cloud metadata
        (Bytes(172, 16, 0, 0), 12),     // private
        (Bytes(192, 0, 0, 0), 24),      // IETF protocol assignments (Oracle's metadata address)
        (Bytes(192, 0, 2, 0), 24),      // documentation
        (Bytes(192, 88, 99, 0), 24),    // 6to4 relay
        (Bytes(192, 168, 0, 0), 16),    // private
        (Bytes(198, 18, 0, 0), 15),     // benchmarking
        (Bytes(198, 51, 100, 0), 24),   // documentation
        (Bytes(203, 0, 113, 0), 24),    // documentation
        (Bytes(224, 0, 0, 0), 4),       // multicast
        (Bytes(240, 0, 0, 0), 4),       // reserved, broadcast
    ];

    private static readonly (byte[] Net, int Bits)[] BlockedV6 =
    [
        (new byte[16], 128),                                           // ::
        (Bytes6(0, 0, 0, 0, 0, 0, 0, 1), 128),                         // ::1
        (Bytes6(0x0100, 0, 0, 0, 0, 0, 0, 0), 64),                     // discard
        (Bytes6(0x2001, 0, 0, 0, 0, 0, 0, 0), 32),                     // Teredo (embeds an IPv4 address in an obfuscated form)
        (Bytes6(0x2001, 0x0db8, 0, 0, 0, 0, 0, 0), 32),                // documentation
        (Bytes6(0xfc00, 0, 0, 0, 0, 0, 0, 0), 7),                      // unique local
        (Bytes6(0xfe80, 0, 0, 0, 0, 0, 0, 0), 10),                     // link-local
        (Bytes6(0xfec0, 0, 0, 0, 0, 0, 0, 0), 10),                     // site-local (deprecated)
        (Bytes6(0xff00, 0, 0, 0, 0, 0, 0, 0), 8),                      // multicast
    ];

    private readonly Func<IPAddress, bool>? _allowOverride;

    private IpPolicy(Func<IPAddress, bool>? allowOverride) => _allowOverride = allowOverride;

    /// <summary>The production policy.</summary>
    public static IpPolicy Public { get; } = new(null);

    /// <summary>
    /// For tests only: <paramref name="allow"/> is asked first, and an address it accepts passes. Tests serve fake pages from
    /// loopback with it. Production code must use <see cref="Public"/>.
    /// </summary>
    public static IpPolicy ForTests(Func<IPAddress, bool> allow) => new(allow);

    public bool IsAllowed(IPAddress address) => (_allowOverride?.Invoke(address) ?? false) || IsPublic(address);

    /// <summary>True when the address is public unicast, whatever the notation it came in.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPublic(address.MapToIPv4());
            }

            var bytes = address.GetAddressBytes();
            // IPv4-compatible (::a.b.c.d), deprecated: judge by the IPv4 part. ::/96 other than :: and ::1 is never a real host.
            if (bytes.Take(12).All(b => b == 0) && !(bytes[12] == 0 && bytes[13] == 0 && bytes[14] == 0 && bytes[15] <= 1))
            {
                return IsPublic(new IPAddress(bytes[12..]));
            }

            // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address that decides.
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes.Skip(4).Take(8).All(b => b == 0))
            {
                return IsPublic(new IPAddress(bytes[12..]));
            }

            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsPublic(new IPAddress(bytes[2..6]));
            }

            return !BlockedV6.Any(r => InRange(bytes, r.Net, r.Bits));
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var v4 = address.GetAddressBytes();
        return !BlockedV4.Any(r => InRange(v4, r.Net, r.Bits));
    }

    private static bool InRange(byte[] address, byte[] net, int bits)
    {
        var full = bits / 8;
        for (var i = 0; i < full; i++)
        {
            if (address[i] != net[i])
            {
                return false;
            }
        }

        var rest = bits % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (address[full] & mask) == (net[full] & mask);
    }

    private static byte[] Bytes(byte a, byte b, byte c, byte d) => [a, b, c, d];

    private static byte[] Bytes6(params ushort[] groups)
    {
        var bytes = new byte[16];
        for (var i = 0; i < 8; i++)
        {
            bytes[i * 2] = (byte)(groups[i] >> 8);
            bytes[(i * 2) + 1] = (byte)(groups[i] & 0xFF);
        }

        return bytes;
    }
}
