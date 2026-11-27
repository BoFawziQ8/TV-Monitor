using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
var path = Environment.GetEnvironmentVariable("TV_TEST_STATE")!;
RestorePointStore.Default = new(Path.Combine(AppContext.BaseDirectory, "test-profile-" + Guid.NewGuid().ToString("N")));
File.WriteAllText(path, """{"Home":"test.home/.Home","Secure":{},"Installed":false,"Fail":false,"Installs":0}""");
var server = new TcpListener(IPAddress.Loopback, 0);
server.Start();
var tv = new Tv("Test TV", "127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, Platform.Android);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
var ct = deadline.Token;
var preflight = await Network.Read(tv, ct);
if (preflight.Power != Power.On) throw new Exception("Fake ADB preflight: " + preflight.Detail);
if (!(await Network.ListTvApps(tv, ct)).SequenceEqual(["test.app/.Main"])) throw new Exception("App list failed");
await Network.LaunchTvApp(tv, "test.app/.Main", ct);
await Network.OpenTvSettings(tv, TvSettingsPage.Network, ct);
void Reject(Action action, string reason)
{
    try { action(); }
    catch (IOException) { Console.WriteLine("PASS: " + reason); return; }
    throw new Exception("Not rejected: " + reason);
}
var request = PlayStoreRequest.Find("VLC & أفلام ' movies");
if (!request.HttpsUrl.Contains("%26") || request.HttpsUrl.Contains("'") || request.Package is not null) throw new Exception("Search URI was not encoded");
if (PlayStoreRequest.Find("https://play.google.com/store/apps/details?id=org.videolan.vlc&hl=en").Package != "org.videolan.vlc") throw new Exception("Play link parsing failed");
Reject(() => PlayStoreRequest.PackageFrom("https://play.google.com.evil.example/store/apps/details?id=org.videolan.vlc"), "reject non-Google Play hosts");
Reject(() => PlayStoreRequest.PackageFrom("https://play.google.com/store/apps/details?id=com.one&id=com.two"), "reject ambiguous package IDs");
Reject(() => PlayStoreRequest.PackageFrom("com.app; reboot"), "reject command injection in package IDs");
await Network.OpenGooglePlay(tv, request, ct);
var state = State();
if (!state["LastIntent"]!.GetValue<string>().Contains("-p com.android.vending") || !state["LastIntent"]!.GetValue<string>().Contains("store/search?q=")) throw new Exception("Play search was not package-pinned");
await Network.OpenGooglePlay(tv, null, ct);
if (!State()["LastIntent"]!.GetValue<string>().Contains("LEANBACK_LAUNCHER")) throw new Exception("Play home not opened");
state = State(); state["HttpsUnsupported"] = true; File.WriteAllText(path, state.ToJsonString());
await Network.OpenGooglePlay(tv, PlayStoreRequest.Find("org.videolan.vlc"), ct);
if (!State()["LastIntent"]!.GetValue<string>().Contains("market://details?id=org.videolan.vlc")) throw new Exception("Market fallback failed");
if (!await Network.GooglePlayAppInstalled(tv, "org.videolan.vlc", ct) || await Network.GooglePlayAppInstalled(tv, "com.example.missing", ct)) throw new Exception("Installed-state check failed");
state = State(); state["StoreAvailable"] = false; File.WriteAllText(path, state.ToJsonString());
try { await Network.OpenGooglePlay(tv, request, ct); throw new Exception("Missing Play Store should be reported"); }
catch (IOException ex) when (ex.Message.StartsWith(T("Google Play Store is not installed for this TV user. This function requires a TV with Google Play."))) { }
Console.WriteLine("PASS: Google Play search, direct links, home, URI fallback, installed check and missing-store handling. No install command sent.");
state = State(); state["StoreAvailable"] = true;
state["Home"] = "kw.urtv.personal.home/.MainActivity";
state["Secure"]!["screensaver_components"] = "kw.urtv.personal.screensaver/.ScreenDream";
state["Secure"]!["screensaver_default_component"] = "kw.urtv.personal.screensaver/.ScreenDream";
state["Secure"]!["screensaver_enabled"] = "1";
File.WriteAllText(path, state.ToJsonString());
var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("1234567890abcdef\ntest/fingerprint\n0")));
var history = Path.Combine(RestorePointStore.Default.Root, "KLightTV");
Directory.CreateDirectory(history);
File.WriteAllText(Path.Combine(history, identity + ".json"), """{"Home":"test.home/.Home","Screensaver":"null","DefaultScreensaver":"null","Enabled":"null"}""");
await Network.RestoreTvSetup(tv, ct);
if (State()["Home"]!.GetValue<string>() != "test.home/.Home") throw new Exception("Legacy Home recovery was lost");
Console.WriteLine("PASS: existing Home/screensaver recovery history remains usable.");
var baseline = await Network.CaptureTvSettings(tv, _ => {}, ct);
var point = RestorePointStore.Default.Save("android-tv", "TV restoration test", baseline);
state = State();
state["System"]!["volume_music"] = "8";
state["System"]!["font_scale"] = "1.25";
state["Global"]!["adb_enabled"] = "0";
File.WriteAllText(path, state.ToJsonString());
await Network.RestoreTvSettings(tv, point, RestorePointStore.Default, _ => {}, ct);
state = State();
if (state["System"]!["volume_music"]!.GetValue<string>() != "5" ||
    state["System"]!["font_scale"]!.GetValue<string>() != "1" ||
    state["Global"]!["adb_enabled"]!.GetValue<string>() != "0") throw new Exception("TV preference restore or access exclusion failed");
Console.WriteLine("PASS: TV preference restore with acknowledged readback; debugging setting unchanged.");
state["System"]!["volume_music"] = "9";
state["System"]!["font_scale"] = "1.5";
state["FailRestore"] = true;
File.WriteAllText(path, state.ToJsonString());
try { await Network.RestoreTvSettings(tv, point, RestorePointStore.Default, _ => {}, ct); throw new Exception("Expected partial restore rejection"); }
catch (IOException ex) when (ex.Message.StartsWith(T("The TV rejected the restore. Its previous settings were restored. "))) { }
state = State();
if (state["System"]!["volume_music"]!.GetValue<string>() != "9" || state["System"]!["font_scale"]!.GetValue<string>() != "1.5")
    throw new Exception("Rollback left a partially restored TV");
if (RestorePointStore.Default.List(out _).Count(p => p.Name.StartsWith("Before TV restore:")) < 2)
    throw new Exception("Missing automatic TV undo points");
Console.WriteLine("PASS: rejected TV preference rolls back earlier writes and retains undo points (simulated TV).");
JsonObject State() => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
