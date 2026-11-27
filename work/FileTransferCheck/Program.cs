using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

static class Program
{
    internal static Icon AppIcon { get; } = SystemIcons.Application;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    [STAThread]
    static async Task Main(string[] args)
    {
        if (args.Contains("--ui")) { RenderUi(); return; }
        if (args.Contains("--marks")) { RenderMarks(); return; }
        if (args.Length == 4 && args[0] == "--configure-tv")
        {
            if (System.Diagnostics.Process.GetProcessesByName("TV Monitor").Length > 0) throw new IOException("Close TV Monitor before changing its saved configuration.");
            var address = TvAddress.Parse(args[2]).ToString();
            Check(Network.ValidMac(args[3]), "valid wake MAC required");
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "devices.json");
            var original = await File.ReadAllBytesAsync(path);
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<Tv>>(original)!;
            var matches = saved.Select((tv, i) => (tv, i)).Where(item => item.tv.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
            Check(matches.Length == 1, "one matching saved TV required");
            var (current, index) = matches[0];
            saved[index] = current with { Address = address, Mac = string.Join(':', Convert.FromHexString(Network.MacKey(args[3])).Select(b => b.ToString("X2"))) };
            var replacement = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(saved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            RestorePointStore.Default.BeforeConfigChange("devices.json", replacement);
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "settings changed during preparation");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, replacement); File.Move(temporary, path, true);
            var verified = System.Text.Json.JsonSerializer.Deserialize<List<Tv>>(await File.ReadAllBytesAsync(path))!;
            Check(verified.SequenceEqual(saved), "saved settings verified");
            Console.WriteLine($"Saved and verified {saved[index].Name}: {saved[index].Address}, MAC {saved[index].Mac}. Other TV records preserved; app restore point created.");
            return;
        }
        if (args.Length == 2 && args[0] == "--wake-tv")
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "devices.json");
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<Tv>>(await File.ReadAllTextAsync(path))!;
            var tv = saved.Single(t => t.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            Console.WriteLine("Before: " + (await Network.Read(tv, timeout.Token)).Detail);
            Console.WriteLine(await Network.Control(tv, TvAction.PowerOn, timeout.Token));
            foreach (var found in await Network.Discover(timeout.Token, new[] { tv }))
                if (Network.MacKey(found.Mac) == Network.MacKey(tv.Mac)) Console.WriteLine("Matching TV discovered at " + found.Address);
            return;
        }
        if (args.Contains("--recover-saved"))
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "devices.json");
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<Tv>>(await File.ReadAllTextAsync(path))!;
            foreach (var tv in await Network.Discover(CancellationToken.None, saved))
                Console.WriteLine($"{tv.Name}: {tv.Address}:{tv.Port}, {tv.Platform}, MAC learned={Network.ValidMac(tv.Mac)}");
            return;
        }
        if (args.Contains("--status-saved"))
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "devices.json");
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<Tv>>(await File.ReadAllTextAsync(path))!;
            foreach (var tv in saved) { var reading = await Network.Read(tv, CancellationToken.None); Console.WriteLine(tv.Name + ": " + System.Text.Json.JsonSerializer.Serialize(reading)); }
            return;
        }
        if (args.Contains("--wake-saved"))
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "devices.json");
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<Tv>>(await File.ReadAllTextAsync(path))!;
            foreach (var tv in saved)
            {
                if (!Network.ValidMac(tv.Mac)) { Console.WriteLine(tv.Name + ": no saved wake MAC; skipped."); continue; }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
                try { Console.WriteLine(tv.Name + ": " + await Network.Control(tv, TvAction.PowerOn, timeout.Token)); }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
                { Console.WriteLine(tv.Name + ": " + e.Message); }
            }
            return;
        }
        if (args.Contains("--probe6"))
        {
            foreach (var scope in TvAddress.Scopes())
            {
                var ip = TvAddress.Parse("fe80::a3d8:4e29:2c7b:ec49", scope.Index);
                using var ping = new System.Net.NetworkInformation.Ping();
                try { var reply = await ping.SendPingAsync(ip, 1500); Console.WriteLine($"{scope}: {reply.Status}"); }
                catch (System.Net.NetworkInformation.PingException e) { Console.WriteLine($"{scope}: {e.Message}"); }
                Console.WriteLine("Neighbor MAC: " + (Network.LearnMac(ip.ToString()) ?? "not learned"));
                var method = typeof(Network).GetMethod("PortOpen", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                foreach (var port in new[] { 5555, 3000, 8001, 8002 })
                    Console.WriteLine($"Port {port}: {await (Task<bool>)method.Invoke(null, new object[] { ip.ToString(), port, CancellationToken.None })!}");
            }
            var discovered = await Network.Discover(CancellationToken.None);
            foreach (var tv in discovered) Console.WriteLine($"Discovered {tv.Name}: {tv.Address} ({tv.Platform}), port {tv.Port}");
            return;
        }
        var v6 = TvAddress.Parse("fe80::a3d8:4e29:2c7b:ec49", 12);
        Check(v6.ScopeId == 12 && TvAddress.Valid(v6.ToString()), "link-local adapter and saved address");
        Check(TvAddress.Endpoint(v6.ToString(), 5555) == "[fe80::a3d8:4e29:2c7b:ec49%12]:5555", "IPv6 ADB endpoint");
        Check(TvAddress.Endpoint("192.168.1.50", 5555) == "192.168.1.50:5555", "IPv4 unchanged");
        Check(TvAddress.Valid("2001:db8:1234:5678:90ab:cdef:1234:5678"), "full IPv6 literal");
        Check(!TvAddress.Valid("fe80::1") && !TvAddress.Valid("192.168.001.1") && !TvAddress.Valid("not an address"), "invalid/unscoped addresses rejected");
        var localUri = TvAddress.LocalUri(new Uri("http://[fe80::a3d8:4e29:2c7b:ec49]:8080/description.xml"), v6);
        Check(localUri is not null && System.Net.IPAddress.Parse(localUri.DnsSafeHost).ScopeId == 12, "SSDP URL retains local scope");
        Check(TvAddress.LocalUri(new Uri("http://[fe80::2]/"), v6) is null, "foreign descriptor rejected");
        var packetMac = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var packet = Network.WakePacket(packetMac);
        Check(packet.Length == 102 && packet.Take(6).All(b => b == 255) && Enumerable.Range(0, 16).All(i => packet.AsSpan(6 + i * 6, 6).SequenceEqual(packetMac)), "wake packet exact layout");
        Console.WriteLine("PASS: IPv4/IPv6 addresses, adapter scope, endpoint/URL formatting and wake packet.");
        Check(TvBrands.KeyFor("LG C1") == "lg" && TvBrands.KeyFor("lg electronics 55") == "lg", "brand keyword: LG");
        Check(TvBrands.KeyFor("Basement Skyworth") == "skyworth" && TvBrands.KeyFor("Skyworth 43") == "skyworth", "brand keyword: Skyworth");
        Check(TvBrands.KeyFor("Orca 65") == "orca" && TvBrands.KeyFor("Wansa") == "wansa", "brand keyword: local brands");
        Check(TvBrands.KeyFor("Sony BRAVIA 4K") == "sony" && TvBrands.KeyFor("Hisense U8") == "hisense" &&
            TvBrands.KeyFor("TCL 55C645") == "tcl" && TvBrands.KeyFor("Philips 55PUS") == "philips", "brand keywords: major brands");
        Check(TvBrands.KeyFor("Samsung Tizen TV") == "samsung" && TvBrands.KeyFor("Xiaomi TV A2") == "xiaomi" &&
            TvBrands.KeyFor("Amazon Fire TV Stick") == "amazon" && TvBrands.KeyFor("Roku TV") == "roku", "brand keywords: streaming brands");
        Check(TvBrands.KeyFor("Salon") is null && TvBrands.KeyFor("Lounge screen") is null, "no false brand matches on ordinary words");
        Check(TvBrands.Of(new Tv("Basement Skyworth", "127.0.0.1", 9, Platform.Android)).Key == "skyworth", "saved name resolves brand");
        Check(TvBrands.Of(new Tv("TV", "127.0.0.1", 9, Platform.Android)).Key == "android" &&
            TvBrands.Of(new Tv("TV", "127.0.0.1", 9, Platform.WebOS)).Key == "webos" &&
            TvBrands.Of(new Tv("TV", "127.0.0.1", 9, Platform.Other)).Key == "tv", "unknown brand falls back to TV type");
        Check(TvBrands.Of(new Tv("Orca 65", "127.0.0.1", 9, Platform.Android, Brand: "lg")).Key == "lg", "manual brand overrides name guess");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<Tv>(
            "{\"Name\":\"Old\",\"Address\":\"192.168.1.5\",\"Port\":5555,\"Platform\":0}")!;
        Check(legacy.Brand is null && legacy.Address == "192.168.1.5", "saved TVs without a brand still load");
        foreach (var brand in TvBrands.Selectable.Where(b => b.Art is not null))
        {
            var bounds = TvBrands.Art(brand.Art!).GetBounds();
            Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Width <= 30 && bounds.Height <= 30, "brand mark geometry: " + brand.Key);
        }
        var seen = new HashSet<string>();
        Check(TvBrands.Selectable.All(b => seen.Add(b.Key)) && TvBrands.Selectable.Count >= 18, "brand catalog is unique and extensible");
        Console.WriteLine("PASS: TV brand detection, saved record compatibility and icon geometry.");
        var backupRoot = Path.Combine(AppContext.BaseDirectory, "ipv6-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(backupRoot, "devices.json"), System.Text.Json.JsonSerializer.Serialize(new[] { new Tv("IPv6 TV", v6.ToString(), 5555, Platform.Android) }));
            var snapshot = new RestorePointStore(backupRoot).Capture();
            Check(snapshot.Files.ContainsKey("devices.json"), "IPv6 TV passes backup validation");
        }
        finally { Directory.Delete(backupRoot, true); }
        if (args.Length == 2 && args[0] == "--memory")
        {
            var value = await Network.ReadTvMemory(new Tv("TV", args[1], 5555, Platform.Android), CancellationToken.None);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(value)); return;
        }
        if (args.Length == 2 && args[0] == "--get")
        {
            var tv = new Tv("TV", args[1], 5555, Platform.Android);
            try
            {
                var entries = await Network.ListTvFolder(tv, "/storage/emulated/0/Download", CancellationToken.None);
                var target = entries.First(entry => !entry.IsDirectory);
                var folder = Path.Combine(Path.GetTempPath(), "tv-get-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var count = await Network.DownloadItems(tv, new[] { new TvItem("/storage/emulated/0/Download/" + target.Name, target) },
                    folder, new Progress<FileTransferProgress>(), CancellationToken.None);
                var path = Path.Combine(folder, target.Name);
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                Console.WriteLine($"DOWNLOADED {count} {target.Name} {new FileInfo(path).Length} {hash}");
                Directory.Delete(folder, true);
            }
            catch (Exception ex) { Console.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}"); }
            return;
        }
        if (args.Length == 2 && args[0] == "--list")
        {
            var tv = new Tv("TV", args[1], 5555, Platform.Android);
            try
            {
                foreach (var volume in await Network.TvVolumes(tv, CancellationToken.None)) Console.WriteLine($"VOL {volume.Label} {volume.Path}");
                var entries = await Network.ListTvFolder(tv, "/storage/emulated/0", CancellationToken.None);
                Console.WriteLine($"ENTRIES {entries.Count}");
                foreach (var entry in entries.Take(10)) Console.WriteLine($"{(entry.IsDirectory ? "DIR " : "FILE")} {entry.Name} {entry.Size}");
            }
            catch (Exception ex) { Console.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}"); }
            return;
        }
        Check(Network.ParseTvStorage("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/fuse 10000000 4000000 5500000 42% /storage/emulated\n") == new TvSpace(10240000000, 4096000000, 5632000000), "storage units and reserved blocks");
        Check(Network.ParseTvStorage("Filesystem 1K-blocks Used Available Use% Mounted on\n/very/long/name\n  100 20 80 20% /sdcard") == new TvSpace(102400, 20480, 81920), "wrapped df row");
        Check(Network.ParseTvRam("MemTotal: 2048000 kB\nMemAvailable: 1024000 kB\nMemFree: 1000 kB\n") == new TvSpace(2097152000, 1048576000, 1048576000), "RAM available instead of MemFree");
        Check(Network.ParseTvRam("MemTotal: 1000 kB\nMemFree: 1 kB\n") is null, "missing RAM available is unknown");
        Check(Network.ParseTvRam("MemTotal: 1000 kB\nMemAvailable: 2000 kB\n") is null, "invalid RAM totals rejected");
        foreach (var invalid in new[] { "permission denied", "fs 100 200 0 100% /sdcard", "fs 9223372036854775807 1 1 1% /sdcard" })
        { try { Network.ParseTvStorage(invalid); throw new Exception("invalid storage accepted"); } catch (IOException) { } }
        Console.WriteLine("PASS: storage/RAM units, reserved space, wrapped output and unavailable/invalid reports.");
        if (args.Length == 2 && args[0] == "--live") { await Live(args[1]); return; }
        foreach (var size in new[] { 0, 1, 65536, 131199 })
        {
            var bytes = new byte[size]; new Random(99).NextBytes(bytes);
            using var wire = new Duplex(new byte[] { 79, 75, 65, 89 });
            using var source = new MemoryStream(bytes);
            long last = 0;
            await AdbFileSender.Send(wire, source, "/sdcard/Movies/ملف's test.part", size, n => { Check(n >= last && n <= size, "monotonic progress"); last = n; }, CancellationToken.None);
            var sent = wire.Written.ToArray(); int offset = 0; using var payload = new MemoryStream();
            Check(Frame() == "SEND", "SEND header");
            var pathSize = Length();
            Check(Encoding.UTF8.GetString(sent, offset, pathSize) == "/sdcard/Movies/ملف's test.part,33206", "UTF8 path and file mode"); offset += pathSize;
            while (true)
            {
                var id = Frame(); var length = Length();
                if (id == "DONE") break;
                Check(id == "DATA" && length <= 65536, "bounded chunks");
                payload.Write(sent, offset, length); offset += length;
            }
            Check(offset == sent.Length && payload.ToArray().SequenceEqual(bytes) && last == size, "exact complete payload");
            string Frame() { var value = Encoding.ASCII.GetString(sent, offset, 4); offset += 4; return value; }
            int Length() { var value = BinaryPrimitives.ReadInt32LittleEndian(sent.AsSpan(offset)); offset += 4; return value; }
        }
        using (var wire = new Duplex(Encoding.ASCII.GetBytes("FAIL\x06\0\0\0denied")))
        {
            try { await AdbFileSender.Send(wire, new MemoryStream(), "/sdcard/f", 0, _ => { }, CancellationToken.None); throw new Exception("failure ignored"); }
            catch (IOException e) { Check(e.Message.Contains("denied"), "TV error propagated"); }
        }
        using (var stop = new CancellationTokenSource())
        using (var wire = new Duplex([]))
        {
            try { await AdbFileSender.Send(wire, new MemoryStream(new byte[131072]), "/sdcard/f", 131072, _ => stop.Cancel(), stop.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { Check(!Encoding.ASCII.GetString(wire.Written.ToArray()).Contains("DONE"), "cancel does not finalize"); }
        }
        using (var wire = new Duplex(Encoding.ASCII.GetBytes("OKAY")))
        {
            await AdbFileSender.Service(wire, "host:transport:test", CancellationToken.None);
            Check(Encoding.ASCII.GetString(wire.Written.ToArray()) == "0013host:transport:test", "ADB service framing");
        }
        using (var wire = new Duplex([.. Dent(0x41ED, 0, 1700000000, "Movies"), .. Dent(0x41ED, 0, 1700000000, "."), .. Dent(0x41ED, 0, 1700000000, ".."),
            .. Dent(0x81A4, 1234, 1700000001, "ملف.mp4"), .. Done()]))
        {
            var list = await AdbFileSender.List(wire, "/sdcard", CancellationToken.None);
            Check(list.Count == 2 && list[0] == new TvEntry("Movies", true, 0, DateTimeOffset.FromUnixTimeSeconds(1700000000))
                && list[1].Name == "ملف.mp4" && !list[1].IsDirectory && list[1].Size == 1234, "LIST parses folders, files and UTF8 names");
            var request = wire.Written.ToArray();
            Check(Encoding.ASCII.GetString(request, 0, 4) == "LIST" && BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(4)) == 7
                && Encoding.UTF8.GetString(request, 8, 7) == "/sdcard", "LIST request framing");
        }
        using (var wire = new Duplex(FrameBytes("FAIL", Encoding.UTF8.GetBytes("denied"))))
        {
            try { await AdbFileSender.List(wire, "/sdcard/private", CancellationToken.None); throw new Exception("listing failure ignored"); }
            catch (IOException e) { Check(e.Message.Contains("denied"), "listing error propagated"); }
        }
        using (var wire = new Duplex([.. FrameBytes("DATA", Encoding.UTF8.GetBytes("hello")), .. FrameBytes("DATA", Encoding.UTF8.GetBytes(" world")), .. Done()]))
        using (var file = new MemoryStream())
        {
            long last = 0;
            var received = await AdbFileSender.Receive(wire, "/sdcard/a.txt", file, n => { Check(n > last, "download progress"); last = n; }, CancellationToken.None);
            Check(received == 11 && last == 11 && Encoding.UTF8.GetString(file.ToArray()) == "hello world", "RECV writes exact data");
            var request = wire.Written.ToArray();
            Check(Encoding.ASCII.GetString(request, 0, 4) == "RECV" && BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(4)) == 13
                && Encoding.UTF8.GetString(request, 8, 13) == "/sdcard/a.txt", "RECV request framing");
        }
        using (var wire = new Duplex(FrameBytes("FAIL", Encoding.UTF8.GetBytes("missing"))))
        using (var file = new MemoryStream())
        {
            try { await AdbFileSender.Receive(wire, "/sdcard/x", file, _ => { }, CancellationToken.None); throw new Exception("download failure ignored"); }
            catch (IOException e) { Check(e.Message.Contains("missing"), "download error propagated"); }
        }
        using (var wire = new Duplex(FrameBytes("DATA", new byte[70000])))
        using (var file = new MemoryStream())
        {
            try { await AdbFileSender.Receive(wire, "/sdcard/x", file, _ => { }, CancellationToken.None); throw new Exception("oversized download chunk accepted"); }
            catch (IOException) { }
        }
        Check(Network.StorageFolder(" /sdcard/Movies//TV Monitor/ ") == "/sdcard/Movies/TV Monitor", "TV folder normalization");
        foreach (var bad in new[] { "/data", "/storage", "/sdcard/../oops", "/sdcard/a\\b" })
        { try { Network.StorageFolder(bad); throw new Exception("unsafe TV folder accepted"); } catch (IOException) { } }
        var volumes = new[] { new TvVolume("Internal storage", "/storage/emulated/0"), new TvVolume("USB", "/storage/1A2B-3C4D") };
        Check(Network.ParentTvPath("/storage/emulated/0", volumes) == "/storage/emulated/0" &&
            Network.ParentTvPath("/storage/emulated/0/Movies", volumes) == "/storage/emulated/0" &&
            Network.ParentTvPath("/storage/1A2B-3C4D/Movies/Action", volumes) == "/storage/1A2B-3C4D/Movies", "TV parent folder stops at the volume root");
        Check(Network.TransferFolder("/sdcard/Movies//TV Monitor/") == "/sdcard/Movies/TV Monitor", "folder normalization");
        foreach (var bad in new[] { "/data/local/tmp", "/sdcard/../data", "/sdcard/Movies\nother", "/sdcard/" })
        { try { Network.TransferFolder(bad); throw new Exception("unsafe folder accepted"); } catch (IOException) { } }
        foreach (var bad in new[] { "../file", "a/b", "a\\b", new string('ع', 101) })
        { try { Network.TransferName(bad); throw new Exception("invalid name accepted"); } catch (IOException) { } }
        Check(Network.TransferName("فيلم's $(test).mp4") == "فيلم's $(test).mp4", "ordinary Unicode/special file names preserved");
        Console.WriteLine("PASS: sync payloads (empty/boundary/multichunk), progress, rejection, cancellation, service framing, listing, download, safe paths and UTF8 names.");

        static byte[] FrameBytes(string id, byte[] payload)
        {
            var bytes = new byte[8 + payload.Length];
            Encoding.ASCII.GetBytes(id).CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), payload.Length);
            payload.CopyTo(bytes, 8);
            return bytes;
        }
        static byte[] Dent(uint mode, uint size, uint time, string name)
        {
            var encoded = Encoding.UTF8.GetBytes(name);
            var bytes = new byte[20 + encoded.Length];
            Encoding.ASCII.GetBytes("DENT").CopyTo(bytes, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), mode);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), size);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), time);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)encoded.Length);
            encoded.CopyTo(bytes, 20);
            return bytes;
        }
        static byte[] Done() => Encoding.ASCII.GetBytes("DONE");
    }

    private static void RenderMarks()
    {
        ApplicationConfiguration.Initialize(); FluentTheme.ReadSystem();
        var brands = TvBrands.Selectable
            .Concat(new[] { TvBrands.TypeOf(Platform.Android), TvBrands.TypeOf(Platform.WebOS), TvBrands.Find("tv")! })
            .ToArray();
        const float scale = 3f;
        var cell = new Size((int)(60 * scale), (int)(46 * scale));
        var columns = 8;
        using var sheet = new Bitmap(cell.Width * columns + 24, cell.Height * ((brands.Length + columns - 1) / columns) + 24);
        using (var canvas = Graphics.FromImage(sheet))
        {
            canvas.Clear(Color.FromArgb(243, 243, 243));
            for (var i = 0; i < brands.Length; i++)
            {
                var brand = brands[i];
                var platform = brand.Key switch
                {
                    "lg" or "webos" => Platform.WebOS,
                    "apple" or "roku" or "tv" => Platform.Other,
                    _ => Platform.Android
                };
                var tile = new Rectangle(12 + i % columns * cell.Width, 12 + i / columns * cell.Height, (int)(46 * scale), (int)(32 * scale));
                using var sample = new Bitmap(tile.Width, tile.Height);
                using (var g = Graphics.FromImage(sample))
                {
                    g.Clear(Color.White);
                    TvBrands.Draw(g, new Tv(brand.Name, "127.0.0.1", 9, platform, Brand: brand.Key), new Rectangle(0, 0, tile.Width, tile.Height), scale);
                }
                var ink = 0;
                for (var y = 0; y < sample.Height; y += 2)
                    for (var x = 0; x < sample.Width; x += 2)
                    {
                        var pixel = sample.GetPixel(x, y);
                        if (pixel.GetBrightness() < 0.86f) ink++;
                    }
                var total = (sample.Width / 2) * (sample.Height / 2);
                var ratio = (float)ink / total;
                Check(ratio is > 0.02f and < 0.9f, "brand mark renders ink: " + brand.Key);
                canvas.DrawImage(sample, tile);
                Console.WriteLine($"{brand.Key}: {ratio:P0} ink, {(brand.Art is null ? "monogram" : "vector")}, {platform}");
            }
        }
        var path = Path.Combine(AppContext.BaseDirectory, "brand-marks.png");
        sheet.Save(path);
        Console.WriteLine("Saved " + path);
    }

    private static void RenderUi()
    {
        ApplicationConfiguration.Initialize(); FluentTheme.ReadSystem();
        var language = typeof(AppText).GetProperty("IsArabic", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var arabic in new[] { false, true })
        foreach (var compact in new[] { false, true })
        {
            language.SetValue(null, arabic);
            using var form = new FileManagerForm(new Tv("TV", "127.0.0.1", 9, Platform.Android), autoLoad: false);
            if (compact) form.ClientSize = new Size((int)(900 * form.DeviceDpi / 96f), (int)(620 * form.DeviceDpi / 96f));
            form.ShowInTaskbar = false; form.Show(); Application.DoEvents();
            FluentTheme.Apply(form); form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var layout = (TableLayoutPanel)form.Controls[0];
            foreach (Control control in layout.Controls)
                Check(control.Bottom <= layout.ClientSize.Height && control.Height > 0, "file manager rows fit at compact size");
            var toolbar = layout.Controls.OfType<FlowLayoutPanel>().Single();
            Check(toolbar.Controls.Cast<Control>().All(button => button.Bottom <= toolbar.Height), "file manager toolbar buttons fully visible");
            Check(toolbar.Controls.Count == 11, "file manager toolbar actions");
            var lists = Descendants(form).OfType<ListView>().ToArray();
            Check(lists.Length == 2 && lists.All(list => list.Columns.Count == 3 && list.Width > 200), "PC and TV file lists");
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"files-ui-{(arabic ? "ar" : "en")}-{(compact ? "compact" : "normal")}.png"));
            form.Close();
        }
        foreach (var arabic in new[] { false, true })
        {
            language.SetValue(null, arabic);
            using var memory = new TvMemoryForm(new Tv("TV", "127.0.0.1", 9, Platform.Android));
            memory.ClientSize = new Size((int)(440 * memory.DeviceDpi / 96f), (int)(500 * memory.DeviceDpi / 96f));
            memory.ShowInTaskbar = false; memory.Show(); Application.DoEvents();
            memory.DisplayReading(new(new(8L << 30, 7L << 30, 512L << 20), new(2L << 30, 1536L << 20, 512L << 20), DateTimeOffset.Now));
            memory.PerformLayout();
            using var bitmap = new Bitmap(memory.Width, memory.Height);
            memory.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var layout = (TableLayoutPanel)memory.Controls[0];
            foreach (Control control in layout.Controls)
                Check(control.Bottom <= layout.ClientSize.Height, "memory UI fits compact size");
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"memory-ui-{(arabic ? "ar" : "en")}.png"));
            memory.Close();
        }
        Console.WriteLine("PASS: English/Arabic file manager and memory dialogs rendered without clipped rows/buttons.");
        foreach (var arabic in new[] { false, true })
        {
            language.SetValue(null, arabic);
            using var dialog = new DeviceDialog(new Tv("Wansa", "fe80::a3d8:4e29:2c7b:ec49%12", 5555, Platform.Other, Mac: "02:11:22:33:44:55"));
            dialog.ShowInTaskbar = false; dialog.Show(); Application.DoEvents();
            var grid = (TableLayoutPanel)dialog.Controls[0];
            var ipField = grid.Controls.OfType<TextBox>().Single(c => c.AccessibleName == AppText.T("IP address"));
            var brandBox = grid.Controls.OfType<ComboBox>().Single(c => c.AccessibleName == AppText.T("Brand"));
            Check(Equals(brandBox.Items[0], AppText.T("Auto (detect)")) && brandBox.Items.Count >= 19, "brand list with automatic detection");
            brandBox.SelectedItem = "LG";
            Check(ipField.Text == "fe80::a3d8:4e29:2c7b:ec49%12" && ipField.MaxLength >= 45, "complete scoped address retained");
            Check(TextRenderer.MeasureText("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff%123456", ipField.Font).Width + 12 <= ipField.ClientSize.Width, "full IPv6 fits visually");
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"address-ui-{(arabic ? "ar" : "en")}.png"));
            ((Button)dialog.AcceptButton!).PerformClick();
            Check(dialog.Value?.Address == "fe80::a3d8:4e29:2c7b:ec49%12" && dialog.Value.Mac == "02:11:22:33:44:55" &&
                dialog.Value.Brand == "lg", "save retains IPv6, MAC and chosen brand separately");
            dialog.Close();
        }
        Console.WriteLine("PASS: full IPv6 fits and saves in English/Arabic device dialogs.");
        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
    }

    static async Task Live(string address)
    {
        var tv = new Tv("Transfer test TV", address, 5555, Platform.Android);
        var root = Path.Combine(AppContext.BaseDirectory, "transfer-test"); Directory.CreateDirectory(root);
        var text = Path.Combine(root, "اختبار نقل's file.txt");
        await File.WriteAllTextAsync(text, "TV Monitor transfer test — اختبار\n" + Guid.NewGuid());
        var wave = Path.Combine(root, "Transfer test.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(16036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(16000); writer.Write(new byte[16000]);
        }
        var folder = "/sdcard/Download/TV Monitor Test-" + Guid.NewGuid().ToString("N");
        var progress = new Inline<FileTransferProgress>(_ => { });
        var first = await Network.UploadFiles(tv, new[] { text, wave }, folder, progress, CancellationToken.None);
        var second = await Network.UploadFiles(tv, new[] { text }, folder, progress, CancellationToken.None);
        Check(first[0].Path != second[0].Path && second[0].Path.Contains("(2)"), "duplicate retains original");
        foreach (var result in first.Concat(second))
        {
            var original = result.Name.EndsWith(".wav") ? wave : text;
            var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original)));
            var reply = await Network.RunAdb(CancellationToken.None, "-s", Network.Serial(tv), "shell", "sha256sum " + Quote(result.Path));
            Check(reply.StartsWith(expected, StringComparison.OrdinalIgnoreCase), "live SHA256 match");
        }
        var large = Path.Combine(root, "cancel-test.bin");
        using (var file = File.Create(large)) file.SetLength(16 * 1024 * 1024);
        using var cancel = new CancellationTokenSource();
        try
        {
            await Network.UploadFiles(tv, new[] { large }, folder, new Inline<FileTransferProgress>(p => { if (p.Sent >= 65536) cancel.Cancel(); }), cancel.Token);
            throw new Exception("live cancellation ignored");
        }
        catch (OperationCanceledException) { }
        var leftovers = await Network.RunAdb(CancellationToken.None, "-s", Network.Serial(tv), "shell", "ls -a " + Quote(folder));
        Check(!leftovers.Contains(".part") && !leftovers.Contains("cancel-test.bin"), "no partial cancelled upload");
        Console.WriteLine("PASS: TV text and WAV uploads, remote SHA256 equality, duplicate preservation and cancelled-file cleanup. No media was played.");
        Console.WriteLine("Test destination: " + folder);
        // Remove only the uniquely named files/folder created by this check.
        var cleanup = await Network.RunAdb(CancellationToken.None, "-s", Network.Serial(tv), "shell", "rm -f " + string.Join(' ', first.Concat(second).Select(f => Quote(f.Path))) + " && rmdir " + Quote(folder));
        Console.WriteLine("Removed generated test files. " + cleanup.Trim());
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    }
}

sealed class Inline<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
sealed class Duplex(byte[] response) : Stream
{
    private readonly MemoryStream input = new(response);
    internal MemoryStream Written { get; } = new();
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return input.ReadAsync(buffer, ct); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Written.WriteAsync(buffer, ct); }
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { input.Dispose(); Written.Dispose(); } base.Dispose(disposing); }
}
