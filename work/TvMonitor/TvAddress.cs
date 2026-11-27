using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

sealed record TvNetworkScope(int Index, string Name)
{
    public override string ToString() => $"{Name} (%{Index})";
}

static class TvAddress
{
    internal static IPAddress Parse(string text, int scope = 0)
    {
        text = text.Trim();
        if (!IPAddress.TryParse(text, out var ip) ||
            (ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() != text) ||
            ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new IOException(T("Enter a valid IPv4 or IPv6 address."));
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal && ip.ScopeId == 0)
        {
            if (scope <= 0) throw new IOException(T("Select the network adapter connected to this IPv6 TV, or include its %scope number."));
            ip.ScopeId = scope;
        }
        return ip;
    }
    internal static bool Valid(string? value)
    { try { if (value is null) return false; Parse(value); return true; } catch (IOException) { return false; } }

    internal static string Endpoint(string address, int port)
    {
        var ip = IPAddress.Parse(address);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }

    internal static List<TvNetworkScope> Scopes()
    {
        var result = new List<TvNetworkScope>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        {
            try
            {
                var properties = nic.GetIPProperties();
                if (properties.UnicastAddresses.Any(a => a.Address.IsIPv6LinkLocal) && properties.GetIPv6Properties() is { Index: > 0 } v6)
                    result.Add(new(v6.Index, nic.Name));
            }
            catch (NetworkInformationException) { }
        }
        return result.OrderBy(s => s.Name).ToList();
    }

    internal static Uri? LocalUri(Uri uri, IPAddress target)
    {
        if (!IPAddress.TryParse(uri.DnsSafeHost, out var host) || !host.GetAddressBytes().SequenceEqual(target.GetAddressBytes())) return null;
        if (host.AddressFamily == AddressFamily.InterNetworkV6 && host.IsIPv6LinkLocal)
        {
            if (host.ScopeId != 0 && host.ScopeId != target.ScopeId) return null;
            host.ScopeId = target.ScopeId;
            return new UriBuilder(uri) { Host = host.ToString() }.Uri;
        }
        return uri;
    }
}
