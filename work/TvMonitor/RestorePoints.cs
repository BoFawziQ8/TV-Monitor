using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed record SavedPoint(int Format, string Kind, Guid Id, DateTimeOffset CreatedUtc,
    string Version, string Name, string Sha256, string Payload);
sealed record AppSettingsSnapshot(Dictionary<string, string> Files);

sealed class RestorePointStore
{
    internal static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor");
    internal static RestorePointStore Default { get; set; } = new(DefaultRoot);
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly Regex SetupFile = new(@"^KLightTV/[A-F0-9]{64}\.json$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal string Root { get; }
    internal string Folder => Path.Combine(Root, "RestorePoints");
    private string Journal => Path.Combine(Root, "pending-restore.tvmrestore");

    internal RestorePointStore(string root) => Root = Path.GetFullPath(root);
    internal static bool Managed(string path) => path is "devices.json" or "language.txt" or "settings.json" || SetupFile.IsMatch(path);

    internal AppSettingsSnapshot Capture(IReadOnlyList<Tv>? current = null, string? language = null, bool bundledHistory = true)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in ManagedFiles()) files.Add(path, Convert.ToBase64String(File.ReadAllBytes(Resolve(path))));
        if (current is not null) files["devices.json"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(current, Json));
        if (language is not null) files["language.txt"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(language));
        var originals = Path.Combine(AppContext.BaseDirectory, "KLightTV", "PreviousSetup.json");
        if (bundledHistory && Root.Equals(DefaultRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(originals))
        {
            var history = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(originals));
            if (history is not null)
                foreach (var (identity, setup) in history)
                {
                    var path = "KLightTV/" + identity + ".json";
                    if (Managed(path) && !files.ContainsKey(path))
                        files.Add(path, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(setup)));
                }
        }
        var snapshot = new AppSettingsSnapshot(files);
        ValidateApp(snapshot);
        return snapshot;
    }

    internal SavedPoint Save<TValue>(string kind, string name, TValue data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        if (bytes.Length > MaximumBytes / 2) throw new IOException(T("The restore point is too large."));
        var point = new SavedPoint(1, kind, Guid.NewGuid(), DateTimeOffset.UtcNow,
            typeof(RestorePointStore).Assembly.GetName().Version?.ToString() ?? "", name.Trim(),
            Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToBase64String(bytes));
        Validate(point);
        Directory.CreateDirectory(Folder);
        AtomicWrite(PointPath(point), JsonSerializer.SerializeToUtf8Bytes(point, Json));
        return point;
    }

    internal SavedPoint[] List(out int unreadable)
    {
        unreadable = 0;
        if (!Directory.Exists(Folder)) return [];
        var points = new List<SavedPoint>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.tvmrestore"))
            try { points.Add(Read(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            { unreadable++; }
        return points.OrderByDescending(p => p.CreatedUtc).ToArray();
    }

    internal SavedPoint Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumBytes) throw new IOException(T("The restore point is too large."));
        var point = JsonSerializer.Deserialize<SavedPoint>(File.ReadAllBytes(path), Json)
            ?? throw new IOException(T("Invalid restore point."));
        Validate(point);
        return point;
    }

    internal SavedPoint Import(string path)
    {
        var point = Read(path);
        // Import gets a fresh identifier so it cannot replace an existing recovery point.
        point = point with { Id = Guid.NewGuid() };
        Directory.CreateDirectory(Folder);
        AtomicWrite(PointPath(point), JsonSerializer.SerializeToUtf8Bytes(point, Json));
        return point;
    }

    internal void Export(SavedPoint point, string destination)
    {
        Validate(point);
        var target = Path.GetFullPath(destination);
        if (target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException(T("Choose an export location outside the app settings folder."));
        AtomicWrite(target, JsonSerializer.SerializeToUtf8Bytes(point, Json));
    }

    internal static TValue Data<TValue>(SavedPoint point) => JsonSerializer.Deserialize<TValue>(Convert.FromBase64String(point.Payload), Json)
        ?? throw new IOException(T("Invalid restore point."));

    internal static void Validate(SavedPoint point)
    {
        if (point.Format != 1 || point.Kind is not ("app" or "android-tv") || point.Id == Guid.Empty ||
            point.Name is null || point.Name.Length is < 1 or > 160 || string.IsNullOrEmpty(point.Payload) ||
            point.Payload.Length > MaximumBytes || point.CreatedUtc == default)
            throw new IOException(T("Invalid or unsupported restore point."));
        var payload = Convert.FromBase64String(point.Payload);
        if (payload.Length > MaximumBytes / 2 || Convert.ToHexString(SHA256.HashData(payload)) != point.Sha256)
            throw new IOException(T("The restore point failed its integrity check."));
        if (point.Kind == "app") ValidateApp(Data<AppSettingsSnapshot>(point));
        else TvRestoreSettings.Validate(Data<TvSettingsSnapshot>(point));
    }

    internal static void ValidateApp(AppSettingsSnapshot snapshot)
    {
        if (snapshot.Files is null || snapshot.Files.Count > 200) throw new IOException(T("Invalid app settings backup."));
        int total = 0;
        foreach (var (path, encoded) in snapshot.Files)
        {
            if (!Managed(path) || encoded is null) throw new IOException(T("The backup contains an unsupported settings file."));
            var bytes = Convert.FromBase64String(encoded);
            total += bytes.Length;
            if (bytes.Length > 1024 * 1024 || total > MaximumBytes / 2) throw new IOException(T("The restore point is too large."));
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            if (path == "devices.json")
            {
                var devices = JsonSerializer.Deserialize<List<Tv>>(text);
                if (devices is null || devices.Count > 1000 || devices.Any(tv => tv is null ||
                    string.IsNullOrWhiteSpace(tv.Name) || tv.Name.Length > 200 ||
                    !TvAddress.Valid(tv.Address) ||
                    tv.Port is < 1 or > 65535 || !Enum.IsDefined(tv.Platform) || tv.Key?.Length > 8192))
                    throw new IOException(T("The backup contains invalid TV connection settings."));
            }
            else if (path == "language.txt")
            {
                if (text.Trim() is not ("en" or "ar")) throw new IOException(T("The backup contains an unsupported language."));
            }
            else if (path == "settings.json")
            {
                SettingsStore.Validate(text);
            }
            else
            {
                using var json = JsonDocument.Parse(text);
                foreach (var key in new[] { "Home", "Screensaver", "DefaultScreensaver", "Enabled" })
                    if (!json.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
                        value.GetString()!.Length > 4096) throw new IOException(T("Invalid saved TV setup."));
            }
        }
    }

    internal SavedPoint RestoreApp(SavedPoint point, AppSettingsSnapshot current)
    {
        Validate(point);
        if (point.Kind != "app") throw new IOException(T("Choose an app restore point."));
        var target = Data<AppSettingsSnapshot>(point);
        var safety = Save("app", "Before app restore", current);
        AtomicWrite(Journal, JsonSerializer.SerializeToUtf8Bytes(safety, Json));
        try
        {
            ApplyFiles(target);
            File.Delete(Journal);
        }
        catch (Exception applyError)
        {
            // If a file is locked, leave the journal for recovery at the next startup.
            bool recovered = false;
            try { ApplyFiles(current); File.Delete(Journal); recovered = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw new IOException(recovered
                ? T("App restore failed. The previous settings were restored.")
                : T("App restore was interrupted. An undo point and recovery journal were saved. Reopen TV Monitor after the file becomes available to recover the previous settings."), applyError);
        }
        return safety;
    }

    internal bool RecoverPending()
    {
        if (!File.Exists(Journal)) return false;
        var point = Read(Journal);
        if (point.Kind != "app") throw new IOException(T("Invalid app recovery journal."));
        ApplyFiles(Data<AppSettingsSnapshot>(point));
        File.Delete(Journal);
        return true;
    }

    internal void BeforeConfigChange(string file, byte[] replacement)
    {
        if (File.Exists(Journal)) throw new IOException(T("Finish recovering the interrupted app restore before changing settings."));
        if (!Managed(file)) throw new ArgumentException(nameof(file));
        var path = Resolve(file);
        if (!File.Exists(path) || File.ReadAllBytes(path).AsSpan().SequenceEqual(replacement)) return;
        try { Save("app", "Before settings change", Capture()); }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new IOException(T("Current settings could not be backed up. Restore a valid app point before changing them."), ex); }
    }

    private void ApplyFiles(AppSettingsSnapshot snapshot)
    {
        ValidateApp(snapshot);
        foreach (var (path, value) in snapshot.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            AtomicWrite(Resolve(path), Convert.FromBase64String(value));
        foreach (var path in ManagedFiles().Where(path => !snapshot.Files.ContainsKey(path)).ToArray())
            File.Delete(Resolve(path));
        var actual = Capture(bundledHistory: false);
        if (actual.Files.Count != snapshot.Files.Count || snapshot.Files.Any(f => actual.Files.GetValueOrDefault(f.Key) != f.Value))
            throw new IOException(T("Restored settings did not pass verification."));
    }

    private IEnumerable<string> ManagedFiles()
    {
        foreach (var path in new[] { "devices.json", "language.txt", "settings.json" }) if (File.Exists(Resolve(path))) yield return path;
        var setup = Path.Combine(Root, "KLightTV");
        if (Directory.Exists(setup))
            foreach (var path in Directory.EnumerateFiles(setup, "*.json"))
            {
                var relative = "KLightTV/" + Path.GetFileName(path);
                if (Managed(relative)) yield return relative;
            }
    }

    private string Resolve(string relative)
    {
        if (!Managed(relative)) throw new IOException(T("Unsupported settings path."));
        var path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException(T("Unsupported settings path."));
        // Never follow a linked settings directory or file into another location.
        for (var current = path; current.Length >= Root.Length; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(T("Linked settings paths cannot be restored."));
            if (current == Root) break;
        }
        return path;
    }

    private string PointPath(SavedPoint point) => Path.Combine(Folder, point.Id.ToString("N") + ".tvmrestore");
    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
