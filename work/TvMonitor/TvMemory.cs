using System.Globalization;
using System.Text.RegularExpressions;

sealed record TvSpace(long Total, long Used, long Available)
{
    internal int UsedPercent => (int)Math.Clamp(Used / (double)Total * 100, 0, 100);
    internal static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GiB" : $"{bytes / (double)(1L << 20):0.0} MiB";
}
sealed record TvMemoryReading(TvSpace Storage, TvSpace? Ram, DateTimeOffset Checked);

static partial class Network
{
    internal static TvSpace ParseTvStorage(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var fields = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // df -P can wrap a long filesystem name onto the preceding line.
            int start = fields.Length >= 5 && long.TryParse(fields[0], out _) ? 0 : 1;
            if (fields.Length < start + 5 || !fields[start + 3].EndsWith('%')) continue;
            if (!long.TryParse(fields[start], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                !long.TryParse(fields[start + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var used) ||
                !long.TryParse(fields[start + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var available) ||
                total <= 0 || used > total || available > total) continue;
            try { return new(checked(total * 1024), checked(used * 1024), checked(available * 1024)); }
            catch (OverflowException) { break; }
        }
        throw new IOException(T("The TV did not report valid storage information."));
    }

    internal static TvSpace? ParseTvRam(string output)
    {
        long? Value(string key)
        {
            var match = Regex.Match(output, @"(?m)^" + key + @":\s+(\d+)\s+kB\s*$");
            return match.Success && long.TryParse(match.Groups[1].Value, out var value) && value <= long.MaxValue / 1024 ? value * 1024 : null;
        }
        var total = Value("MemTotal"); var available = Value("MemAvailable");
        return total is > 0 && available is >= 0 && available <= total ? new(total.Value, total.Value - available.Value, available.Value) : null;
    }

    internal static async Task<TvMemoryReading> ReadTvMemory(Tv tv, CancellationToken ct)
    {
        await ActiveAndroidUser(tv, ct);
        var storage = ParseTvStorage(await TvCommand(tv, "df -Pk /sdcard", ct));
        TvSpace? ram = null;
        try { ram = ParseTvRam(await TvCommand(tv, "cat /proc/meminfo", ct)); }
        catch (IOException) { /* Some TVs restrict RAM reporting; storage is still useful. */ }
        ct.ThrowIfCancellationRequested();
        return new(storage, ram, DateTimeOffset.Now);
    }
}
