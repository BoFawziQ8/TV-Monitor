using System.Text.RegularExpressions;

sealed record PlayStoreRequest(string HttpsUrl, string MarketUrl, string? Package)
{
    internal static bool ValidPackage(string value) => value.Length <= 255 &&
        Regex.IsMatch(value, @"^[a-zA-Z_][a-zA-Z0-9_]*(\.[a-zA-Z_][a-zA-Z0-9_]*)+$", RegexOptions.CultureInvariant);

    internal static PlayStoreRequest Find(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 2048 || value.Any(char.IsControl))
            throw new IOException(T("Enter an app name, Google Play link or package ID."));
        if (ValidPackage(value) || value.Contains("://", StringComparison.Ordinal))
        {
            var package = PackageFrom(value);
            return new("https://play.google.com/store/apps/details?id=" + package, "market://details?id=" + package, package);
        }
        if (value.Length > 200) throw new IOException(T("Use a shorter app name, or paste its Google Play link."));
        var encoded = Uri.EscapeDataString(value);
        return new("https://play.google.com/store/search?q=" + encoded + "&c=apps", "market://search?q=" + encoded + "&c=apps", null);
    }

    internal static string PackageFrom(string value)
    {
        value = value.Trim();
        if (ValidPackage(value)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("play.google.com", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.TrimEnd('/') != "/store/apps/details" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            throw new IOException(T("Paste a Google Play app link or enter its package ID to open or check a specific app."));
        var ids = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == "id")
            .Select(pair => Uri.UnescapeDataString(pair[1])).ToArray();
        if (ids.Length != 1 || !ValidPackage(ids[0]))
            throw new IOException(T("The Google Play link does not contain a valid app package ID."));
        return ids[0];
    }
}

static partial class Network
{
    private const string PlayPackage = "com.android.vending";

    internal static async Task OpenGooglePlay(Tv tv, PlayStoreRequest? request, CancellationToken ct)
    {
        var user = await ActiveAndroidUser(tv, ct);
        if (!await PackageInstalled(tv, user, PlayPackage, ct))
            throw new IOException(T("Google Play Store is not installed for this TV user. This function requires a TV with Google Play."));
        if (request is null)
        {
            foreach (var category in new[] { "LEANBACK_LAUNCHER", "LAUNCHER" })
            {
                var intent = $"-a android.intent.action.MAIN -c android.intent.category.{category} -p {PlayPackage}";
                var result = await TvCommand(tv, $"cmd package resolve-activity --brief --user {user} {intent}", ct);
                if (Lines(result).Any(IsComponent))
                { await StartAndroidActivity(tv, user, intent, ct); return; }
            }
            request = new("https://play.google.com/store/apps", "market://search?q=apps&c=apps", null);
        }
        foreach (var uri in new[] { request.HttpsUrl, request.MarketUrl })
        {
            var intent = $"-a android.intent.action.VIEW -d {ShellQuote(uri)} -p {PlayPackage}";
            var result = await TvCommand(tv, $"cmd package resolve-activity --brief --user {user} {intent}", ct);
            if (!Lines(result).Any(IsComponent)) continue;
            await StartAndroidActivity(tv, user, intent, ct);
            return;
        }
        throw new IOException(T("This TV's Google Play Store could not open that link. Try Open Google Play and search with the TV remote."));
    }

    internal static async Task<bool> GooglePlayAppInstalled(Tv tv, string package, CancellationToken ct)
    {
        if (!PlayStoreRequest.ValidPackage(package)) throw new IOException(T("Invalid app package ID."));
        var user = await ActiveAndroidUser(tv, ct);
        return await PackageInstalled(tv, user, package, ct);
    }

    private static async Task<bool> PackageInstalled(Tv tv, int user, string package, CancellationToken ct)
    {
        var result = await TvCommand(tv, $"pm path --user {user} {ShellQuote(package)} || true", ct);
        return Lines(result).Any(line => line.StartsWith("package:", StringComparison.Ordinal));
    }
}
