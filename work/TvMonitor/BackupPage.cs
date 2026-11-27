using System.Text.Json;

sealed class BackupPage : UserControl
{
    private readonly RestorePointStore store = RestorePointStore.Default;
    private readonly Func<Tv[]> devices;
    private readonly Func<AppSettingsSnapshot> captureApp;
    private readonly Func<bool> begin;
    private readonly Action end;
    private readonly Action reload;
    private readonly TextBox name = new() { MaxLength = 100 };
    private readonly ComboBox tvs = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox points = new() { IntegralHeight = false };
    private readonly TextBox preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label status = new();
    private readonly List<Button> buttons = [];
    private bool active;

    internal BackupPage(Func<Tv[]> devices, Func<AppSettingsSnapshot> captureApp, Func<bool> begin, Action end, Action reload)
    {
        this.devices = devices; this.captureApp = captureApp; this.begin = begin; this.end = end; this.reload = reload;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(shell);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        shell.Controls.Add(scroll, 0, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = 590, ColumnCount = 1, RowCount = 8, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 36, 74, 36, 42, 36, 42, 0, 100 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        scroll.Controls.Add(layout);
        layout.Controls.Add(new Label { Text = T("Restore points"), Dock = DockStyle.Fill, Font = new Font("Segoe UI Variable Text", 19, FontStyle.Bold) });
        layout.Controls.Add(new Label
        {
            Text = T("App: TV connections, LG pairing, language and saved Home/screensaver history. Android TV: readable preferences, with supported display, sound, Home and screensaver restore. Excludes firmware, APKs, private app data and Wi-Fi passwords. Keep exported points private."),
            Dock = DockStyle.Fill
        });
        name.Dock = DockStyle.Fill;
        name.PlaceholderText = T("Optional restore point name");
        name.AccessibleName = T("Restore point name");
        layout.Controls.Add(name);
        layout.Controls.Add(Row(
            Button("Create app point", () =>
            {
                var point = store.Save("app", PointName("Manual app point"), captureApp());
                RefreshPoints(point.Id); status.Text = T("App restore point created and verified.");
                return Task.CompletedTask;
            }, true),
            Button("Export selected", Export), Button("Import point", Import)));
        tvs.Dock = DockStyle.Fill;
        tvs.AccessibleName = T("TV to back up or restore");
        layout.Controls.Add(tvs);
        layout.Controls.Add(Row(Button("Create TV point", async () =>
        {
            var tv = AndroidTv();
            var snapshot = await Network.CaptureTvSettings(tv, Report, CancellationToken.None);
            var point = store.Save("android-tv", PointName("TV point") + ": " + tv.Name, snapshot);
            RefreshPoints(point.Id);
            status.Text = Tf($"TV point created: {snapshot.Tables.Sum(t => t.Value.Count)} readable values, {TvRestoreSettings.SupportedCount(snapshot)} supported preferences.");
        }), Button("Restore selected", Restore, true), Button("Refresh points", () => { RefreshPoints(); return Task.CompletedTask; })));
        points.Dock = DockStyle.Fill;
        points.AccessibleName = T("Saved restore points");
        points.SelectedIndexChanged += (_, _) => ShowPreview();
        layout.Controls.Add(points);
        preview.Dock = DockStyle.Fill;
        preview.AccessibleName = T("Restore point details and supported settings");
        layout.Controls.Add(preview);
        status.Dock = DockStyle.Fill;
        status.Text = T("An undo point is saved before every restore. App changes also create automatic points. Android authorization may be needed again on another PC.");
        shell.Controls.Add(status, 0, 1);
        VisibleChanged += (_, _) =>
        {
            if (!Visible || active) return;
            try { RefreshDevices(); RefreshPoints(); }
            catch (Exception ex) when (Handled(ex)) { status.Text = ex.Message; }
        };
    }

    private string PointName(string fallback) => string.IsNullOrWhiteSpace(name.Text) ? T(fallback) : name.Text.Trim();
    private void Report(string text) => status.Text = text;
    private Tv AndroidTv()
    {
        var tv = (tvs.SelectedItem as TvItem)?.Tv;
        if (tv?.Platform != Platform.Android)
            throw new IOException(T("Select a connected Android TV. LG webOS does not expose a full settings restore API in this app."));
        return tv;
    }
    private void RefreshDevices()
    {
        var old = (tvs.SelectedItem as TvItem)?.Tv;
        tvs.Items.Clear();
        foreach (var tv in devices()) tvs.Items.Add(new TvItem(tv));
        if (tvs.Items.Count > 0)
            tvs.SelectedIndex = Math.Max(0, tvs.Items.Cast<TvItem>().ToList().FindIndex(item => item.Tv == old));
    }
    private void RefreshPoints(Guid? select = null)
    {
        select ??= (points.SelectedItem as PointItem)?.Point.Id;
        var saved = store.List(out var damaged);
        points.Items.Clear();
        foreach (var point in saved) points.Items.Add(new PointItem(point));
        if (points.Items.Count > 0)
            points.SelectedIndex = Math.Max(0, points.Items.Cast<PointItem>().ToList().FindIndex(item => item.Point.Id == select));
        else preview.Text = T("No restore points yet. Create an app point or select an Android TV above.");
        if (damaged > 0) status.Text = Tf($"{damaged} unreadable restore points were skipped. Their files were kept.");
    }
    private void ShowPreview()
    {
        if (points.SelectedItem is not PointItem item) return;
        var point = item.Point;
        var text = point.Name + Environment.NewLine + point.CreatedUtc.ToLocalTime().ToString("g") + " · " + point.Version + Environment.NewLine;
        if (point.Kind == "app")
        {
            var snapshot = RestorePointStore.Data<AppSettingsSnapshot>(point);
            var language = snapshot.Files.TryGetValue("language.txt", out var bytes)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bytes)).TrimStart('\uFEFF').Trim() : "en";
            text += T("Restores TV Monitor settings only. The app reopens after restore. Includes these files:") + Environment.NewLine +
                string.Join(", ", snapshot.Files.Keys) + Environment.NewLine + T("Saved language: ") + (language == "ar" ? "العربية" : "English");
        }
        else
        {
            var snapshot = RestorePointStore.Data<TvSettingsSnapshot>(point);
            text += snapshot.Name + " · " + snapshot.Address + Environment.NewLine +
                T("Restores only to the same TV, Android user and firmware. Supported preferences:") + Environment.NewLine +
                string.Join(", ", snapshot.Tables.SelectMany(t => t.Value.Where(v => TvRestoreSettings.Restorable(t.Key, v.Key))
                    .Select(v => t.Key + "/" + v.Key + "=" + v.Value))) +
                (snapshot.Home is null ? "" : Environment.NewLine + "Home=" + snapshot.Home) +
                Environment.NewLine + T("A factory reset can change the TV identity and prevent this restore.") +
                (snapshot.Notes.Length == 0 ? "" : Environment.NewLine + string.Join(" · ", snapshot.Notes));
        }
        preview.Text = text;
    }

    private async Task Restore()
    {
        var point = Selected();
        var target = point.Kind == "app" ? T("TV Monitor settings") : AndroidTv().Name;
        if (MessageBox.Show(FindForm(), Tf($"Restore {target} from this point? Current settings will be saved first so you can undo the restore.") +
            Environment.NewLine + Environment.NewLine + preview.Text,
            T("Confirm restore"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (point.Kind == "app")
        {
            store.RestoreApp(point, captureApp());
            status.Text = T("App settings restored and verified. Reopening TV Monitor…");
            BeginInvoke(reload);
        }
        else
        {
            var safety = await Network.RestoreTvSettings(AndroidTv(), point, store, Report, CancellationToken.None);
            RefreshPoints(safety.Id);
        }
    }
    private SavedPoint Selected() => (points.SelectedItem as PointItem)?.Point ?? throw new IOException(T("Select a restore point first."));
    private Task Export()
    {
        var point = Selected();
        using var dialog = new SaveFileDialog
        {
            Title = T("Export restore point"), Filter = "TV Monitor restore point (*.tvmrestore)|*.tvmrestore",
            FileName = $"TVMonitor-{point.Kind}-{point.CreatedUtc:yyyyMMdd-HHmmss}.tvmrestore", DefaultExt = "tvmrestore", AddExtension = true
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        { store.Export(point, dialog.FileName); status.Text = T("Restore point exported. Keep it private; it can contain pairing keys and TV preferences."); }
        return Task.CompletedTask;
    }
    private Task Import()
    {
        using var dialog = new OpenFileDialog { Title = T("Import restore point"), Filter = "TV Monitor restore point (*.tvmrestore)|*.tvmrestore", CheckFileExists = true };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        { var point = store.Import(dialog.FileName); RefreshPoints(point.Id); status.Text = T("Restore point imported and verified. Nothing has been restored yet."); }
        return Task.CompletedTask;
    }
    private Button Button(string text, Func<Task> action, bool primary = false)
    {
        var button = new FluentButton
        {
            Text = T(text), Accent = primary, Size = new Size(166, 36), Margin = new Padding(0, 0, 6, 0),
            AccessibleName = T(text)
        };
        buttons.Add(button);
        button.Click += async (_, _) =>
        {
            if (active) return;
            if (!begin()) { status.Text = T("Wait for the current action and stop live/audio sessions before backup or restore."); return; }
            active = true;
            foreach (var control in buttons) control.Enabled = false;
            name.Enabled = tvs.Enabled = points.Enabled = false;
            status.Text = T("Working on the restore point…");
            try { await action(); }
            catch (Exception ex) when (Handled(ex)) { status.Text = ex.Message; }
            finally
            {
                active = false; end();
                foreach (var control in buttons) control.Enabled = true;
                name.Enabled = tvs.Enabled = points.Enabled = true;
            }
        };
        return button;
    }
    private static bool Handled(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or FormatException or
        OperationCanceledException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException;
    private static FlowLayoutPanel Row(params Button[] children)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(children);
        return row;
    }
    private sealed record TvItem(Tv Tv) { public override string ToString() => Tv.Name + " · " + Tv.Address; }
    private sealed record PointItem(SavedPoint Point)
    {
        public override string ToString() => Point.CreatedUtc.ToLocalTime().ToString("g") + " · " +
            T(Point.Kind == "app" ? "App" : "Android TV") + " · " + T(Point.Name);
    }
}
