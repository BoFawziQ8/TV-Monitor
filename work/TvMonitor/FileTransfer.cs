using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

sealed record FileTransferProgress(int Index, int Count, string Name, long Sent, long Size, long Completed, long Total, bool Saved = false);
sealed record UploadedTvFile(string Name, string Path, long Size);
sealed record TvEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);
sealed record TvItem(string Path, TvEntry Entry);
sealed record TvVolume(string Label, string Path)
{
    public override string ToString() => Label;
}

// Uses the existing, locally authenticated ADB server. No TV helper or cloud upload.
static class AdbFileSender
{
    internal static async Task Send(Stream wire, Stream file, string remote, long size, Action<long> progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Write(ReadOnlyMemory<byte> bytes)
        { timeout.CancelAfter(TimeSpan.FromSeconds(45)); await wire.WriteAsync(bytes, timeout.Token); }
        async Task Read(Memory<byte> bytes)
        { timeout.CancelAfter(TimeSpan.FromSeconds(45)); await wire.ReadExactlyAsync(bytes, timeout.Token); }
        async Task Frame(string id, uint length)
        {
            var header = new byte[8]; Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), length); await Write(header);
        }
        var path = Encoding.UTF8.GetBytes(remote + ",33206"); // regular file, 0666; TV applies storage permissions
        await Frame("SEND", (uint)path.Length); await Write(path);
        long sent = 0;
        var buffer = new byte[65536];
        while (sent < size)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - sent)), ct);
            if (read == 0) throw new IOException(T("The source file changed during transfer."));
            await Frame("DATA", (uint)read); await Write(buffer.AsMemory(0, read));
            sent += read; progress(sent);
        }
        if (await file.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw new IOException(T("The source file changed during transfer."));
        await Frame("DONE", (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var response = new byte[4]; await Read(response);
        var status = Encoding.ASCII.GetString(response);
        if (status == "OKAY") return;
        if (status != "FAIL") throw new IOException(T("Invalid file-transfer response from the TV."));
        var lengthBytes = new byte[4]; await Read(lengthBytes);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length > 32768) throw new IOException(T("Invalid file-transfer response from the TV."));
        var error = new byte[length]; await Read(error);
        throw new IOException(T("The TV rejected the file: ") + Encoding.UTF8.GetString(error));
    }

    internal static async Task Service(Stream stream, string service, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var request = Encoding.UTF8.GetBytes(service);
        if (request.Length > 65535) throw new IOException("ADB request too long.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.Length.ToString("X4", CultureInfo.InvariantCulture)), timeout.Token);
        await stream.WriteAsync(request, timeout.Token);
        var response = new byte[4]; await stream.ReadExactlyAsync(response, timeout.Token);
        if (Encoding.ASCII.GetString(response) == "OKAY") return;
        if (Encoding.ASCII.GetString(response) != "FAIL") throw new IOException(T("Invalid file-transfer response from the TV."));
        await stream.ReadExactlyAsync(response, timeout.Token);
        if (!int.TryParse(Encoding.ASCII.GetString(response), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length) || length > 32768)
            throw new IOException(T("Invalid file-transfer response from the TV."));
        var error = new byte[length]; await stream.ReadExactlyAsync(error, timeout.Token);
        throw new IOException(T("The TV rejected the file: ") + Encoding.UTF8.GetString(error));
    }

    internal static async Task<List<TvEntry>> List(Stream wire, string remote, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Write(ReadOnlyMemory<byte> bytes)
        { timeout.CancelAfter(TimeSpan.FromSeconds(45)); await wire.WriteAsync(bytes, timeout.Token); }
        async Task Read(Memory<byte> bytes)
        { timeout.CancelAfter(TimeSpan.FromSeconds(45)); await wire.ReadExactlyAsync(bytes, timeout.Token); }
        async Task Frame(string id, uint length)
        {
            var header = new byte[8]; Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), length); await Write(header);
        }
        async Task<string> Failure()
        {
            var lengthBytes = new byte[4]; await Read(lengthBytes);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (length > 32768) throw new IOException(T("Invalid folder listing from the TV."));
            var error = new byte[length]; await Read(error);
            return Encoding.UTF8.GetString(error);
        }
        var path = Encoding.UTF8.GetBytes(remote);
        await Frame("LIST", (uint)path.Length); await Write(path);
        var entries = new List<TvEntry>();
        var stat = new byte[16];
        while (true)
        {
            var idBytes = new byte[4]; await Read(idBytes);
            var id = Encoding.ASCII.GetString(idBytes);
            if (id == "DONE") return entries;
            if (id == "FAIL")
                throw new IOException(T("The TV could not list that folder: ") + await Failure());
            if (id != "DENT") throw new IOException(T("Invalid folder listing from the TV."));
            await Read(stat);
            var mode = BinaryPrimitives.ReadUInt32LittleEndian(stat);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(4));
            var time = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(8));
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(12));
            if (nameLength > 1024) throw new IOException(T("Invalid folder listing from the TV."));
            var nameBytes = new byte[(int)nameLength]; await Read(nameBytes);
            var name = Encoding.UTF8.GetString(nameBytes);
            if (name.Length == 0 || name is "." or "..") continue;
            entries.Add(new(name, (mode & 0xF000) == 0x4000, size, DateTimeOffset.FromUnixTimeSeconds(time)));
        }
    }

    internal static async Task<long> Receive(Stream wire, string remote, Stream file, Action<long> progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Write(ReadOnlyMemory<byte> bytes)
        { timeout.CancelAfter(TimeSpan.FromSeconds(45)); await wire.WriteAsync(bytes, timeout.Token); }
        async Task Read(Memory<byte> bytes)
        { timeout.CancelAfter(TimeSpan.FromSeconds(45)); await wire.ReadExactlyAsync(bytes, timeout.Token); }
        async Task Frame(string id, uint length)
        {
            var header = new byte[8]; Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), length); await Write(header);
        }
        var path = Encoding.UTF8.GetBytes(remote);
        await Frame("RECV", (uint)path.Length); await Write(path);
        long received = 0;
        while (true)
        {
            var idBytes = new byte[4]; await Read(idBytes);
            var id = Encoding.ASCII.GetString(idBytes);
            if (id == "DONE") return received;
            if (id == "FAIL")
            {
                var lengthBytes = new byte[4]; await Read(lengthBytes);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
                if (length > 32768) throw new IOException(T("Invalid file data from the TV."));
                var error = new byte[length]; await Read(error);
                throw new IOException(T("The TV rejected the file: ") + Encoding.UTF8.GetString(error));
            }
            if (id != "DATA") throw new IOException(T("Invalid file data from the TV."));
            var dataLength = new byte[4]; await Read(dataLength);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(dataLength);
            if (count > 65536) throw new IOException(T("Invalid file data from the TV."));
            var chunk = new byte[count]; await Read(chunk);
            await file.WriteAsync(chunk, ct);
            received += count;
            progress(received);
        }
    }
}

