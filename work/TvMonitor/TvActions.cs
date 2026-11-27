using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

enum TvAction { PowerOn, PowerOff, Restart }

static partial class Network
{
    private const string PopupPackage = "local.tvmonitor.popup";

    internal static async Task<string> MessageTv(Tv tv, string message, CancellationToken ct)
    {
        var state = await Read(tv, ct);
        if (state.Power != Power.On)
            throw new IOException(Tf($"Turn on and connect {tv.Name} before sending. {state.Detail}"));
        if (tv.Platform == Platform.WebOS)
            return await WithWebOs(tv, async (socket, token) =>
            {
                var alert = await Exchange(socket, "alert", "system.notifications/createAlert", new
                {
                    title = "Message from TV Monitor", message, modal = true,
                    buttons = new[] { new { label = T("OK"), focus = true } }
                }, token);
                if (Success(alert)) return Tf($"{tv.Name} accepted the popup.");
                // Fall back only after rejection so an uncertain response cannot send twice.
                if (!Rejected(alert)) throw new IOException(T("The LG popup response could not be verified."));
                var toast = await Exchange(socket, "toast", "system.notifications/createToast", new { message }, token);
                if (Success(toast)) return Tf($"{tv.Name} accepted the message as a brief notification.");
                throw new IOException(Tf($"LG declined the message. Try Pair LG again. {Rejection(toast)}"));
            }, ct);

        var serial = Serial(tv);
        await EnsureHelper(tv, 1, ct);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        var reply = await RunAdb(ct, 15000, "-s", serial, "shell",
            $"am start -W -n {PopupPackage}/.PopupActivity --es message64 {encoded}");
        if (!Regex.IsMatch(reply, @"(?m)^Status: ok\s*$"))
            throw new IOException(Tf($"Android could not open the popup. {ShortError(reply)}"));
        return Tf($"{tv.Name} accepted the popup. It closes with OK or after 30 seconds.");
    }

    private static async Task EnsureHelper(Tv tv, int requiredVersion, CancellationToken ct)
    {
        var serial = Serial(tv);
        var package = await RunAdb(ct, "-s", serial, "shell", $"dumpsys package {PopupPackage}");
        var version = Regex.Match(package, @"\bversionCode=(\d+)").Groups[1].Value;
        if (int.TryParse(version, out var installedVersion) && installedVersion >= requiredVersion) return;
        ct.ThrowIfCancellationRequested();
        var apk = Path.Combine(AppContext.BaseDirectory, "tv-popup.apk");
        if (!File.Exists(apk)) throw new IOException(T("The bundled TV Monitor helper APK is missing."));
        await InstallApk(tv, apk, 60000, CancellationToken.None);
        ct.ThrowIfCancellationRequested();
    }

