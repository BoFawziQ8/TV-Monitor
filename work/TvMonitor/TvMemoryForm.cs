using System.Net.Sockets;

sealed class TvMemoryForm : FluentForm
{
    private readonly Tv tv;
    private readonly Label storage = new() { Dock = DockStyle.Fill };
    private readonly Label ram = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar diskBar = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar ramBar = new() { Dock = DockStyle.Fill };
    private readonly Button refresh;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private bool released;

    internal TvMemoryForm(Tv tv)
    {
        this.tv = tv; Text = Tf($"{tv.Name} · Memory & storage"); Icon = Program.AppIcon;
        ClientSize = new Size(570, 500); MinimumSize = new Size(440, 500);
        StartPosition = FormStartPosition.CenterParent;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No; RightToLeftLayout = IsArabic;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 48, 72, 22, 20, 72, 22, 52, 46, 0 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = T("TV shared storage (/sdcard/) and working memory (RAM). Values are reported by the TV."), Dock = DockStyle.Fill });
        layout.Controls.Add(storage); layout.Controls.Add(diskBar); layout.Controls.Add(new Label());
        layout.Controls.Add(ram); layout.Controls.Add(ramBar);
        layout.Controls.Add(new Label { Text = T("Storage is space for apps and files. Available RAM includes memory Android can reclaim; it is not space for movies."), Dock = DockStyle.Fill });
        refresh = new FluentButton { Text = T("Refresh memory"), Dock = DockStyle.Fill, Accent = true };
        refresh.Click += async (_, _) => await RefreshReading(); layout.Controls.Add(refresh);
        layout.Controls.Add(status);
        storage.AccessibleName = T("TV storage"); ram.AccessibleName = T("TV RAM");
        storage.Text = ram.Text = T("Not checked yet.");
        Shown += async (_, _) => await RefreshReading();
        PrepareLayout();
    }

    private async Task RefreshReading()
    {
        if (busy) return;
        busy = true; refresh.Enabled = false;
        storage.Text = ram.Text = T("Checking TV memory…");
        diskBar.Value = ramBar.Value = 0; diskBar.Enabled = ramBar.Enabled = false; status.Text = "";
        try
        {
            var reading = await Network.ReadTvMemory(tv, lifetime.Token);
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            DisplayReading(reading);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (!IsDisposed && !lifetime.IsCancellationRequested)
            {
                storage.Text = ram.Text = T("Memory information is unavailable.");
                status.Text = ex is OperationCanceledException ? T("The TV did not respond in time.") : ex.Message;
            }
        }
        finally { busy = false; if (!IsDisposed) refresh.Enabled = true; }
    }
    internal void DisplayReading(TvMemoryReading reading)
    {
        storage.Text = Summary(T("TV storage"), reading.Storage); diskBar.Value = reading.Storage.UsedPercent; diskBar.Enabled = true;
        ram.Text = reading.Ram is { } memory ? Summary(T("TV RAM"), memory) : T("RAM information is unavailable on this TV.");
        ramBar.Value = reading.Ram?.UsedPercent ?? 0; ramBar.Enabled = reading.Ram is not null;
        status.Text = Tf($"Updated: {reading.Checked:HH:mm:ss}") + (reading.Storage.Available < 1L << 30 ? "\n" + T("Low storage: less than 1 GiB available.") : "");
    }
    private static string Summary(string title, TvSpace space) => title + "\n" + Tf($"Available: {TvSpace.Size(space.Available)}   ·   Total: {TvSpace.Size(space.Total)}") + "\n" + Tf($"Used: {TvSpace.Size(space.Used)} ({space.UsedPercent}%)");
    protected override void OnFormClosing(FormClosingEventArgs e) { if (!released) lifetime.Cancel(); base.OnFormClosing(e); }
    protected override void Dispose(bool disposing) { if (disposing && !released) { released = true; lifetime.Cancel(); lifetime.Dispose(); } base.Dispose(disposing); }
}