static partial class Network
{
    internal static string TransferFolder(string input)
    {
        var parts = input.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!input.Trim().StartsWith("/sdcard/", StringComparison.Ordinal) || parts.Length < 2 ||
            parts.Any(p => p is "." or ".." || p.Any(c => char.IsControl(c) || c == '\\')) || Encoding.UTF8.GetByteCount(input) > 800)
            throw new IOException(T("Choose a folder inside /sdcard/, such as /sdcard/Movies/TV Monitor."));
        return "/" + string.Join('/', parts);
    }

    internal static string TransferName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Any(c => char.IsControl(c) || c is '/' or '\\') || Encoding.UTF8.GetByteCount(name) > 200)
            throw new IOException(T("A file name is too long or invalid for TV storage."));
        return name;
    }

    internal static async Task<List<UploadedTvFile>> UploadFiles(Tv tv, IReadOnlyList<string> paths, string folder,
        IProgress<FileTransferProgress> progress, CancellationToken ct) =>
        await UploadItems(tv, paths, TransferFolder(folder), progress, ct);

    internal static async Task<List<UploadedTvFile>> UploadItems(Tv tv, IReadOnlyList<string> paths, string folder,
        IProgress<FileTransferProgress> progress, CancellationToken ct)
    {
        folder = StorageFolder(folder);
        if (paths.Count == 0) throw new IOException(T("Choose files first."));
        var files = new List<(FileInfo File, string RelativeFolder)>();
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var top = Path.GetFileName(root);
                TransferName(top);
                folders.Add(top);
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    var relative = TransferRelative(root, directory);
                    foreach (var segment in relative.Split('/')) TransferName(segment);
                    folders.Add(relative);
                }
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    TransferName(Path.GetFileName(file));
                    var parent = Path.GetDirectoryName(TransferRelative(root, file))?.Replace('\\', '/') ?? "";
                    files.Add((new FileInfo(file), parent == "." ? "" : parent));
                }
            }
            else
            {
                var file = new FileInfo(path);
                TransferName(file.Name);
                if (!file.Exists) throw new IOException(T("A selected file no longer exists: ") + file.Name);
                files.Add((file, ""));
            }
        }
        if (files.Count == 0 && folders.Count == 0) throw new IOException(T("Choose files first."));
        long total = 0; foreach (var item in files) total = checked(total + item.File.Length);
        await EnsureAdb(tv, ct);
        await TvCommand(tv, $"mkdir -p {ShellQuote(folder)} && test -d {ShellQuote(folder)} && test -w {ShellQuote(folder)}", ct);
        foreach (var directory in folders)
            await TvCommand(tv, $"mkdir -p {ShellQuote(folder + "/" + directory)}", ct);
        var storage = ParseTvStorage(await TvCommand(tv, $"df -Pk {ShellQuote(folder)}", ct));
        if (total > storage.Available || storage.Available - total < 1024 * 1024)
            throw new IOException(T("The TV does not have enough free storage for these files."));
        var uploaded = new List<UploadedTvFile>();
        long completed = 0;
        for (int index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (file, relativeFolder) = files[index];
            var name = file.Name;
            var label = relativeFolder.Length == 0 ? name : relativeFolder + "/" + name;
            var target = relativeFolder.Length == 0 ? folder : folder + "/" + relativeFolder;
            var destination = await UniqueTvPath(tv, target, name, false, ct);
            var temporary = target + "/.tvmonitor-" + Guid.NewGuid().ToString("N") + ".part";
            bool committed = false;
            try
            {
                await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                var size = source.Length;
                if (size != file.Length) throw new IOException(T("The source file changed during transfer."));
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", int.Parse(AdbPort), ct);
                var stream = client.GetStream();
                await AdbFileSender.Service(stream, "host:transport:" + Serial(tv), ct);
                await AdbFileSender.Service(stream, "sync:", ct);
                progress.Report(new(index + 1, files.Count, label, 0, size, completed, total));
                long lastReport = Environment.TickCount64;
                await AdbFileSender.Send(stream, source, temporary, size, sent =>
                {
                    if (sent != size && Environment.TickCount64 - lastReport < 100) return;
                    lastReport = Environment.TickCount64;
                    progress.Report(new(index + 1, files.Count, label, sent, size, completed, total));
                }, ct);
                var measured = await TvCommand(tv, $"stat -c %s {ShellQuote(temporary)}", ct);
                if (!long.TryParse(measured.Trim(), out var received) || received != size)
                    throw new IOException(T("The TV file size could not be verified."));
                // No clobber protects existing files even if a new collision appears mid-transfer.
                await TvCommand(tv, $"mv -n {ShellQuote(temporary)} {ShellQuote(destination)} && test ! -e {ShellQuote(temporary)} && test -f {ShellQuote(destination)}", ct);
                committed = true; completed += size;
                uploaded.Add(new(name, destination, size));
                progress.Report(new(index + 1, files.Count, destination, size, size, completed - size, total, true));
                await TryScanTvFile(tv, destination);
            }
            finally
            {
                if (!committed)
                {
                    try { await TvCommand(tv, "rm -f " + ShellQuote(temporary), CancellationToken.None); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
        return uploaded;
    }

    internal static async Task<int> DownloadItems(Tv tv, IReadOnlyList<TvItem> items, string localFolder,
        IProgress<FileTransferProgress> progress, CancellationToken ct)
    {
        if (items.Count == 0) throw new IOException(T("Nothing is selected on the TV."));
        var destination = Path.GetFullPath(localFolder);
        if (!Directory.Exists(destination)) throw new IOException(T("This PC folder is not available."));
        using var client = await StartSync(tv, ct);
        var wire = client.GetStream();
        var plan = new List<(TvItem Item, string Relative)>();
        foreach (var item in items) await CollectTvTree(item, item.Entry.Name, plan, wire, ct);
        var filePlan = plan.Where(entry => !entry.Item.Entry.IsDirectory).ToArray();
        long total = 0; foreach (var entry in filePlan) total = checked(total + entry.Item.Entry.Size);
        long completed = 0; var index = 0;
        foreach (var (item, relative) in plan)
        {
            ct.ThrowIfCancellationRequested();
            var local = LocalPath(destination, relative);
            if (item.Entry.IsDirectory) { Directory.CreateDirectory(local); continue; }
            index++;
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            local = UniqueLocalPath(local);
            var temporary = local + ".part";
            bool saved = false;
            try
            {
                long received;
                await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    progress.Report(new(index, filePlan.Length, relative, 0, item.Entry.Size, completed, total));
                    long lastReport = Environment.TickCount64;
                    received = await AdbFileSender.Receive(wire, item.Path, file, sent =>
                    {
                        if (sent != item.Entry.Size && Environment.TickCount64 - lastReport < 100) return;
                        lastReport = Environment.TickCount64;
                        progress.Report(new(index, filePlan.Length, relative, sent, item.Entry.Size, completed, total));
                    }, ct);
                }
                if (received != item.Entry.Size) throw new IOException(T("The TV file size could not be verified."));
                File.Move(temporary, local, false);
                saved = true;
                completed += item.Entry.Size;
                progress.Report(new(index, filePlan.Length, relative, item.Entry.Size, item.Entry.Size, completed - item.Entry.Size, total, true));
            }
            finally
            {
                if (!saved) TryDeleteFile(temporary);
            }
        }
        return index;
    }

    private static async Task CollectTvTree(TvItem item, string relative, List<(TvItem Item, string Relative)> plan, Stream wire, CancellationToken ct)
    {
        plan.Add((item, relative));
        if (!item.Entry.IsDirectory) return;
        foreach (var entry in await ListFolder(wire, item.Path, ct))
            await CollectTvTree(new TvItem(item.Path + "/" + entry.Name, entry), relative + "/" + entry.Name, plan, wire, ct);
    }

    internal static async Task<List<TvVolume>> TvVolumes(Tv tv, CancellationToken ct)
    {
        await EnsureAdb(tv, ct);
        var volumes = new List<TvVolume> { new(T("Internal storage"), "/storage/emulated/0") };
        string listing;
        try { listing = await TvCommand(tv, "ls -1 /storage", ct); }
        catch (IOException) { return volumes; }
        foreach (var name in Lines(listing).Distinct(StringComparer.Ordinal))
        {
            if (name is "emulated" or "self" or "sdcard" or "encrypt" or "knox" || name.StartsWith('.') ||
                name.Any(c => char.IsControl(c) || c is '/' or '\\' or ' ')) continue;
            volumes.Add(new(name, "/storage/" + name));
        }
        return volumes;
    }

    internal static string ParentTvPath(string path, IReadOnlyList<TvVolume> volumes)
    {
        var root = volumes.Select(volume => volume.Path)
            .Where(candidate => path == candidate || path.StartsWith(candidate + "/", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length).FirstOrDefault();
        if (root is null) return path;
        var slash = path.LastIndexOf('/');
        if (slash <= 0) return root;
        var parent = path[..slash];
        return parent.Length < root.Length ? root : parent;
    }

    internal static async Task<List<TvEntry>> ListTvFolder(Tv tv, string path, CancellationToken ct)
    {
        path = StorageFolder(path);
        using var client = await StartSync(tv, ct);
        return await ListFolder(client.GetStream(), path, ct);
    }

    private static Task<List<TvEntry>> ListFolder(Stream wire, string path, CancellationToken ct) => AdbFileSender.List(wire, path, ct);

    internal static async Task<string> TvMakeFolder(Tv tv, string folder, string name, CancellationToken ct)
    {
        folder = StorageFolder(folder);
        name = TransferName(name.Trim());
        var path = folder + "/" + name;
        await EnsureAdb(tv, ct);
        if (await TvPathExists(tv, path, ct)) throw new IOException(T("That name is already used in this folder."));
        await TvCommand(tv, $"mkdir {ShellQuote(path)} && test -d {ShellQuote(path)}", ct);
        return path;
    }

    internal static async Task<string> TvRenameItem(Tv tv, string path, string newName, CancellationToken ct)
    {
        path = StorageFolder(path);
        newName = TransferName(newName.Trim());
        var slash = path.LastIndexOf('/');
        if (slash <= 0) throw new IOException(T("Choose a folder inside the TV storage."));
        var destination = path[..slash] + "/" + newName;
        if (destination == path) return path;
        await EnsureAdb(tv, ct);
        if (await TvPathExists(tv, destination, ct)) throw new IOException(T("That name is already used in this folder."));
        await TvCommand(tv, $"mv -n {ShellQuote(path)} {ShellQuote(destination)} && test ! -e {ShellQuote(path)} && test -e {ShellQuote(destination)}", ct);
        return destination;
    }

    internal static async Task TvDeleteItems(Tv tv, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) throw new IOException(T("Nothing is selected on the TV."));
        foreach (var path in paths)
        {
            StorageFolder(path);
            if (path is "/storage/emulated/0" or "/sdcard" || Regex.IsMatch(path, @"^/storage/[^/]+$", RegexOptions.CultureInvariant))
                throw new IOException(T("The TV storage root cannot be deleted."));
        }
        await EnsureAdb(tv, ct);
        await TvCommand(tv, "rm -rf " + string.Join(' ', paths.Select(ShellQuote)), ct);
    }

    internal static async Task<int> TvPaste(Tv tv, IReadOnlyList<TvItem> items, string folder, bool move, CancellationToken ct)
    {
        folder = StorageFolder(folder);
        if (items.Count == 0) throw new IOException(T("Nothing is selected on the TV."));
        foreach (var item in items) StorageFolder(item.Path);
        await EnsureAdb(tv, ct);
        var pasted = 0;
        foreach (var item in items)
        {
            var slash = item.Path.LastIndexOf('/');
            var parent = item.Path[..slash];
            if (move && parent == folder) continue;
            if (item.Entry.IsDirectory && folder.StartsWith(item.Path + "/", StringComparison.Ordinal))
                throw new IOException(T("A folder cannot be copied into itself."));
            if (!await TvPathExists(tv, item.Path, ct))
                throw new IOException(T("A selected TV item no longer exists: ") + item.Entry.Name);
            var destination = await UniqueTvPath(tv, folder, item.Entry.Name, item.Entry.IsDirectory, ct);
            await TvCommand(tv, move
                ? $"mv -n {ShellQuote(item.Path)} {ShellQuote(destination)} && test -e {ShellQuote(destination)} && test ! -e {ShellQuote(item.Path)}"
                : $"cp -R {ShellQuote(item.Path)} {ShellQuote(destination)} && test -e {ShellQuote(destination)}", ct);
            pasted++;
        }
        return pasted;
    }

    internal static string StorageFolder(string input)
    {
        var trimmed = input.Trim();
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] is not ("sdcard" or "storage") ||
            parts.Any(part => part is "." or ".." || part.Any(c => char.IsControl(c) || c == '\\')) ||
            Encoding.UTF8.GetByteCount(trimmed) > 800)
            throw new IOException(T("Choose a folder inside the TV storage."));
        var path = "/" + string.Join('/', parts);
        if (path == "/storage") throw new IOException(T("Choose a folder inside the TV storage."));
        return path;
    }

    private static async Task<string> UniqueTvPath(Tv tv, string folder, string name, bool directory, CancellationToken ct)
    {
        var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = directory ? "" : Path.GetExtension(name);
        for (var attempt = 1; attempt <= 9999; attempt++)
        {
            var candidate = folder + "/" + (attempt == 1 ? name : stem + $" ({attempt})" + extension);
            if (!await TvPathExists(tv, candidate, ct)) return candidate;
        }
        throw new IOException(T("No unused destination file name could be found."));
    }

    private static async Task<bool> TvPathExists(Tv tv, string path, CancellationToken ct) =>
        (await TvCommand(tv, $"if test -e {ShellQuote(path)} || test -L {ShellQuote(path)}; then echo YES; else echo NO; fi", ct)).Trim() == "YES";

    private static async Task EnsureAdb(Tv tv, CancellationToken ct)
    {
        var result = await RunAdb(ct, "connect", Serial(tv));
        if (result.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("authenticate", StringComparison.OrdinalIgnoreCase))
            throw new IOException(T("Approve the USB debugging prompt on the TV, then refresh."));
        if (result.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("No route to host", StringComparison.OrdinalIgnoreCase))
            throw new IOException(T("ADB did not answer. The TV may be asleep, disconnected, or using a different IP."));
    }

    private static async Task<TcpClient> StartSync(Tv tv, CancellationToken ct)
    {
        await EnsureAdb(tv, ct);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", int.Parse(AdbPort), ct);
            var stream = client.GetStream();
            await AdbFileSender.Service(stream, "host:transport:" + Serial(tv), ct);
            await AdbFileSender.Service(stream, "sync:", ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task TryScanTvFile(Tv tv, string path)
    {
        try
        {
            var output = await TvCommand(tv, "am get-current-user", CancellationToken.None);
            if (!int.TryParse(output.Trim(), out var user)) return;
            var uri = new Uri("file://" + path).AbsoluteUri;
            await TvCommand(tv, $"am broadcast --user {user} -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {ShellQuote(uri)}", CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or SocketException) { }
    }

    private static string TransferRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string LocalPath(string root, string relative)
    {
        var path = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            path = Path.Combine(path, SafeLocalName(segment));
        return path;
    }

    private static string SafeLocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (text.Length == 0) text = "file";
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];
        return reserved.Contains(text, StringComparer.OrdinalIgnoreCase) ? "_" + text : text;
    }

    private static string UniqueLocalPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var attempt = 2; attempt <= 9999; attempt++)
        {
            var candidate = Path.Combine(directory, stem + $" ({attempt})" + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException(T("No unused destination file name could be found."));
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