    internal static async Task InstallApp(Tv tv, string apk, CancellationToken ct)
    {
        if (tv.Platform != Platform.Android)
            throw new IOException(T("APK installation requires an Android TV."));
        if (!Path.GetExtension(apk).Equals(".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(apk))
            throw new IOException(T("Choose an existing Android APK file."));
        if (!await PortOpen(tv.Address, tv.Port, ct))
            throw new IOException(Tf($"Connect or wake {tv.Name} before installing an app."));
        var serial = Serial(tv);
        await RunAdb(ct, "connect", serial);
        var state = await RunAdb(ct, "-s", serial, "get-state");
        if (state.Trim() != "device")
            throw new IOException(Tf($"ADB is unavailable on {tv.Name}. Check the TV’s debugging approval. {ShortError(state)}"));
        await InstallApk(tv, apk, 600000, ct);
    }

    private static async Task InstallApk(Tv tv, string apk, int timeout, CancellationToken ct)
    {
        var output = await RunAdb(ct, timeout, "-s", Serial(tv), "install", "-r", apk);
        if (!Regex.IsMatch(output, @"(?m)^Success\s*$"))
            throw new IOException(Tf($"Android did not install the APK. {ShortError(output)}"));
    }

    internal static async Task<string> Control(Tv tv, TvAction action, CancellationToken ct)
    {
        var state = await Read(tv, ct);
        if (action == TvAction.PowerOn && state.Power == Power.On) return Tf($"{tv.Name} is already on.");
        if (action == TvAction.PowerOff && state.Power == Power.Standby) return Tf($"{tv.Name} is already in standby.");
        if (action == TvAction.Restart) return await RebootAndroid(tv, ct);
        if (action == TvAction.PowerOff && tv.Platform == Platform.Other)
            throw new IOException(T("Power off requires a supported Android TV or LG webOS connection."));

        if (action == TvAction.PowerOn)
        {
            if (tv.Platform == Platform.Android && await PortOpen(tv.Address, tv.Port, ct))
                await AndroidKey(tv, "KEYCODE_WAKEUP", ct);
            else if (tv.Platform == Platform.WebOS && state.Power == Power.ScreenOff)
                await WebOsCommand(tv, "com.webos.service.tvpower/power/turnOnScreen", ct);
            else
                await WakeOnLan(tv, ct);
            if (tv.Platform == Platform.Other)
                return Tf($"Wake packet sent to {tv.Name}. This TV type cannot report its power state; power-on is unconfirmed.");
        }
        else if (tv.Platform == Platform.Android)
            await AndroidKey(tv, "KEYCODE_SLEEP", ct);
        else
        {
            try { await WebOsCommand(tv, "system/turnOff", ct); }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        for (var attempt = 0; attempt < 7; attempt++)
        {
            await Task.Delay(1500, ct);
            var updated = await Read(tv, ct);
            if (action == TvAction.PowerOn && updated.Power == Power.On) return Tf($"{tv.Name} is on.");
            if (action == TvAction.PowerOff && updated.Power == Power.Standby) return Tf($"{tv.Name} is in standby.");
        }

        return action == TvAction.PowerOn
            ? Tf($"Wake requested for {tv.Name}. The TV did not report that it was on.")
            : Tf($"Standby requested for {tv.Name}. The resulting power state could not be confirmed.");
    }

    private static async Task AndroidKey(Tv tv, string key, CancellationToken ct)
    {
        var reply = await RunAdb(ct, "-s", Serial(tv), "shell", $"input keyevent {key} && echo TVMONITOR_OK");
        if (!Regex.IsMatch(reply, @"(?m)^TVMONITOR_OK\s*$"))
            throw new IOException(Tf($"Android did not confirm the key press. {ShortError(reply)}"));
    }

    private static async Task<string> RebootAndroid(Tv tv, CancellationToken ct)
    {
        if (tv.Platform != Platform.Android) throw new IOException(T("Restart is available for Android TV."));
        var serial = Serial(tv);
        async Task<string> BootId() =>
            (await RunAdb(ct, "-s", serial, "shell", "cat /proc/sys/kernel/random/boot_id")).Trim();
        if (!Guid.TryParse(await BootId(), out var original))
            throw new IOException(T("Could not read the Android boot ID. Check the TV’s ADB connection."));
        var reply = await RunAdb(ct, "-s", serial, "reboot");
        if (!string.IsNullOrWhiteSpace(reply))
            throw new IOException(Tf($"ADB reported a restart error. {ShortError(reply)}"));
        var until = DateTime.UtcNow.AddSeconds(80);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(2500, ct);
            if (!await PortOpen(tv.Address, tv.Port, ct)) continue;
            try
            {
                await RunAdb(ct, "connect", serial);
                if (Guid.TryParse(await BootId(), out var current) && current != original)
                    return Tf($"{tv.Name} restarted and reconnected.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return Tf($"Restart requested for {tv.Name}. A new Android boot could not be verified.");
    }

    private static async Task WebOsCommand(Tv tv, string endpoint, CancellationToken ct)
    {
        await WithWebOs(tv, async (socket, token) =>
        {
            var reply = await Exchange(socket, "power-action", endpoint, new { }, token);
            if (!Success(reply)) throw new IOException(Tf($"LG did not acknowledge the command. {Rejection(reply)}"));
            return "";
        }, ct);
    }

    private static async Task<TResult> WithWebOs<TResult>(
        Tv tv, Func<ClientWebSocket, CancellationToken, Task<TResult>> action, CancellationToken ct)
    {
        if (tv.Key is null) throw new IOException(T("Choose Pair LG and approve the request on the TV first."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(6500);
        using var socket = new ClientWebSocket();
        if (await Register(socket, tv, false, timeout.Token) is null)
            throw new IOException(T("Pair LG again to authorize this action."));
        return await action(socket, timeout.Token);
    }

    private static bool Success(JsonElement reply) => Child(Child(reply, "payload"), "returnValue").ValueKind == JsonValueKind.True;
    private static bool Rejected(JsonElement reply) => Str(reply, "type") == "error" ||
        Child(Child(reply, "payload"), "returnValue").ValueKind == JsonValueKind.False;
    private static string Rejection(JsonElement reply) => Str(reply, "error") is { Length: > 0 } error
        ? error : Str(Child(reply, "payload"), "errorText");
    private static string ShortError(string error) =>
        error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    internal static bool ValidMac(string? mac) => TryMac(mac, out _);
    internal static string MacKey(string? mac) => TryMac(mac, out var bytes) ? Convert.ToHexString(bytes) : "";
    private static bool TryMac(string? value, out byte[] mac)
    {
        mac = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { mac = PhysicalAddress.Parse(value.Replace(":", "").Replace("-", "")).GetAddressBytes(); }
        catch (FormatException) { return false; }
        return mac.Length == 6 && (mac[0] & 1) == 0 && mac.Any(b => b != 0);
    }

    private static async Task WakeOnLan(Tv tv, CancellationToken ct)
    {
        if (!TryMac(tv.Mac, out var mac))
            throw new IOException(T("No wake address is saved. Enter the TV’s network MAC address in Edit TV."));
        var address = IPAddress.Parse(tv.Address);
        var links = address.AddressFamily == AddressFamily.InterNetwork
            ? new[] { LanAddress(tv.Address) }.OfType<UnicastIPAddressInformation>().ToArray()
            : NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                    (address.ScopeId == 0 || (n.Supports(NetworkInterfaceComponent.IPv6) && n.GetIPProperties().GetIPv6Properties()?.Index == address.ScopeId)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)).ToArray();
        // A saved IP can change while the TV sleeps. A MAC-targeted broadcast can
        // still wake it on an attached LAN without assuming that old IP is current.
        if (links.Length == 0)
            links = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)).ToArray();
        if (links.Length == 0)
            throw new IOException(T("Wake-on-LAN needs an IPv4 connection on the TV's network adapter and the TV's real MAC address."));
        var magic = WakePacket(mac);
        foreach (var lan in links)
        {
            var local = lan.Address.GetAddressBytes(); var mask = lan.IPv4Mask.GetAddressBytes();
            var broadcast = new IPAddress(local.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
            using var udp = new UdpClient(new IPEndPoint(lan.Address, 0)) { EnableBroadcast = true };
            for (var i = 0; i < 3; i++)
            {
                foreach (var port in new[] { 9, 7 }) await udp.SendAsync(magic, new IPEndPoint(broadcast, port), ct);
                await Task.Delay(180, ct);
            }
        }
    }

    internal static byte[] WakePacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("Expected a 6-byte MAC address.", nameof(mac));
        var packet = new byte[102]; Array.Fill(packet, (byte)255, 0, 6);
        for (int offset = 6; offset < packet.Length; offset += 6) mac.CopyTo(packet, offset);
        return packet;
    }

    private static UnicastIPAddressInformation? LanAddress(string host)
    {
        var remote = IPAddress.Parse(host).GetAddressBytes();
        if (remote.Length != 4) return null;
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
            .FirstOrDefault(ip =>
            {
                var local = ip.Address.GetAddressBytes();
                var mask = ip.IPv4Mask.GetAddressBytes();
                return local.Select((b, i) => ((b ^ remote[i]) & mask[i]) == 0).All(match => match);
            });
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint SendARP(uint destination, uint source, [Out] byte[] mac, ref uint length);

    internal static string? LearnMac(string host)
    {
        if (IPAddress.TryParse(host, out var remote) && remote.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (remote.ScopeId == 0) return null;
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "interface", "ipv6", "show", "neighbors", "interface=" + remote.ScopeId }) info.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(2500)) { process.Kill(); return null; }
                foreach (var line in output.GetAwaiter().GetResult().Split('\n'))
                {
                    var cells = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (cells.Length >= 2 && IPAddress.TryParse(cells[0], out var neighbor) && neighbor.GetAddressBytes().SequenceEqual(remote.GetAddressBytes()))
                        return cells.Skip(1).FirstOrDefault(ValidMac)?.Replace('-', ':').ToUpperInvariant();
                }
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return null;
        }
        try
        {
            if (LanAddress(host) is not { } lan) return null;
            uint length = 8;
            var mac = new byte[length];
            return SendARP(BitConverter.ToUInt32(IPAddress.Parse(host).GetAddressBytes()),
                BitConverter.ToUInt32(lan.Address.GetAddressBytes()), mac, ref length) == 0 && length == 6
                ? Convert.ToHexString(mac.AsSpan(0, 6)) : null;
        }
        catch (NetworkInformationException) { return null; }
    }
}
