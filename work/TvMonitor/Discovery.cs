using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

static partial class Network
{
    private const string AndroidRemote = "_androidtvremote2._tcp.local";
    private const string AndroidRemoteV1 = "_androidtvremote._tcp.local";
    private const string GoogleCast = "_googlecast._tcp.local";
    private const string AdbTls = "_adb-tls-connect._tcp.local";
    private const string AdbLegacy = "_adb._tcp.local";

    private static UdpClient DiscoverySocket(UnicastIPAddressInformation link, int ttl)
    {
        var socket = new UdpClient(new IPEndPoint(link.Address, 0));
        try
        {
            if (link.Address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, (int)link.Address.ScopeId);
                socket.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, ttl);
            }
            else
            {
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, link.Address.GetAddressBytes());
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool OnLink(UnicastIPAddressInformation link, IPAddress remote)
    {
        if (link.Address.AddressFamily == AddressFamily.InterNetworkV6)
            return remote.AddressFamily == AddressFamily.InterNetworkV6 && remote.IsIPv6LinkLocal &&
                (remote.ScopeId == 0 || remote.ScopeId == link.Address.ScopeId) && !remote.GetAddressBytes().SequenceEqual(link.Address.GetAddressBytes());
        var address = remote.GetAddressBytes();
        var local = link.Address.GetAddressBytes();
        var mask = link.IPv4Mask.GetAddressBytes();
        return address.Length == local.Length && !remote.Equals(link.Address) &&
            local.Select((b, i) => ((b ^ address[i]) & mask[i]) == 0).All(match => match);
    }

    private static async Task<List<Tv>> DiscoverMdns(UnicastIPAddressInformation link, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(3300);
        var endpoint = new IPEndPoint(link.Address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(IPAddress.Parse("ff02::fb").GetAddressBytes(), link.Address.ScopeId) : IPAddress.Parse("224.0.0.251"), 5353);
        var observations = new Dictionary<IPAddress, List<DnsRecord>>();
        try
        {
            using var udp = DiscoverySocket(link, 255);
            foreach (var name in new[] { AndroidRemote, AndroidRemoteV1, GoogleCast, AdbTls, AdbLegacy })
                await udp.SendAsync(Question(name), endpoint, timeout.Token);
            for (var packets = 0; packets < 200; packets++)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (reply.RemoteEndPoint.Port != 5353 || !OnLink(link, reply.RemoteEndPoint.Address)) continue;
                if (reply.RemoteEndPoint.Address.AddressFamily == AddressFamily.InterNetworkV6 && reply.RemoteEndPoint.Address.ScopeId == 0)
                    reply.RemoteEndPoint.Address.ScopeId = link.Address.ScopeId;
                var records = ParseDns(reply.Buffer);
                if (records.Count == 0) continue;
                if (!observations.TryGetValue(reply.RemoteEndPoint.Address, out var known))
                    observations.Add(reply.RemoteEndPoint.Address, known = []);
                known.AddRange(records);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }

        return observations.Select(entry => IdentifyTv(entry.Key, entry.Value)).OfType<Tv>().ToList();
    }

    internal static byte[] Question(string name)
    {
        // An ephemeral source port requests a legacy unicast response, so no local 5353 listener is needed.
        using var query = new MemoryStream();
        query.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            query.WriteByte((byte)bytes.Length);
            query.Write(bytes);
        }
        query.Write(new byte[] { 0, 0, 12, 0, 1 });
        return query.ToArray();
    }

    internal sealed record DnsRecord(string Owner, int Type, string Value, int Port = 0);

    internal static List<DnsRecord> ParseDns(byte[] packet)
    {
        if (packet.Length < 12 || (packet[2] & 0x80) == 0) return [];
        int Word(int offset) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
        var records = new List<DnsRecord>();
        int offset = 12;
        try
        {
            for (var i = 0; i < Word(4); i++)
            {
                ReadName(packet, ref offset);
                offset += 4;
            }
            var count = Word(6) + Word(8) + Word(10);
            for (var i = 0; i < count; i++)
            {
                var owner = ReadName(packet, ref offset);
                var type = Word(offset);
                var size = Word(offset + 8);
                var start = offset + 10;
                offset = start + size;
                if (offset > packet.Length) return [];
                if (type == 12)
                {
                    var position = start;
                    var instance = ReadName(packet, ref position);
                    if (position <= offset) records.Add(new(owner, type, instance));
                }
                else if (type == 33 && size >= 7)
                    records.Add(new(owner, type, "", Word(start + 4)));
                else if (type == 16)
                {
                    for (var position = start; position < offset;)
                    {
                        var length = packet[position++];
                        if (position + length > offset) return [];
                        var entry = Encoding.UTF8.GetString(packet, position, length);
                        if (entry.StartsWith("fn=", StringComparison.OrdinalIgnoreCase) ||
                            entry.StartsWith("md=", StringComparison.OrdinalIgnoreCase))
                            records.Add(new(owner, type, entry));
                        position += length;
                    }
                }
            }
        }
        catch (ArgumentOutOfRangeException) { return []; }
        catch (InvalidDataException) { return []; }
        return records;
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var visited = new HashSet<int>();
        int position = offset, end = -1, bytes = 0;
        while (position < packet.Length && visited.Add(position) && visited.Count <= 128)
        {
            var length = packet[position++];
            if (length == 0)
            {
                offset = end < 0 ? position : end;
                return string.Join('.', labels);
            }
            if ((length & 0xc0) == 0xc0 && position < packet.Length)
            {
                if (end < 0) end = position + 1;
                position = ((length & 0x3f) << 8) | packet[position];
            }
            else if (length <= 63 && position + length <= packet.Length && (bytes += length + 1) < 255)
            {
                labels.Add(Encoding.UTF8.GetString(packet, position, length));
                position += length;
            }
            else break;
        }
        throw new InvalidDataException("Malformed DNS name.");
    }

    internal static Tv? IdentifyTv(IPAddress address, List<DnsRecord> records)
    {
        bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        var remote = records.FirstOrDefault(r => r.Type == 12 && (Is(r.Owner, AndroidRemote) || Is(r.Owner, AndroidRemoteV1)));
        var cast = records.FirstOrDefault(r => r.Type == 12 && Is(r.Owner, GoogleCast));
        string? Tag(string prefix) => cast is null ? null : records.FirstOrDefault(r => r.Type == 16 &&
            Is(r.Owner, cast.Value) && r.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Value[prefix.Length..];
        var model = Tag("md=") ?? "";
        var android = remote is not null || Regex.IsMatch(model, @"\b(?:Android TV|Google TV)\b", RegexOptions.IgnoreCase);
        if (!android && !Regex.IsMatch(model, @"\bTV\b|Skyworth|BRAVIA|VIZIO", RegexOptions.IgnoreCase)) return null;

        var name = Tag("fn=") ?? (remote is null ? model :
            remote.Value.EndsWith("." + remote.Owner, StringComparison.OrdinalIgnoreCase)
                ? remote.Value[..^(remote.Owner.Length + 1)] : remote.Value);
        name = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        if (name.Length == 0) name = android ? T("Android TV") : T("TV");
        var adbInstances = records.Where(r => r.Type == 12 && (Is(r.Owner, AdbTls) || Is(r.Owner, AdbLegacy)))
            .Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var port = records.FirstOrDefault(r => r.Type == 33 && r.Port > 0 && adbInstances.Contains(r.Owner))?.Port ?? 5555;
        return new(name, address.ToString(), android ? port : 5555, android ? Platform.Android : Platform.Other,
            Brand: TvBrands.KeyFor(model + " " + name));
    }
}
