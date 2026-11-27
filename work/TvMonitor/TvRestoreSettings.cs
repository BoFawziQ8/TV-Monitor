using System.Globalization;
using System.Text.RegularExpressions;

sealed record TvSettingsSnapshot(string Identity, int User, string Name, string Address, string? Home,
    Dictionary<string, Dictionary<string, string>> Tables, string[] Notes);
sealed record TvSettingChange(string Table, string Key, string? Before, string? After);

static class TvRestoreSettings
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["system"] = ["screen_brightness", "screen_brightness_mode", "screen_off_timeout", "font_scale",
            "accelerometer_rotation", "user_rotation", "sound_effects_enabled", "haptic_feedback_enabled",
            "volume_music", "volume_ring", "volume_alarm", "volume_notification", "volume_system"],
        ["secure"] = ["screensaver_components", "screensaver_default_component", "screensaver_enabled",
            "screensaver_activate_on_dock", "screensaver_activate_on_sleep", "sleep_timeout"],
        ["global"] = ["window_animation_scale", "transition_animation_scale", "animator_duration_scale"]
    };
    internal static bool Restorable(string table, string key) => Allowed.TryGetValue(table, out var names) && names.Contains(key);
    internal static int SupportedCount(TvSettingsSnapshot snapshot) => snapshot.Tables.Sum(t => t.Value.Count(v => Restorable(t.Key, v.Key)));

    internal static void Validate(TvSettingsSnapshot snapshot)
    {
        if (snapshot.Identity is null || !Regex.IsMatch(snapshot.Identity, "^[A-F0-9]{64}$") || snapshot.User < 0 ||
            snapshot.Name is null || snapshot.Name.Length > 200 || snapshot.Tables is null || snapshot.Tables.Count is < 1 or > 3 ||
            snapshot.Notes is null || snapshot.Notes.Length > 10 || (snapshot.Home is not null && !Component(snapshot.Home)))
            throw new IOException(T("Invalid TV settings backup."));
        foreach (var (table, values) in snapshot.Tables)
        {
            if (!Allowed.ContainsKey(table) || values is null || values.Count > 5000) throw new IOException(T("Invalid TV settings table."));
            foreach (var (key, value) in values)
            {
                if (!Regex.IsMatch(key, @"^[\w.:-]{1,160}$", RegexOptions.CultureInvariant) || value is null ||
                    value.Length > 16384 || value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
                    throw new IOException(T("Invalid TV setting value."));
            }
        }
    }

    internal static TvSettingChange[] Plan(TvSettingsSnapshot target, TvSettingsSnapshot current)
    {
        Validate(target); Validate(current);
        if (target.Identity != current.Identity || target.User != current.User)
            throw new IOException(T("This point belongs to a different TV, Android user, or firmware. Restore was blocked."));
        var changes = new List<TvSettingChange>();
        foreach (var (table, values) in target.Tables)
        {
            if (!current.Tables.TryGetValue(table, out var before))
                throw new IOException(T("The TV no longer allows reading a required settings table."));
            foreach (var (key, value) in values.Where(v => Restorable(table, v.Key)))
                if (before.GetValueOrDefault(key) != value)
                {
                    ValidateValue(key, value);
                    changes.Add(new(table, key, before.GetValueOrDefault(key), value));
                }
        }
        return changes.ToArray();
    }

    internal static bool Component(string value) => Regex.IsMatch(value, @"^[\w.]+/[\w.$]+$", RegexOptions.CultureInvariant);
    private static void ValidateValue(string key, string value)
    {
        if (key is "screensaver_components" or "screensaver_default_component")
        {
            if (value != "null" && !value.Split(',').All(Component)) throw new IOException(T("Invalid screensaver component in backup."));
            return;
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new IOException(T("A restorable TV preference has an unsupported value."));
        if (key is not ("font_scale" or "window_animation_scale" or "transition_animation_scale" or "animator_duration_scale") && Math.Truncate(number) != number)
            throw new IOException(T("A restorable TV preference has an unsupported value."));
        var valid = key switch
        {
            "font_scale" => number is >= .5 and <= 2,
            "window_animation_scale" or "transition_animation_scale" or "animator_duration_scale" => number is >= 0 and <= 10,
            "screen_brightness" => number is >= 0 and <= 255,
            "user_rotation" => number is >= 0 and <= 3,
            "screen_off_timeout" or "sleep_timeout" => number is >= -1 and <= int.MaxValue,
            _ when key.StartsWith("volume_", StringComparison.Ordinal) => number is >= 0 and <= 100,
            _ => number is 0 or 1
        };
        if (!valid) throw new IOException(T("A restorable TV preference is outside the supported range."));
    }
}

static partial class Network
{
    internal static async Task<TvSettingsSnapshot> CaptureTvSettings(Tv tv, Action<string> report, CancellationToken ct)
    {
        report(T("Reading TV identity and settings…"));
        var user = await ActiveAndroidUser(tv, ct);
        var identity = await SetupIdentity(tv, user, ct);
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var table in new[] { "system", "secure", "global" })
        {
            try
            {
                var text = await TvCommand(tv, $"settings --user {user} list {table}", ct);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in Lines(text))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    var key = line[..separator];
                    var value = line[(separator + 1)..];
                    if (Regex.IsMatch(key, @"^[\w.:-]{1,160}$", RegexOptions.CultureInvariant) && value.Length <= 16384)
                        values[key] = value;
                }
                if (values.Count == 0) throw new IOException("The settings table was empty or unreadable.");
                tables.Add(table, values);
            }
            catch (IOException) { notes.Add("Unreadable table: " + table); }
        }
        string? home = null;
        try
        {
            var resolved = await TvCommand(tv, $"cmd package resolve-activity --brief --user {user} {HomeIntent}", ct);
            home = Lines(resolved).LastOrDefault(IsComponent);
            if (home?.StartsWith("android/", StringComparison.Ordinal) == true) home = null;
        }
        catch (IOException) { notes.Add("Default Home could not be read."); }
        var snapshot = new TvSettingsSnapshot(identity, user, tv.Name, tv.Address, home, tables, notes.ToArray());
        TvRestoreSettings.Validate(snapshot);
        return snapshot;
    }

    internal static async Task<SavedPoint> RestoreTvSettings(Tv tv, SavedPoint point, RestorePointStore store,
        Action<string> report, CancellationToken ct)
    {
        RestorePointStore.Validate(point);
        if (point.Kind != "android-tv") throw new IOException(T("Choose a TV restore point."));
        var target = RestorePointStore.Data<TvSettingsSnapshot>(point);
        var current = await CaptureTvSettings(tv, report, ct);
        var changes = TvRestoreSettings.Plan(target, current);
        bool homeChanged = target.Home is not null && target.Home != current.Home;
        if (homeChanged && current.Home is null) throw new IOException(T("The current Home app cannot be backed up safely."));
        // Check apps before making any change. Missing activities/services cannot be restored by writing settings.
        if (homeChanged)
        {
            var homes = await TvCommand(tv, $"cmd package query-activities --brief --components --user {current.User} {HomeIntent}", ct);
            if (!Lines(homes).Where(IsComponent).Select(NormalizeComponent).Contains(NormalizeComponent(target.Home!)))
                throw new IOException(T("Install the backup's Home app on the TV before restoring this point."));
        }
        var dreams = changes.Where(c => c.Key is "screensaver_components" or "screensaver_default_component")
            .SelectMany(c => c.After == "null" ? [] : c.After!.Split(',')).ToArray();
        if (dreams.Length > 0)
        {
            var services = await TvCommand(tv, $"cmd package query-services --brief --components --user {current.User} -a android.service.dreams.DreamService", ct);
            var available = Lines(services).Where(IsComponent).Select(NormalizeComponent).ToHashSet();
            if (dreams.Any(d => !available.Contains(NormalizeComponent(d))))
                throw new IOException(T("Install the backup's screensaver app before restoring this point."));
        }
        var safety = store.Save("android-tv", "Before TV restore: " + tv.Name, current);
        if (changes.Length == 0 && !homeChanged)
        { report(T("The TV already matches this restore point.")); return safety; }
        try
        {
            report(T("Restoring supported preferences and verifying TV readback…"));
            await ApplyTvChanges(tv, current.User, changes, homeChanged ? target.Home : null, false, ct);
            await VerifyTvChanges(tv, current, changes, homeChanged ? target.Home : null, false, report, ct);
        }
        catch (Exception applyError) when (applyError is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            try
            {
                report(T("Restore failed. Rolling the TV back to its previous settings…"));
                await ApplyTvChanges(tv, current.User, changes, homeChanged ? current.Home : null, true, CancellationToken.None);
                await VerifyTvChanges(tv, current, changes, homeChanged ? current.Home : null, true, report, CancellationToken.None);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                throw new IOException(T("The TV restore was only partial. Reconnect the TV and restore its saved Before TV restore point. ") + rollbackError.Message, applyError);
            }
            throw new IOException(T("The TV rejected the restore. Its previous settings were restored. ") + applyError.Message, applyError);
        }
        report(T("Supported TV settings restored and read back. Some TVs apply display or sound changes after a restart."));
        return safety;
    }

    private static async Task ApplyTvChanges(Tv tv, int user, TvSettingChange[] changes, string? home, bool rollback, CancellationToken ct)
    {
        var commands = changes.Select(change =>
        {
            var value = rollback ? change.Before : change.After;
            return $"settings --user {user} " + (value is null
                ? $"delete {change.Table} {ShellQuote(change.Key)}"
                : $"put {change.Table} {ShellQuote(change.Key)} {ShellQuote(value)}") + " || exit 1";
        }).ToList();
        if (home is not null) commands.Add($"cmd package set-home-activity --user {user} {ShellQuote(home)} || exit 1");
        commands.Add("echo __TVMON_RESTORE_DONE__");
        var output = await RunAdb(ct, 120000, "-s", Serial(tv), "shell", string.Join("; ", commands));
        if (!Lines(output).Contains("__TVMON_RESTORE_DONE__")) throw new IOException(T("The TV did not complete the settings operation."));
    }

    private static async Task VerifyTvChanges(Tv tv, TvSettingsSnapshot identity, TvSettingChange[] changes, string? home,
        bool rollback, Action<string> report, CancellationToken ct)
    {
        var actual = await CaptureTvSettings(tv, report, ct);
        if (actual.Identity != identity.Identity || actual.User != identity.User ||
            changes.Any(c => !actual.Tables.TryGetValue(c.Table, out var table) || table.GetValueOrDefault(c.Key) != (rollback ? c.Before : c.After)) ||
            (home is not null && NormalizeComponent(actual.Home ?? "") != NormalizeComponent(home)))
            throw new IOException(T("TV settings did not pass readback verification."));
    }
}
