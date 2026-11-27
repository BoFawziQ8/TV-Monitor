using System.Text;
using System.Text.Json;

var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var store = new RestorePointStore(root);
var tv = new Tv("Test TV", "192.168.1.50", 5555, Platform.Android, "test-pairing-key", "00:11:22:33:44:55");
void Assert(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
void Reject(Action action, string text)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException) { Console.WriteLine("PASS " + text); return; }
    throw new Exception("Not rejected: " + text);
}
void WriteState(string label, string language, int quality = 1)
{
    File.WriteAllText(Path.Combine(root, "devices.json"), JsonSerializer.Serialize(new[] { tv with { Name = label } }));
    File.WriteAllText(Path.Combine(root, "language.txt"), language);
    File.WriteAllText(Path.Combine(root, "settings.json"),
        $"{{\"RefreshSeconds\":12,\"Quality\":{quality},\"Sound\":0,\"MouseControl\":true}}");
}
bool Same(AppSettingsSnapshot a, AppSettingsSnapshot b) => a.Files.Count == b.Files.Count && a.Files.All(f => b.Files.GetValueOrDefault(f.Key) == f.Value);

WriteState("Original", "ar");
Directory.CreateDirectory(Path.Combine(root, "KLightTV"));
var setupName = new string('A', 64) + ".json";
File.WriteAllText(Path.Combine(root, "KLightTV", setupName), """{"Home":"test.home/.Main","Screensaver":"test.dream/.Dream","DefaultScreensaver":"test.dream/.Dream","Enabled":"1"}""");
var original = store.Capture();
Assert(original.Files.ContainsKey("settings.json"), "capture includes the app settings file");
var point = store.Save("app", "Original point", original);
Assert(store.List(out _).Length == 1, "create and list verified app point");
WriteState("Changed", "en", quality: 2);
File.Delete(Path.Combine(root, "KLightTV", setupName));
var changed = store.Capture();
var safety = store.RestoreApp(point, changed);
Assert(Same(store.Capture(), original), "restore TV list, language and KLightTV history byte for byte");
Assert(Same(RestorePointStore.Data<AppSettingsSnapshot>(safety), changed), "save automatic undo point before restore");
store.RestoreApp(safety, store.Capture());
Assert(Same(store.Capture(), changed), "undo an app restore");

var exportPath = root + ".tvmrestore";
store.Export(point, exportPath);
var imported = store.Import(exportPath);
Assert(imported.Id != point.Id && imported.Sha256 == point.Sha256, "export/import validates payload and preserves existing points");
Reject(() => RestorePointStore.Validate(point with { Sha256 = new string('0', 64) }), "reject corrupt payload checksum");
Reject(() => RestorePointStore.Validate(point with { Format = 99 }), "reject unsupported future backup format");
Reject(() => store.Save("app", "traversal", new AppSettingsSnapshot(new() { ["../outside.txt"] = "eA==" })), "reject path traversal before filesystem writes");
Reject(() => store.Save("app", "language", new AppSettingsSnapshot(new() { ["language.txt"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("xx")) })), "reject invalid language");
Reject(() => store.Save("app", "settings", new AppSettingsSnapshot(new() { ["settings.json"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"RefreshSeconds":999}""")) })), "reject out-of-range app settings payload");
Reject(() => store.Save("app", "device", new AppSettingsSnapshot(new() { ["devices.json"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("[{\"Name\":\"invalid\",\"Address\":\"outside\",\"Port\":0}]")) })), "reject invalid TV connection payload");

var beforeFailure = store.Capture();
if (OperatingSystem.IsWindows())
{
    using (var locked = new FileStream(Path.Combine(root, "language.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        Reject(() => store.RestoreApp(point, beforeFailure), "simulate a locked file during an app restore");
    Assert(File.Exists(Path.Combine(root, "pending-restore.tvmrestore")), "keep recovery journal after interrupted rollback");
    Assert(store.RecoverPending() && Same(store.Capture(), beforeFailure), "recover previous app settings from journal after restart");
    Assert(!store.RecoverPending(), "completed recovery does not repeat");
}
var count = store.List(out _).Length;
store.BeforeConfigChange("devices.json", File.ReadAllBytes(Path.Combine(root, "devices.json")));
Assert(store.List(out _).Length == count, "avoid duplicate automatic points for unchanged settings");
store.BeforeConfigChange("devices.json", Encoding.UTF8.GetBytes("[]"));
Assert(store.List(out _).Length == count + 1, "create automatic point before a real configuration change");
File.WriteAllText(Path.Combine(root, "devices.json"), "broken json");
Reject(() => store.BeforeConfigChange("devices.json", Encoding.UTF8.GetBytes("[]")), "block settings mutation if current configuration cannot be backed up");
Assert(File.ReadAllText(Path.Combine(root, "devices.json")) == "broken json", "preserve unreadable configuration for recovery");
WriteState("Changed", "en");

TvSettingsSnapshot Snapshot(string identity, string volume) => new(identity, 0, "Test TV", "192.168.1.50", "test.home/.Main", new()
{
    ["system"] = new() { ["volume_music"] = volume, ["font_scale"] = "1" },
    ["secure"] = new() { ["adb_enabled"] = volume, ["wifi_secret"] = "do not restore" },
    ["global"] = new() { ["adb_enabled"] = volume, ["animator_duration_scale"] = volume == "5" ? "1" : "0.5" }
}, []);
var currentTv = Snapshot(new string('A', 64), "5");
var targetTv = Snapshot(new string('A', 64), "6");
var tvPoint = store.Save("android-tv", "TV", targetTv);
Assert(RestorePointStore.Data<TvSettingsSnapshot>(tvPoint).Identity == targetTv.Identity, "round-trip a TV identity and settings snapshot");
var plan = TvRestoreSettings.Plan(targetTv, currentTv);
Assert(plan.Length == 2 && plan.All(c => c.Key is "volume_music" or "animator_duration_scale"), "exclude network, debugging and unknown keys from TV restore");
Reject(() => TvRestoreSettings.Plan(targetTv with { Identity = new string('B', 64) }, currentTv), "block restoration to a different TV or firmware");
Reject(() => TvRestoreSettings.Plan(targetTv with { User = 1 }, currentTv), "block restoration to another Android user");
var invalidTv = Snapshot(new string('A', 64), "200");
Reject(() => TvRestoreSettings.Plan(invalidTv, currentTv), "block out-of-range TV preference values");
Reject(() => TvRestoreSettings.Plan(Snapshot(new string('A', 64), "5.5"), currentTv), "block fractional values for integer TV preferences");
Reject(() => TvRestoreSettings.Validate(targetTv with { Home = "test.home/.Main; reboot" }), "reject injected Home activity");
Assert(TvRestoreSettings.Plan(currentTv, currentTv).Length == 0, "matching TV snapshot creates no setting changes");
Console.WriteLine("All restore-point checks passed. Test files: " + root);
