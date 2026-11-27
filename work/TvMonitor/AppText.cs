global using static AppText;

using System.Globalization;

static class AppText
{
    private static readonly string Preference = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "language.txt");
    private static readonly Lazy<Dictionary<string, string>> Arabic = new(LoadArabic);
    internal static bool IsArabic { get; private set; } = ReadPreference();
    private static readonly CultureInfo Kuwait = CultureInfo.GetCultureInfo("ar-KW");

    internal static string T(string text) => IsArabic ? Arabic.Value.GetValueOrDefault(text, text) : text;
    internal static string Tf(FormattableString message) => IsArabic
        ? string.Format(Kuwait, T(message.Format), message.GetArguments())
        : message.ToString(CultureInfo.CurrentCulture);

    internal static void ReloadPreference() => IsArabic = ReadPreference();

    internal static void SwitchLanguage()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Preference)!);
        var temporary = Preference + ".tmp";
        var next = IsArabic ? "en" : "ar";
        RestorePointStore.Default.BeforeConfigChange("language.txt", System.Text.Encoding.UTF8.GetBytes(next));
        File.WriteAllText(temporary, next);
        File.Move(temporary, Preference, true);
        IsArabic = !IsArabic;
    }

    private static bool ReadPreference()
    {
        try { return File.Exists(Preference) && File.ReadAllText(Preference).Trim() == "ar"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Dictionary<string, string> LoadArabic()
    {
        using var source = typeof(AppText).Assembly.GetManifestResourceStream("TvMonitor.Arabic")!;
        using var reader = new StreamReader(source);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('|');
            if (separator > 0) result.Add(line[..separator], line[(separator + 1)..]);
        }
        return result;
    }
}
