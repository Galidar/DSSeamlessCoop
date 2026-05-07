/*
 * Network info: detect public (WAN) and private (LAN) IPs.
 *
 * Public IP comes from api.ipify.org (lightweight, no rate limit at our
 * usage). Private IP picks the IPv4 of the interface holding the default
 * gateway, which is the same one the OS uses to talk to the internet.
 */

using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bonfire.Service.Modules;

public static class Network
{
    public static async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Bonfire/0.1");
            var ip = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
            return string.IsNullOrEmpty(ip) ? null : ip;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetPrivateIp()
    {
        // Prefer the interface that has a default gateway and is up.
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                       && !g.Address.Equals(IPAddress.Any)));

            foreach (var p in candidates)
            {
                foreach (var ua in p.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address) &&
                        !ua.Address.ToString().StartsWith("169.254."))
                    {
                        return ua.Address.ToString();
                    }
                }
            }
        }
        catch { /* fall through to broader search */ }

        // Fallback: any non-loopback non-APIPA IPv4.
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var addr in host.AddressList)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr) &&
                    !addr.ToString().StartsWith("169.254."))
                {
                    return addr.ToString();
                }
            }
        }
        catch { }

        return null;
    }
}
