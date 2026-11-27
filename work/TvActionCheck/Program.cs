using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using var token = new CancellationTokenSource(TimeSpan.FromSeconds(18));
if (args.Contains("--scan"))
{
    const string service = "_androidtvremote2._tcp.local";
    var query = Network.Question(service);
    if (query[5] != 1 || !query[^4..].SequenceEqual(new byte[] { 0, 12, 0, 1 }))
        throw new Exception("Invalid mDNS query");
    var packet = new byte[12] { 0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0 }
        .Concat(query[12..]).Concat(new byte[] { 0xc0, 0x0c, 0, 12, 0, 1, 0, 0, 0, 30, 0, 5, 2, 84, 86, 0xc0, 0x0c }).ToArray();
    var records = Network.ParseDns(packet);
    if (records.Count != 1 || records[0].Value != "TV." + service)
        throw new Exception("Compressed name was not decoded");
    records.Add(new("_adb-tls-connect._tcp.local", 12, "debug._adb-tls-connect._tcp.local"));
    records.Add(new("debug._adb-tls-connect._tcp.local", 33, "", 43717));
    var found = Network.IdentifyTv(IPAddress.Parse("192.168.1.25"), records);
    if (found is not { Name: "TV", Port: 43717, Platform: Platform.Android } ||
        Network.IdentifyTv(IPAddress.Loopback, [new("_adb._tcp.local", 12, "phone")]) is not null ||
        Network.ParseDns(packet[..^1]).Count != 0)
        throw new Exception("Device detection failed");
    Console.WriteLine("PASS compressed mDNS, dynamic ADB port, malformed packet, and phone rejection.");
    var devices = await Network.Discover(token.Token);
    foreach (var device in devices)
        Console.WriteLine($"{device.Name} | {device.Platform} | {device.Address}:{device.Port}");
    Console.WriteLine($"Live scan: {devices.Count} devices");
    return;
}
if (args.Contains("--probe-talk"))
{
    var orca = new Tv("Orca 65", "192.168.1.46", 5555, Platform.Android);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
    await using (var link = await Network.OpenTalk(orca, deadline.Token))
    {
        var pcm = new byte[640];
        for (var frame = 0; frame < 50; frame++)
        {
            await link.Stream.WriteAsync(pcm, deadline.Token);
            await Task.Delay(20, deadline.Token);
        }
        await Task.Delay(250, deadline.Token);
    }
    Console.WriteLine("PASS: Orca acknowledged Android AudioTrack and received silent PCM over the ADB tunnel. The PC microphone was not opened.");
    return;
}
if (args.Contains("--skyworth-tone"))
{
    var skyworth = new Tv("Basement Skyworth", "192.168.1.28", 5555, Platform.Android);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    await using (var link = await Network.OpenTalk(skyworth, deadline.Token))
    {
        var frame = new byte[640];
        for (var i = 0; i < 320; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * 2),
                (short)(Math.Sin(2 * Math.PI * 400 * i / 16000) * 1300));
        for (var i = 0; i < 30; i++)
        {
            await link.Stream.WriteAsync(frame, deadline.Token);
            await Task.Delay(20, deadline.Token);
        }
        await Task.Delay(250, deadline.Token);
    }
    Console.WriteLine("Skyworth accepted a brief low-level test tone; microphone unused.");
    return;
}
if (args.Contains("--probe-lg"))
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "devices.json");
    var device = JsonSerializer.Deserialize<List<Tv>>(File.ReadAllText(path))!.Single(tv => tv.Name == "LG C1");
    using var connection = new Network.RemoteSession(device);
    await connection.Connect(token.Token);
    Console.WriteLine("PASS: LG C1 granted the pointer input socket and accepted a connection. No key sent.");
    return;
}
var ct = token.Token;
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var tv = new Tv("Simulated LG", "127.0.0.1", port, Platform.WebOS, "example");
const string message = "رسالة اختبار / Test: $(literal) ' 😀";
var state = "Active";
bool rejectAlert = false;
int alerts = 0, toasts = 0, offs = 0, pointerOpens = 0;
var keys = new System.Collections.Concurrent.ConcurrentQueue<string>();
var server = Task.Run(async () =>
{
    while (!ct.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        _ = Serve(client);
    }
}, ct);

