using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

enum TvSettingsPage { General, Display, Sound, Network, Apps }

static partial class Network
{
    private const string KLightHome = "kw.urtv.personal.home/.MainActivity";
    private const string KLightDream = "kw.urtv.personal.screensaver/.ScreenDream";
    private const string HomeIntent = "-a android.intent.action.MAIN -c android.intent.category.HOME";
    private const string LeanbackIntent = "-a android.intent.action.MAIN -c android.intent.category.LEANBACK_LAUNCHER";
    private static readonly string BundlePath = Path.Combine(AppContext.BaseDirectory, "KLightTV");
    private static string SnapshotPath => Path.Combine(RestorePointStore.Default.Root, "KLightTV");
    private static readonly Regex ComponentName = new(@"^[\w.]+/[\w.$]+$", RegexOptions.CultureInvariant);
    private sealed record TvSetup(string Home, string Screensaver, string DefaultScreensaver, string Enabled);

    internal static async Task OpenTvSettings(Tv tv, TvSettingsPage page, CancellationToken ct)
    {
        var user = await ActiveAndroidUser(tv, ct);
        var action = page switch
        {
            TvSettingsPage.Display => "DISPLAY_SETTINGS", TvSettingsPage.Sound => "SOUND_SETTINGS",
            TvSettingsPage.Network => "WIFI_SETTINGS", TvSettingsPage.Apps => "MANAGE_APPLICATIONS_SETTINGS",
            _ => "SETTINGS"
        };
        await StartAndroidActivity(tv, user, "-a android.settings." + action, ct);
    }

