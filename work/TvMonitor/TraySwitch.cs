using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

// One tray switch: a left click powers on the chosen TV. The dashboard is hidden
// to the tray instead of closing so the switch stays reachable.
sealed class TraySwitch : IDisposable
{
    private static readonly string TargetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "tray.json");
    private static Tv? remembered;

    private readonly NotifyIcon notify;
    private readonly Icon icon;
    private readonly ToolStripMenuItem power = new() { Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly ToolStripMenuItem remote = new(T("Remote"));
    private readonly ToolStripMenuItem choose = new(T("Chosen TV"));
#if !STORE_BUILD
    private readonly ToolStripMenuItem startup = new(T("Start with Windows"));
#endif
    private readonly Func<Tv?> chosen;
    private readonly Func<IReadOnlyList<Tv>> all;
    private readonly Action<Tv> select;
    private readonly Func<Tv, Task<string>> powerOn;
    private readonly Action remoteFor;
    private readonly Action<string>? report;
    private readonly Action open;
    private readonly Action exit;
    private bool busy;
    private bool hinted;

    internal TraySwitch(Func<Tv?> chosen, Func<IReadOnlyList<Tv>> all, Action<Tv> select,
        Func<Tv, Task<string>> powerOn, Action remoteFor, Action<string>? report, Action open, Action exit)
    {
        this.chosen = chosen; this.all = all; this.select = select;
        this.powerOn = powerOn; this.remoteFor = remoteFor; this.report = report; this.open = open; this.exit = exit;
        var menu = new ContextMenuStrip { ShowImageMargin = false, RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No };
        power.Click += (_, _) => _ = TurnOn();
        remote.Click += (_, _) => this.remoteFor();
        choose.DropDown.RightToLeft = menu.RightToLeft;
        var openItem = new ToolStripMenuItem(T("Open TV Monitor"));
        openItem.Click += (_, _) => this.open();
        var exitItem = new ToolStripMenuItem(T("Exit TV Monitor"));
        exitItem.Click += (_, _) => this.exit();
#if !STORE_BUILD
        // Packaged Store builds cannot use a Run entry for autostart.
        startup.Click += (_, _) =>
        {
            try { Startup.Set(!Startup.Enabled); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                report?.Invoke(T("The startup setting could not be saved."));
            }
            startup.Checked = Startup.Enabled;
        };
        menu.Items.AddRange([power, remote, new ToolStripSeparator(), choose, startup, new ToolStripSeparator(), openItem, exitItem]);
#else
        menu.Items.AddRange([power, remote, new ToolStripSeparator(), choose, new ToolStripSeparator(), openItem, exitItem]);
#endif
        menu.Opening += (_, _) => Refresh(menu);
        icon = (Icon)Program.AppIcon.Clone();
        notify = new NotifyIcon
        {
            Icon = icon, Text = Tf($"TV Monitor · Click to turn on {chosen()?.Name ?? T("your TV")}"),
            ContextMenuStrip = menu, Visible = true
        };
        notify.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _ = TurnOn(); };
        RefreshText();
    }

    internal void RefreshText()
    {
        if (busy) return;
        var tv = chosen();
        notify.Text = Short(tv is null ? "TV Monitor" : Tf($"TV Monitor · Click to turn on {tv.Name}"));
    }

    internal void Remember() => Save(chosen());
    internal void RemindInTray()
    {
        if (hinted) return;
        hinted = true;
        Balloon(chosen() is { } tv
            ? Tf($"TV Monitor is still running in the tray. Click its icon to turn on {tv.Name}.")
            : T("TV Monitor is still running in the tray. Open it from the tray menu."), ToolTipIcon.Info);
    }

    internal static Tv? Recall(IReadOnlyList<Tv> devices)
    {
        try
        {
            if (!File.Exists(TargetPath)) return null;
            var saved = JsonSerializer.Deserialize<Target>(File.ReadAllText(TargetPath));
            if (saved is null) return null;
            return devices.FirstOrDefault(tv => tv.Address == saved.Address && tv.Port == saved.Port)
                ?? devices.FirstOrDefault(tv => tv.Name == saved.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Save(Tv? tv)
    {
        if (tv is null || tv == remembered) return;
        remembered = tv;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
            var temporary = TargetPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Target(tv.Name, tv.Address, tv.Port)));
            File.Move(temporary, TargetPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Refresh(ContextMenuStrip menu)
    {
        var current = chosen();
        power.Text = current is null ? T("Turn on TV") : Tf($"Turn on {current.Name}");
        power.Enabled = current is not null && !busy;
        remote.Enabled = current is not null && !busy;
        choose.DropDownItems.Clear();
        foreach (var tv in all())
        {
            var item = new ToolStripMenuItem(tv.Name) { Checked = tv == current };
            var target = tv;
            item.Click += (_, _) => { select(target); Save(target); RefreshText(); };
            choose.DropDownItems.Add(item);
        }
        choose.Enabled = choose.DropDownItems.Count > 0 && !busy;
#if !STORE_BUILD
        // The menu hides its image margin, so the tick is shown in the text.
        var starts = Startup.Enabled;
        startup.Checked = starts;
        startup.Text = (starts ? "✓ " : "") + T("Start with Windows");
#endif
        menu.RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
    }

    private async Task TurnOn()
    {
        if (busy) return;
        if (chosen() is not { } tv)
        {
            Balloon(T("No TV is saved. Add a TV in TV Monitor first."), ToolTipIcon.Warning);
            open();
            return;
        }
        busy = true;
        notify.Text = Short(Tf($"Turning on {tv.Name}…"));
        string message;
        try
        {
            message = await powerOn(tv);
            Balloon(message, ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Net.WebSockets.WebSocketException or
                                    UnauthorizedAccessException or OperationCanceledException or JsonException or
                                    System.ComponentModel.Win32Exception or NetworkInformationException)
        {
            message = ex is OperationCanceledException ? T("Timed out. Check the TV and try again.") : ex.Message;
            Balloon(message, ToolTipIcon.Warning);
        }
        finally
        {
            busy = false;
            RefreshText();
        }
        report?.Invoke(message);
    }

    private void Balloon(string message, ToolTipIcon icon)
    {
        notify.BalloonTipTitle = "TV Monitor";
        notify.BalloonTipText = message.Length > 255 ? message[..252] + "…" : message;
        notify.BalloonTipIcon = icon;
        notify.ShowBalloonTip(6000);
    }

    private static string Short(string text) => text.Length > 63 ? text[..60] + "…" : text;

    public void Dispose()
    {
        notify.Visible = false;
        notify.Dispose();
        icon.Dispose();
    }

    private sealed record Target(string Name, string Address, int Port);
}
