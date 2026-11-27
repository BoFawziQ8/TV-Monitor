using System.Text.Json;
using System.Text.RegularExpressions;
var path = Environment.GetEnvironmentVariable("TV_TEST_STATE")!;
var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path))!;
var action = args[2] == "connect" ? "connect" : args[4];
if (action == "connect") { Console.WriteLine("connected"); return; }
if (action == "install-multi-package")
{
    if (args.Length != 8 || args[5] != "-r" || !File.Exists(args[6]) || !File.Exists(args[7]))
        throw new Exception("APK arguments wrong");
    state.Installed = true;
    state.Installs++;
    Save();
    Console.WriteLine("Success");
    return;
}
var command = args[5];
if (command.Contains("__TVMON_RESTORE_DONE__"))
{
    foreach (Match match in Regex.Matches(command, @"settings --user \d+ (put|delete) (system|secure|global) '([^']+)'(?: '([^']*)')?"))
    {
        var key = match.Groups[3].Value;
        if (state.FailRestore && key == "font_scale")
        {
            state.FailRestore = false; Save();
            Console.WriteLine("Simulated permission denial after earlier settings were written");
            return;
        }
        var table = match.Groups[2].Value switch { "system" => state.System, "secure" => state.Secure, _ => state.Global };
        if (match.Groups[1].Value == "delete") table.Remove(key);
        else table[key] = match.Groups[4].Value;
    }
    var home = Regex.Match(command, @"set-home-activity --user \d+ '([^']+)'");
    if (home.Success) state.Home = home.Groups[1].Value;
    Save(); Console.WriteLine("__TVMON_RESTORE_DONE__"); return;
}
if (command.Contains("sha256sum"))
{
    if (state.Installed)
        Console.WriteLine(command.Contains(".home") ? "7E7987413FBB7CD8019F9447F39A2D2D48FDF0E3EA715902DB4CF210D1D693C7  /data/home.apk"
            : "C58257E418CC7F8A14B22ECCBEF1A6F8FDB5523EBD8802C66DA4FA1042DC412B  /data/dream.apk");
    return;
}
if (command.Contains("dumpsys power")) { Console.WriteLine("SDK=29\nDisplay Power: state=ON"); return; }
if (command.StartsWith("am get-current-user")) Console.WriteLine("0");
else if (command.StartsWith("getprop ro.build.version.sdk")) Console.WriteLine("29");
else if (command.StartsWith("getprop ro.build.fingerprint")) Console.WriteLine("test/fingerprint");
else if (command.StartsWith("pm list features")) Console.WriteLine("feature:android.software.leanback");
else if (command.Contains(" get secure android_id")) Console.WriteLine("1234567890abcdef");
else if (command.Contains(" list system"))
    foreach (var entry in state.System) Console.WriteLine(entry.Key + "=" + entry.Value);
else if (command.Contains(" list global"))
    foreach (var entry in state.Global) Console.WriteLine(entry.Key + "=" + entry.Value);
else if (command.Contains(" list secure"))
{
    Console.WriteLine("android_id=1234567890abcdef");
    foreach (var entry in state.Secure) Console.WriteLine(entry.Key + "=" + entry.Value);
}
else if (command.StartsWith("pm path"))
{
    var package = Regex.Match(command, @"--user \d+ '([^']+)'").Groups[1].Value;
    if ((package == "com.android.vending" && state.StoreAvailable) || package == "org.videolan.vlc") Console.WriteLine("package:/data/app/" + package + "/base.apk");
}
else if (command.Contains("resolve-activity"))
{
    if (command.Contains("com.android.vending"))
        Console.WriteLine(state.HttpsUnsupported && command.Contains("https://") ? "No activity found" : "com.android.vending/.TvMainActivity");
    else Console.WriteLine("priority=0\n" + state.Home);
}
else if (command.Contains("query-activities")) Console.WriteLine("1 activities found:\n test.app/.Main");
else if (command.StartsWith("am start")) { state.LastIntent = command; Save(); Console.WriteLine("Starting: Intent"); }
else if (command.Contains("set-home-activity"))
{
    state.Home = Regex.Match(command, "'([^']+)'").Groups[1].Value;
    Save();
}
else if (command.Contains(" get secure "))
{
    var key = command.Split(' ')[5];
    Console.WriteLine(state.Secure.GetValueOrDefault(key, "null"));
}
else if (command.Contains(" put secure ") || command.Contains(" delete secure "))
{
    var key = command.Split(' ')[5];
    if (state.Fail && key == "screensaver_default_component")
    {
        state.Fail = false;
        Save();
        Console.WriteLine("Simulated rejected screensaver change");
        return;
    }
    state.Secure[key] = command.Contains(" delete ") ? "null" : Regex.Match(command, "'([^']+)'").Groups[1].Value;
    Save();
}
else throw new Exception("Unexpected command: " + command);
Console.WriteLine("TVMONITOR_OK");
void Save() => File.WriteAllText(path, JsonSerializer.Serialize(state));

sealed class State
{
    public string Home { get; set; } = "test.home/.Home";
    public Dictionary<string, string> Secure { get; set; } = new();
    public Dictionary<string, string> System { get; set; } = new() { ["volume_music"] = "5", ["font_scale"] = "1" };
    public Dictionary<string, string> Global { get; set; } = new() { ["animator_duration_scale"] = "1", ["adb_enabled"] = "1" };
    public bool FailRestore { get; set; }
    public bool StoreAvailable { get; set; } = true;
    public bool HttpsUnsupported { get; set; }
    public string LastIntent { get; set; } = "";
    public bool Installed { get; set; }
    public bool Fail { get; set; }
    public int Installs { get; set; }
}