    internal static async Task<string[]> ListTvApps(Tv tv, CancellationToken ct)
    {
        var user = await ActiveAndroidUser(tv, ct);
        var result = await TvCommand(tv, $"cmd package query-activities --brief --components --user {user} {LeanbackIntent}", ct);
        return Lines(result).Where(IsComponent).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static async Task LaunchTvApp(Tv tv, string component, CancellationToken ct)
    {
        if (!IsComponent(component)) throw new IOException(T("Android returned an invalid app activity."));
        var user = await ActiveAndroidUser(tv, ct);
        await StartAndroidActivity(tv, user, $"-n {ShellQuote(component)}", ct);
    }

    internal static async Task<string> RestoreTvSetup(Tv tv, CancellationToken ct)
    {
        var user = await ActiveAndroidUser(tv, ct);
        var current = await ReadSetup(tv, user, ct);
        if (!UsesKLight(current)) return T("The TV is already using a different Home and screensaver.");
        var previous = LoadPrevious(await SetupIdentity(tv, user, ct))
            ?? throw new IOException(T("No previous setup is saved for this TV."));
        // Leave any setting the user changed independently since installing KLightTV.
        var restore = new TvSetup(
            current.Home == KLightHome ? previous.Home : current.Home,
            current.Screensaver == KLightDream ? previous.Screensaver : current.Screensaver,
            current.DefaultScreensaver == KLightDream ? previous.DefaultScreensaver : current.DefaultScreensaver,
            current.Screensaver == KLightDream && current.Enabled == "1" ? previous.Enabled : current.Enabled);
        await ApplyWithRollback(tv, user, current, restore, ct);
        return T("Previous Home and screensaver settings restored.");
    }

    private static bool UsesKLight(TvSetup setup) =>
        setup.Home == KLightHome || setup.Screensaver == KLightDream || setup.DefaultScreensaver == KLightDream;

    private static async Task ApplyWithRollback(Tv tv, int user, TvSetup before, TvSetup after, CancellationToken ct)
    {
        try { await ApplySetup(tv, user, before, after, ct); }
        catch (Exception applyError) when (applyError is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            try
            {
                var actual = await ReadSetup(tv, user, CancellationToken.None);
                await ApplySetup(tv, user, actual, before, CancellationToken.None);
            }
            catch (Exception restoreError) when (restoreError is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                throw new IOException(T("The setup changed only partly. Reconnect the TV and use Restore previous setup. ") +
                    restoreError.Message, applyError);
            }
            throw new IOException(T("The change could not be verified. The previous settings were restored. ") + applyError.Message, applyError);
        }
    }

    private static async Task ApplySetup(Tv tv, int user, TvSetup before, TvSetup after, CancellationToken ct)
    {
        if (!IsComponent(after.Home)) throw new IOException(T("The previous Home activity is invalid."));
        if (before.Home != after.Home)
            await TvCommand(tv, $"cmd package set-home-activity --user {user} {ShellQuote(after.Home)}", ct);
        var changes = new[]
        {
            ("screensaver_components", before.Screensaver, after.Screensaver),
            ("screensaver_default_component", before.DefaultScreensaver, after.DefaultScreensaver),
            ("screensaver_enabled", before.Enabled, after.Enabled)
        };
        foreach (var (key, oldValue, value) in changes)
        {
            if (oldValue == value) continue;
            var command = value == "null" ? $"delete secure {key}" : $"put secure {key} {ShellQuote(value)}";
            await TvCommand(tv, $"settings --user {user} {command}", ct);
        }
        if (await ReadSetup(tv, user, ct) != after)
            throw new IOException(T("Android did not report the requested Home and screensaver settings."));
    }

    private static async Task<TvSetup> ReadSetup(Tv tv, int user, CancellationToken ct)
    {
        var resolved = await TvCommand(tv, $"cmd package resolve-activity --brief --user {user} {HomeIntent}", ct);
        var home = Lines(resolved).LastOrDefault(IsComponent);
        if (home is null || home.StartsWith("android/", StringComparison.Ordinal))
            throw new IOException(T("Android has no saved default Home app to restore."));
        async Task<string> Secure(string key) => await TvCommand(tv, $"settings --user {user} get secure {key}", ct);
        return new(NormalizeComponent(home), NormalizeComponent(await Secure("screensaver_components")),
            NormalizeComponent(await Secure("screensaver_default_component")), await Secure("screensaver_enabled"));
    }

    private static string NormalizeComponent(string value) => string.Join(',', value.Split(',').Select(part =>
    {
        var slash = part.IndexOf('/');
        if (slash < 0) return part;
        var package = part[..slash];
        var prefix = package + "/";
        return part.StartsWith(prefix + package + ".", StringComparison.Ordinal)
            ? prefix + part[(prefix.Length + package.Length)..] : part;
    }));

    private static async Task<string> SetupIdentity(Tv tv, int user, CancellationToken ct)
    {
        var id = await TvCommand(tv, $"settings --user {user} get secure android_id", ct);
        var fingerprint = await TvCommand(tv, "getprop ro.build.fingerprint", ct);
        if (!Regex.IsMatch(id, @"^[0-9a-fA-F]{16}$") || fingerprint.Length == 0)
            throw new IOException(T("Could not identify the TV for a safe settings backup."));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\n{fingerprint}\n{user}")));
    }

    private static TvSetup? LoadPrevious(string identity)
    {
        var path = Path.Combine(SnapshotPath, identity + ".json");
        if (File.Exists(path)) return JsonSerializer.Deserialize<TvSetup>(File.ReadAllText(path));
        var original = Path.Combine(BundlePath, "PreviousSetup.json");
        return File.Exists(original)
            ? JsonSerializer.Deserialize<Dictionary<string, TvSetup>>(File.ReadAllText(original))?.GetValueOrDefault(identity)
            : null;
    }

    private static void SavePrevious(string identity, TvSetup setup)
    {
        Directory.CreateDirectory(SnapshotPath);
        var path = Path.Combine(SnapshotPath, identity + ".json");
        var json = JsonSerializer.Serialize(setup);
        RestorePointStore.Default.BeforeConfigChange("KLightTV/" + identity + ".json", Encoding.UTF8.GetBytes(json));
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
    }

    private static async Task<int> ActiveAndroidUser(Tv tv, CancellationToken ct)
    {
        if (tv.Platform != Platform.Android || (await Read(tv, ct)).Power != Power.On)
            throw new IOException(T("Turn on and connect the Android TV before using TV Tools."));
        var output = await TvCommand(tv, "am get-current-user", ct);
        if (!int.TryParse(output, out var user) || user < 0)
            throw new IOException(T("Could not read the current Android user."));
        return user;
    }

    private static async Task StartAndroidActivity(Tv tv, int user, string intent, CancellationToken ct)
    {
        var output = await TvCommand(tv, $"am start --user {user} {intent}", ct);
        if (!output.Contains("Starting:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Error", StringComparison.OrdinalIgnoreCase))
            throw new IOException(T("The TV could not open that screen. ") + ShortError(output));
    }

    private static async Task<string> TvCommand(Tv tv, string command, CancellationToken ct)
    {
        const string ok = "TVMONITOR_OK";
        var result = await RunAdb(ct, "-s", Serial(tv), "shell", command + " && echo " + ok);
        var lines = Lines(result);
        if (!lines.Contains(ok)) throw new IOException(T("Android did not complete the command. ") + ShortError(result));
        return string.Join('\n', lines.Where(line => line != ok));
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static bool IsComponent(string text) => ComponentName.IsMatch(text);
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