async Task Serve(TcpClient client)
{
    using (client)
    {
        var stream = client.GetStream();
        var header = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, ct) > 0)
        {
            header.Add(one[0]);
            if (header.Count >= 4 && Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n")) break;
        }
        var http = Encoding.ASCII.GetString(header.ToArray());
        var key = http.Split("\r\n").FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        if (key is null) return;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Split(':', 2)[1].Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
        var buffer = new byte[8192];
        var pointer = http.StartsWith("GET /pointer ");
        if (pointer) Interlocked.Increment(ref pointerOpens);
        try
        {
            while (true)
            {
                var frame = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (frame.MessageType != WebSocketMessageType.Text) return;
                if (pointer)
                {
                    keys.Enqueue(Encoding.ASCII.GetString(buffer, 0, frame.Count));
                    continue;
                }
                using var json = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
                var request = json.RootElement;
                var id = request.GetProperty("id").GetString();
                string type = "response";
                object payload;
                if (id == "register")
                {
                    type = "registered";
                    payload = new Dictionary<string, string> { ["client-key"] = "example" };
                }
                else
                {
                    var uri = request.GetProperty("uri").GetString()!;
                    payload = new { returnValue = true };
                    if (uri.EndsWith("/getPowerState")) payload = new { state, returnValue = true };
                    if (uri.EndsWith("/getForegroundAppInfo")) payload = new { appId = "com.webos.app.home" };
                    if (uri.EndsWith("/createAlert") || uri.EndsWith("/createToast"))
                    {
                        if (request.GetProperty("payload").GetProperty("message").GetString() != message) throw new Exception("Text changed");
                        if (uri.EndsWith("/createAlert"))
                        {
                            Interlocked.Increment(ref alerts);
                            if (rejectAlert) payload = new { returnValue = false };
                        }
                        else Interlocked.Increment(ref toasts);
                    }
                    if (uri.EndsWith("/getPointerInputSocket")) payload = new { socketPath = $"ws://127.0.0.1:{port}/pointer" };
                    if (uri.EndsWith("/turnOff")) { state = "Active Standby"; Interlocked.Increment(ref offs); }
                }
                await ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new { type, id, payload })), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (WebSocketException) { }
    }
}

if (!(await Network.MessageTv(tv, message, ct)).Contains("accepted the popup") || alerts != 1 || toasts != 0) throw new Exception("Modal path failed");
rejectAlert = true;
if (!(await Network.MessageTv(tv, message, ct)).Contains("brief notification") || alerts != 2 || toasts != 1) throw new Exception("Fallback failed");
if (!(await Network.Control(tv, TvAction.PowerOff, ct)).Contains("is in standby") || offs != 1) throw new Exception("Power off verification failed");
if (Network.ParseAndroid("mWakefulness=Asleep\nDisplay Power: state=OFF").Power != Power.Standby) throw new Exception("Android standby parser");
if (!Network.ValidMac("00:11:22:33:44:55") || Network.ValidMac("FF:FF:FF:FF:FF:FF")) throw new Exception("Wake MAC validation");
using var remote = new Network.RemoteSession(tv);
await remote.Press(RemoteKey.Up, ct);
await remote.Press(RemoteKey.Ok, ct);
await remote.Press(RemoteKey.Nine, ct);
while (keys.Count < 3) await Task.Delay(20, ct);
if (pointerOpens != 1 || !keys.SequenceEqual(new[] { "type:button\nname:UP\n\n", "type:button\nname:ENTER\n\n", "type:button\nname:9\n\n" }))
    throw new Exception("Remote key transport failed");
listener.Stop();
Console.WriteLine("PASS: one persistent LG remote socket received UP, ENTER and 9 in order.");
Console.WriteLine("PASS: webOS Unicode modal, toast fallback, power-off acknowledgment and standby verification, Android standby, Wake MAC validation");




