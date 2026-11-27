using System.Text.Json;

enum LiveQuality { Low, Balanced, High }
enum TvSound { Auto, Off }

sealed record AppSettings
{
    public int RefreshSeconds { get; init; } = 12;
    public LiveQuality Quality { get; init; } = LiveQuality.Balanced;
    public TvSound Sound { get; init; } = TvSound.Auto;
    public bool MouseControl { get; init; } = true;
}

static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(RestorePointStore.DefaultRoot, "settings.json");

    internal static AppSettings Current { get; private set; } = Load();

    internal static void Reload() => Current = Load();

    internal static bool Save(AppSettings next, out string? error)
    {
        error = null;
        next = Sanitize(next);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true });
            RestorePointStore.Default.BeforeConfigChange("settings.json", bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, FilePath, true);
            Current = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static AppSettings Sanitize(AppSettings settings) => settings with
    {
        RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 5, 300),
        Quality = Enum.IsDefined(settings.Quality) ? settings.Quality : LiveQuality.Balanced,
        Sound = Enum.IsDefined(settings.Sound) ? settings.Sound : TvSound.Auto
    };

    internal static void Validate(string text)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(text)
            ?? throw new IOException(T("The backup contains invalid app settings."));
        if (settings.RefreshSeconds is < 5 or > 300 || !Enum.IsDefined(settings.Quality) || !Enum.IsDefined(settings.Sound))
            throw new IOException(T("The backup contains invalid app settings."));
    }

    internal static (int MaxSize, int MaxFps, string BitRate) Profile(LiveQuality quality) => quality switch
    {
        LiveQuality.Low => (854, 20, "2M"),
        LiveQuality.High => (1920, 60, "8M"),
        _ => (1280, 30, "4M")
    };

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return Sanitize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }
}
