using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

enum Platform { Android, WebOS, Other }
enum Power { Checking, On, Standby, ScreenOff, Unknown }
sealed record Tv(string Name, string Address, int Port, Platform Platform, string? Key = null, string? Mac = null, string? Brand = null);
sealed record Reading(Power Power, string Source, string Detail, bool Pair = false, int Sdk = 0, string? Maker = null);

static partial class Network
{
    internal static readonly string Engine = Path.Combine(AppContext.BaseDirectory, "scrcpy");
    internal static readonly string Adb = Path.Combine(Engine, "adb.exe");
    internal const string AdbPort = "5041";
    private static readonly SemaphoreSlim AdbServer = new(1);
    private static readonly HttpClient DiscoveryHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false
    });

    internal static string Serial(Tv tv) => TvAddress.Endpoint(tv.Address, tv.Port);

    internal static async Task<Reading> Read(Tv tv, CancellationToken ct)
    {
        try
        {
            return tv.Platform switch
            {
                Platform.Android => await Android(tv, ct),
                Platform.WebOS => (await WebOs(tv, false, ct)).Reading,
                _ => new(Power.Unknown, "", await PortOpen(tv.Address, tv.Port, ct)
                    ? T("A network service is reachable. Power state and native controls are unavailable for this TV type.")
                    : T("Set the TV type and current IP using Edit TV."))
            };
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or
                                    OperationCanceledException or System.ComponentModel.Win32Exception or JsonException)
        {
            return new(Power.Unknown, "", ex is OperationCanceledException ? T("No response before timeout.") : ex.Message);
        }
    }

    private static async Task<bool> PortOpen(string address, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1600);
        using var client = new TcpClient(IPAddress.Parse(address).AddressFamily);
        try { await client.ConnectAsync(address, port, timeout.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    internal static Task<string> RunAdb(CancellationToken ct, params string[] args) => RunAdb(ct, 6500, args);

    private static async Task<string> RunAdb(CancellationToken ct, int milliseconds, params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(milliseconds);
        await AdbServer.WaitAsync(timeout.Token);
        try
        {
            var info = new ProcessStartInfo(Adb)
            {
                WorkingDirectory = Engine,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-P", AdbPort }.Concat(args)) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException(T("ADB could not start."));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw;
            }
            return (await output) + (await errors);
        }
        finally { AdbServer.Release(); }
    }

    private static async Task<Reading> Android(Tv tv, CancellationToken ct)
    {
        if (!await PortOpen(tv.Address, tv.Port, ct))
            return new(Power.Unknown, "", T("ADB did not answer. The TV may be asleep, disconnected, or using a different IP."));
        var serial = Serial(tv);
        var connected = await RunAdb(ct, "connect", serial);
        var report = await RunAdb(ct, "-s", serial, "shell",
            "echo SDK=$(getprop ro.build.version.sdk); " +
            "echo MAKER=$(getprop ro.product.manufacturer) $(getprop ro.product.brand) $(getprop ro.product.model); " +
            "dumpsys power; echo ====ACTIVITY====; dumpsys activity activities");
        if ((connected + report).Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            connected.Contains("authenticate", StringComparison.OrdinalIgnoreCase))
            return new(Power.Unknown, "", T("Approve the USB debugging prompt on the TV, then refresh."));
        if (report.Contains("device offline", StringComparison.OrdinalIgnoreCase))
        {
            await RunAdb(ct, "disconnect", serial);
            return new(Power.Unknown, "", T("ADB session disconnected. The next refresh will reconnect."));
        }
        return ParseAndroid(report);
    }

    internal static Reading ParseAndroid(string report)
    {
        var display = Regex.Match(report, @"Display Power:\s*state=(\w+)").Groups[1].Value;
        var wake = Regex.Match(report, @"mWakefulness=(\w+)").Groups[1].Value;
        int.TryParse(Regex.Match(report, @"(?m)^SDK=(\d+)").Groups[1].Value, out var sdk);
        var maker = Regex.Match(report, @"(?m)^MAKER=(.*)$").Groups[1].Value.Trim();
        if (maker.Length == 0) maker = null;
        if (display == "OFF" && wake is "Awake" or "Dreaming")
            return new(Power.ScreenOff, "", T("Android reports that the display is off."), Sdk: sdk, Maker: maker);
        if (display is "OFF" or "DOZE" or "DOZE_SUSPEND" || wake is "Asleep" or "Dozing")
            return new(Power.Standby, "", T("Android reports standby or sleep."), Sdk: sdk, Maker: maker);
        if (display == "ON" || wake is "Awake" or "Dreaming")
        {
            var activity = Regex.Match(report, @"(?:topResumedActivity|mResumedActivity|ResumedActivity)\s*[:=][^\r\n]*?\s([\w.]+)/[\w.$]+");
            var source = Friendly(activity.Groups[1].Value);
            return new(Power.On, source, T("Live view available for Android surfaces the TV can capture."), Sdk: sdk, Maker: maker);
        }
        return new(Power.Unknown, "", T("No display power state was returned. Check the TV’s ADB authorization."), Maker: maker);
    }

    internal static string Friendly(string app) => app switch
    {
        "com.google.android.youtube.tv" or "youtube.leanback.v4" => "YouTube",
        "com.netflix.ninja" or "netflix" => "Netflix",
        "org.videolan.vlc" => "VLC",
        "com.webos.app.livetv" => T("Live TV"),
        "com.webos.app.home" or "com.google.android.tvlauncher" or "com.google.android.apps.tv.launcherx" => T("Home"),
        "com.amazon.amazonvideo.livingroom" or "amazon" => "Prime Video",
        "com.disney.disneyplus" or "com.disney.disneyplus-prod" => "Disney+",
        _ when app.StartsWith("com.webos.app.hdmi", StringComparison.Ordinal) => "HDMI " + app["com.webos.app.hdmi".Length..],
        _ => app
    };

    internal static async Task<(Reading Reading, string? Key)> WebOs(Tv tv, bool pair, CancellationToken ct)
    {
        if (!await PortOpen(tv.Address, tv.Port, ct))
            return (new(Power.Unknown, "", T("webOS did not answer. Power state is unknown.")), null);
        if (tv.Key is null && !pair)
            return (new(Power.Unknown, "", T("webOS reachable. Pair to read power and the current app or input."), true), null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(pair ? 45000 : 4500);
        var token = timeout.Token;
        using var socket = new ClientWebSocket();
        var key = await Register(socket, tv, pair, token);
        if (key is null)
            return (new(Power.Unknown, "", T("Pairing is required. Choose Pair LG and approve the TV prompt."), true), null);
        // A pairing succeeds even if the firmware times out on a later status request.
        try
        {
            var powerReply = await Request(socket, "power", "com.webos.service.tvpower/power/getPowerState", token);
            var state = Str(powerReply, "state");
            var power = state switch
            {
                "Active" => Power.On, "Screen Off" => Power.ScreenOff,
                "Active Standby" or "Suspend" or "Standby" or "Power Off" => Power.Standby,
                _ => Power.Unknown
            };
            var source = "";
            if (power == Power.On)
            {
                var foreground = await Request(socket, "source", "com.webos.applicationManager/getForegroundAppInfo", token);
                var app = Str(foreground, "appId");
                source = Friendly(app);
                if (app == "com.webos.app.livetv")
                {
                    var channel = await Request(socket, "channel", "tv/getCurrentChannel", token);
                    var name = Str(channel, "channelName");
                    if (name.Length > 0) source += " · " + name;
                }
            }
            return (new(power, source, power == Power.Unknown ? T("Paired. Power state unavailable.") : T("webOS status only · live video unavailable.")), key);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new(Power.Unknown, "", T("Paired. The TV timed out when asked for status.")), key);
        }
    }

    private static async Task<string?> Register(ClientWebSocket socket, Tv tv, bool pair, CancellationToken token)
    {
        socket.Options.Proxy = null;
        await socket.ConnectAsync(new Uri("ws://" + Serial(tv)), token);
        var payload = new Dictionary<string, object>
        {
            ["pairingType"] = "PROMPT",
            ["manifest"] = new
            {
                manifestVersion = 1, appVersion = "1.0", appId = "local.tvmonitor",
                localizedAppNames = new Dictionary<string, string> { [""] = "TV Monitor" },
                permissions = new[] { "READ_POWER_STATE", "CONTROL_POWER", "READ_APP_STATUS", "READ_RUNNING_APPS",
                    "READ_CURRENT_CHANNEL", "WRITE_NOTIFICATION_ALERT", "WRITE_NOTIFICATION_TOAST", "CONTROL_MOUSE_AND_KEYBOARD" }
            }
        };
        if (tv.Key is not null && !pair) payload["client-key"] = tv.Key;
        await Send(socket, new { type = "register", id = "register", payload }, token);
        while (true)
        {
            var registration = await Receive(socket, token);
            var type = Str(registration, "type");
            var data = Child(registration, "payload");
            if (type == "registered") return Str(data, "client-key") is { Length: > 0 } key ? key : tv.Key;
            if (type == "error" || (!pair && Str(data, "pairingType") == "PROMPT"))
                return null;
        }
    }

    private static async Task<JsonElement> Request(ClientWebSocket socket, string id, string uri, CancellationToken token) =>
        Child(await Exchange(socket, id, uri, new { }, token), "payload");

    private static async Task<JsonElement> Exchange(ClientWebSocket socket, string id, string uri, object payload, CancellationToken token)
    {
        await Send(socket, new { type = "request", id, uri = "ssap://" + uri, payload }, token);
        while (true)
        {
            var msg = await Receive(socket, token);
            if (Str(msg, "id") == id) return msg;
        }
    }

    private static Task Send(ClientWebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonElement> Receive(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var bytes = new MemoryStream();
        WebSocketReceiveResult read;
        do
        {
            read = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (read.MessageType != WebSocketMessageType.Text || bytes.Length + read.Count > 131072)
                throw new IOException(T("Unexpected webOS response."));
            bytes.Write(buffer, 0, read.Count);
        } while (!read.EndOfMessage);
        using var doc = JsonDocument.Parse(bytes.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement Child(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;

    private static string Str(JsonElement value, string name)
    {
        var child = Child(value, name);
        return child.ValueKind == JsonValueKind.String ? child.GetString() ?? "" : "";
    }

    internal static async Task<List<Tv>> Discover(CancellationToken ct, IEnumerable<Tv>? saved = null)
    {
        var links = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.SupportsMulticast &&
                          nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(link => (link.Address.AddressFamily == AddressFamily.InterNetwork &&
                           !link.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) || link.Address.IsIPv6LinkLocal)
            .DistinctBy(link => link.Address)
            .ToArray();
        var ssdp = Task.WhenAll(links.Select(link => DiscoverSsdp(link, ct)));
        var mdns = Task.WhenAll(links.Select(link => DiscoverMdns(link, ct)));
        var recovered = RecoverKnownTvs(saved?.ToArray() ?? [], ct);
        await Task.WhenAll(ssdp, mdns, recovered);
        ct.ThrowIfCancellationRequested();
        var descriptions = await Task.WhenAll(ssdp.Result.SelectMany(list => list).Distinct().Take(60)
            .Select(uri => Describe(uri, ct)));
        var discovered = recovered.Result.Concat(mdns.Result.SelectMany(list => list)).Concat(descriptions.OfType<Tv>())
            .GroupBy(tv => tv.Address)
            .Select(group => group.OrderBy(tv => tv.Platform == Platform.Other)
                .ThenBy(tv => tv.Platform == Platform.Android && tv.Port == 5555).First())
            .OrderBy(tv => tv.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return (await Task.WhenAll(discovered.Select(async tv => ValidMac(tv.Mac) ? tv :
            tv with { Mac = await Task.Run(() => LearnMac(tv.Address), ct) }))).ToList();
    }

    private static async Task<HashSet<Uri>> DiscoverSsdp(UnicastIPAddressInformation link, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2800);
        var ipv6 = link.Address.AddressFamily == AddressFamily.InterNetworkV6;
        var endpoint = new IPEndPoint(ipv6 ? new IPAddress(IPAddress.Parse("ff02::c").GetAddressBytes(), link.Address.ScopeId) : IPAddress.Parse("239.255.255.250"), 1900);
        var locations = new HashSet<Uri>();
        try
        {
            using var udp = DiscoverySocket(link, 2);
            foreach (var target in new[] { "ssdp:all", "urn:lge-com:service:webos-second-screen:1", "urn:dial-multiscreen-org:service:dial:1" })
            {
                var search = Encoding.ASCII.GetBytes(
                    $"M-SEARCH * HTTP/1.1\r\nHOST: {(ipv6 ? "[FF02::C]:1900" : "239.255.255.250:1900")}\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {target}\r\n\r\n");
                await udp.SendAsync(search, endpoint, timeout.Token);
            }
            while (locations.Count < 40)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (!OnLink(link, reply.RemoteEndPoint.Address)) continue;
                if (ipv6 && reply.RemoteEndPoint.Address.ScopeId == 0) reply.RemoteEndPoint.Address.ScopeId = link.Address.ScopeId;
                var match = Regex.Match(Encoding.ASCII.GetString(reply.Buffer), @"^location:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri) &&
                    uri.Scheme == "http" && uri.UserInfo.Length == 0 &&
                    TvAddress.LocalUri(uri, reply.RemoteEndPoint.Address) is { } scoped)
                    locations.Add(scoped);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }
        return locations;
    }

    private static async Task<Tv?> Describe(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1800);
        try
        {
            using var response = await DiscoveryHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (body.Length + count > 131072) return null;
                body.Write(buffer, 0, count);
            }
            body.Position = 0;
            using var reader = System.Xml.XmlReader.Create(body, new()
            {
                Async = true, DtdProcessing = System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument = 131072
            });
            var xml = await XDocument.LoadAsync(reader, LoadOptions.None, timeout.Token);
            var rootDevice = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "device");
            string Find(string name) => rootDevice?.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
            var label = Find("friendlyName");
            var product = Find("manufacturer") + " " + Find("modelName") + " " + Find("modelDescription");
            var kind = Find("deviceType");
            var webOs = product.Contains("webOS", StringComparison.OrdinalIgnoreCase) ||
                        xml.Descendants().Any(x => x.Name.LocalName == "serviceType" && x.Value.Contains("webos", StringComparison.OrdinalIgnoreCase));
            var android = Regex.IsMatch(product, "Android TV|Google TV", RegexOptions.IgnoreCase);
            if (label.Length == 0 || !(webOs || android || kind.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase)))
                return null;
            var platform = webOs ? Platform.WebOS : android ? Platform.Android : Platform.Other;
            return new(label[..Math.Min(label.Length, 80)], IPAddress.Parse(uri.DnsSafeHost).ToString(), webOs ? 3000 : 5555, platform,
                Brand: TvBrands.KeyFor(product));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Xml.XmlException or OperationCanceledException)
        {
            return null;
        }
    }
}



