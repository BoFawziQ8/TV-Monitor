using System.Diagnostics;

static class Program
{
    internal static Icon AppIcon { get; } = LoadIcon();
    private static Icon LoadIcon()
    { using var stream = typeof(Program).Assembly.GetManifestResourceStream("TvMonitor.ico")!; return new Icon(stream); }

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        FluentTheme.ReadSystem();
        if (args.Contains("--geometry"))
        {
            var area = new Rectangle(0, 0, 1920, 1080);
            Check(StreamRemoteBar.OverlayPosition(new(500, 200, 800, 450), area, new(128, 44), 8) == new Point(1164, 208), "overlay anchors to video");
            Check(StreamRemoteBar.OverlayPosition(new(100, 50, 800, 450), area, new(128, 44), 8) == new Point(764, 58), "overlay follows moved video");
            Check(StreamRemoteBar.OverlayPosition(new(0, 0, 1920, 1080), area, new(128, 44), 8) == new Point(1784, 8), "fullscreen position");
            Check(StreamRemoteBar.OverlayPosition(new(-1800, 120, 1000, 700), new(-1920, 0, 1920, 1080), new(128, 44), 8) == new Point(-936, 128), "negative-monitor coordinates");
            Check(StreamRemoteBar.RemotePosition(new(100, 100, 800, 450), area, new(320, 460), 44, 8) == new Point(908, 160), "remote beside video when space permits");
            var edge = StreamRemoteBar.RemotePosition(new(0, 0, 1920, 1080), area, new(320, 460), 44, 8);
            Check(area.Contains(new Rectangle(edge, new Size(320, 460))), "remote remains reachable in fullscreen");
            Check(StreamRemoteBar.ButtonSize(960, 288) == new Size(144, 132), "compact icon at 300 percent DPI");
            Check(StreamRemoteBar.ButtonSize(1920, 288) == new Size(384, 132), "caption fits wide video at 300 percent DPI");
            Check(StreamRemoteBar.ButtonSize(500, 96) == new Size(48, 44), "compact icon at normal DPI");
            var boundTv = new Tv("Bound TV", "127.0.0.1", 9, Platform.Android);
            bool opened = false;
            using (var bar = new StreamRemoteBar(boundTv, Process.GetCurrentProcess(), (target, owner) => opened = ReferenceEquals(target, boundTv)))
            {
                bar.Show(); Application.DoEvents(); ((Button)bar.Controls[0]).PerformClick();
                Check(opened, "remote button uses the stream TV"); bar.Close();
            }
            using (var remote = new RemoteForm(boundTv)) { remote.Dispose(); remote.Dispose(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "geometry-result.txt"), "PASS: six geometry checks, three DPI sizes, bound-TV button callback and repeat-safe remote disposal.");
            return;
        }
        if (args.Contains("--viewer")) { Application.Run(new SimulatedVideo()); return; }
        var live = args.Length == 2 && args[0] == "--live";
        var tv = new Tv(live ? "Orca 65" : "Test TV", live ? args[1] : "127.0.0.1", live ? 5555 : 9, Platform.Android);
        Application.Run(new CheckHost(tv, live));
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
}

sealed class SimulatedVideo : FluentForm
{
    internal SimulatedVideo()
    {
        Text = "Test TV · Live controls check";
        ClientSize = new Size(800, 450); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Text = "Simulated video window — no TV connection\nF11 toggles fullscreen", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 18) });
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F11) return;
            if (FormBorderStyle == FormBorderStyle.None) { WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.Sizable; }
            else { FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; }
        };
        PrepareLayout();
    }
}

sealed class CheckHost : FluentForm
{
    private Process? process;
    private StreamRemoteBar? bar;
    private RemoteForm? remote;
    private readonly Label status = new() { Dock = DockStyle.Fill };
    internal CheckHost(Tv tv, bool live)
    {
        Text = "Live controls check"; ClientSize = new Size(400, 150);
        StartPosition = FormStartPosition.CenterScreen;
        status.Text = live ? "Live video-only UI test. Production audio settings are unchanged." : "Simulated video UI test — no TV connection.";
        var close = new Button { Text = "Close test", Dock = DockStyle.Bottom, Height = 38 };
        close.Click += (_, _) => Close();
        Controls.Add(status); Controls.Add(close); PrepareLayout();
        Shown += async (_, _) =>
        {
            try
            {
                ProcessStartInfo info;
                if (live)
                {
                    var reading = await Network.Read(tv, CancellationToken.None);
                    if (reading.Power != Power.On) throw new IOException("Live TV unavailable: " + reading.Detail);
                    info = Network.Scrcpy(tv);
                    foreach (var argument in new[] { "--no-audio", "--no-control", "--no-power-on", "--max-size=960", "--window-title=Orca 65 · Live controls check" }) info.ArgumentList.Add(argument);
                }
                else
                {
                    info = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                    info.ArgumentList.Add("--viewer");
                }
                process = Process.Start(info)!;
                Task<string>? output = null, errors = null;
                if (live) { output = process.StandardOutput.ReadToEndAsync(); errors = process.StandardError.ReadToEndAsync(); }
                bar = new StreamRemoteBar(tv, process, (target, owner) =>
                {
                    if (remote is { IsDisposed: false }) { remote.Activate(); return; }
                    remote = new RemoteForm(target) { StartPosition = FormStartPosition.Manual };
                    remote.Shown += (_, _) => owner.PlaceRemote(remote);
                    remote.Show(owner);
                });
                bar.StartTracking();
                await process.WaitForExitAsync();
                if (!bar.IsDisposed) bar.Close();
                status.Text = "Video ended; floating controls closed.";
                if (live && process.ExitCode != 0) status.Text += await errors!;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        remote?.Close(); if (bar is { IsDisposed: false }) bar.Close();
        if (process is not null && !process.HasExited) process.CloseMainWindow();
        base.OnFormClosing(e);
    }
}


