using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

static partial class Network
{
    // Bounded recovery of already-owned TVs by their saved MAC, without logging
    // in to or classifying unrelated devices with an open port.
    private static async Task<List<Tv>> RecoverKnownTvs(Tv[] known, CancellationToken ct)
    {
        var targets = known.Where(t => ValidMac(t.Mac)).GroupBy(t => MacKey(t.Mac)).ToDictionary(g => g.Key, g => g.First());
        if (targets.Count == 0) return [];
        var links = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .DistinctBy(a => a.Address).Take(4).ToArray();
        var addresses = new HashSet<string>();
        foreach (var link in links)
        {
            var bytes = link.Address.GetAddressBytes();
            for (int host = 1; host < 255; host++)
            {
                var candidate = new IPAddress(new byte[] { bytes[0], bytes[1], bytes[2], (byte)host });
                if (OnLink(link, candidate)) addresses.Add(candidate.ToString());
            }
        }
        var ports = targets.Values.Select(t => t.Port).Distinct().Take(4).ToArray();
        var found = new System.Collections.Concurrent.ConcurrentBag<Tv>();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 48, CancellationToken = ct }, async (host, token) =>
        {
            foreach (var port in ports)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(350);
                using var client = new TcpClient(AddressFamily.InterNetwork);
                try { await client.ConnectAsync(host, port, timeout.Token); }
                catch (SocketException) { continue; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                var mac = await Task.Run(() => LearnMac(host), token);
                if (mac is not null && targets.TryGetValue(MacKey(mac), out var saved) && port == saved.Port)
                    found.Add(saved with { Address = host });
            }
        });
        return found.DistinctBy(t => MacKey(t.Mac)).ToList();
    }
}
