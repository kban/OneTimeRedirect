using System.Net;
using System.Net.Sockets;

namespace OneTimeRedirect.Web;

/// <summary>Partition key for per-client limits: the IPv4 address, or the /64 prefix of an IPv6 address.</summary>
public static class ClientKey
{
    public static string From(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        // One IPv6 subscriber usually owns a whole /64; per-address keys would let it rotate past every limit.
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
