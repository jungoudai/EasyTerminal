using System.Net;
using System.Net.Sockets;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;

namespace EasyTerminal.Core.Integrations;

/// <summary>Looks up where a host's public address is, for hosts that can't ask for themselves.</summary>
public static class GeoLookup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task TryFillAsync(DeviceFacts facts, string host)
    {
        try
        {
            string name = HostEndpoint.Parse(host).Host;
            var addresses = IPAddress.TryParse(name, out var parsed) ? new[] { parsed } : await Dns.GetHostAddressesAsync(name);
            // A private address means nothing to a public lookup service, and it shouldn't leave the network.
            var address = addresses.FirstOrDefault(IsPublic);
            if (address == null) return;
            string json = await Http.GetStringAsync($"https://ipinfo.io/{address}/json");
            RemoteProbe.ApplyGeo(facts, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException or ArgumentException)
        {
            // The location is a nicety; a failed lookup only leaves it unknown.
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal) return false;
        if (address.AddressFamily != AddressFamily.InterNetwork) return address.AddressFamily == AddressFamily.InterNetworkV6;
        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 => b[1] is < 64 or > 127,
            169 => b[1] != 254,
            172 => b[1] is < 16 or > 31,
            192 => b[1] != 168,
            >= 224 => false,
            _ => true,
        };
    }
}
