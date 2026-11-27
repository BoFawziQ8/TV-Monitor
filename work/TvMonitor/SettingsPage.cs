using System.Diagnostics;

sealed class SettingsPage : UserControl
{
    private static readonly int[] RefreshOptions = [6, 12, 30, 60];
    private readonly Func<Tv[]> devices;
    private readonly Func<Tv?> trayTv;
    private readonly Action<Tv> chooseTv;
    private readonly Action<int> applyRefresh;
    private readonly Func<bool> changeLanguage;
    private readonly List<Tv> trayDevices = [];
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, AutoEllipsis = true };
    private readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox tray = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox refresh = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox sound = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox mouse = new() { Text = T("The PC mouse controls the TV"), AutoSize = false };
#if !STORE_BUILD
    private readonly CheckBox startup = new() { Text = T("Start with Windows"), AutoSize = false };
#endif
    private bool loading;

    internal SettingsPage(Func<Tv[]> devices, Func<Tv?> trayTv, Action<Tv> chooseTv,
        Action<int> applyRefresh, Func<bool> changeLanguage)
    {
        this.devices = devices; this.trayTv = trayTv; this.chooseTv = chooseTv;
        this.applyRefresh = applyRefresh; this.changeLanguage = changeLanguage;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(shell);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        shell.Controls.Add(scroll, 0, 0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(layout);

        AddHeading(T("Settings"), 40, 0);
        var description = new Label
        {
            Text = T("Language, the tray switch TV, Windows sign-in, refresh timing and live view defaults. Changes are saved immediately."),
            Dock = DockStyle.Fill, Margin = Padding.Empty, AutoEllipsis = true
        };
        description.Tag = "muted";
        AddWide(description, 46);

        foreach (var option in new[] { "English", "العربية" }) language.Items.Add(option);
        language.AccessibleName = T("Language");
        language.SelectedIndexChanged += (_, _) =>
        {
            if (loading || language.SelectedIndex < 0) return;
            if ((language.SelectedIndex == 1) == IsArabic) return;
            if (!changeLanguage())
            {
                loading = true;
                language.SelectedIndex = IsArabic ? 1 : 0;
                loading = false;
            }
        };
        AddRow(T("Language"), language, 42);

        tray.AccessibleName = T("TV for the tray switch");
        tray.SelectedIndexChanged += (_, _) =>
        {
            if (loading || tray.SelectedIndex < 0 || tray.SelectedIndex >= trayDevices.Count) return;
            var target = trayDevices[tray.SelectedIndex];
            chooseTv(target);
            status.Text = Tf($"The tray switch now turns on {target.Name}.");
        };
        AddRow(T("TV for the tray switch"), tray, 42);

#if !STORE_BUILD
        startup.AccessibleName = T("Start with Windows");
        startup.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            try
            {
                Startup.Set(startup.Checked);
                status.Text = startup.Checked
                    ? T("TV Monitor will open quietly in the tray when you sign in to Windows.")
                    : T("TV Monitor will no longer start with Windows.");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                loading = true;
                startup.Checked = Startup.Enabled;
                loading = false;
                status.Text = T("The startup setting could not be saved.");
            }
        };
        AddWide(startup, 38);
#endif

        foreach (var option in RefreshOptions) refresh.Items.Add(Seconds(option));
        refresh.AccessibleName = T("Refresh every");
        refresh.SelectedIndexChanged += (_, _) =>
        {
            if (loading || refresh.SelectedIndex < 0) return;
            var seconds = RefreshOptions[refresh.SelectedIndex];
            if (SettingsStore.Save(SettingsStore.Current with { RefreshSeconds = seconds }, out var error))
            {
                applyRefresh(seconds);
                status.Text = Tf($"Checking every {seconds} seconds now.");
            }
            else
            {
                status.Text = T("Could not save settings. ") + error;
                RefreshValues();
            }
        };
        AddRow(T("Refresh every"), refresh, 42);

        AddHeading(T("Live view"), 44, 8);
        foreach (var option in new[] { T("Low"), T("Balanced"), T("High") }) quality.Items.Add(option);
        quality.AccessibleName = T("Video quality");
        quality.SelectedIndexChanged += (_, _) =>
        {
            if (loading || quality.SelectedIndex < 0) return;
            Save(SettingsStore.Current with { Quality = (LiveQuality)quality.SelectedIndex });
        };
        AddRow(T("Video quality"), quality, 42);
        foreach (var option in new[] { T("Auto"), T("Off") }) sound.Items.Add(option);
        sound.AccessibleName = T("TV sound");
        sound.SelectedIndexChanged += (_, _) =>
        {
            if (loading || sound.SelectedIndex < 0) return;
            Save(SettingsStore.Current with { Sound = (TvSound)sound.SelectedIndex });
        };
        AddRow(T("TV sound"), sound, 42);
        mouse.AccessibleName = T("The PC mouse controls the TV");
        mouse.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            Save(SettingsStore.Current with { MouseControl = mouse.Checked });
        };
        AddWide(mouse, 38);

        var folder = new FluentButton { Text = T("Open settings folder"), Size = new Size(190, 36), Margin = new Padding(0, 0, 8, 0) };
        folder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(RestorePointStore.DefaultRoot);
                Process.Start(new ProcessStartInfo("explorer.exe", RestorePointStore.DefaultRoot) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { status.Text = ex.Message; }
        };
        var folderBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        folderBar.Controls.Add(folder);
        AddWide(folderBar, 46);
        var note = new Label
        {
            Text = T("Live view changes apply the next time a TV is opened. Refresh changes apply now."),
            Dock = DockStyle.Fill, Margin = Padding.Empty, AutoEllipsis = true
        };
        note.Tag = "muted";
        AddWide(note, 40);
        status.Tag = "muted";
        shell.Controls.Add(status, 0, 1);
        VisibleChanged += (_, _) => { if (Visible) RefreshValues(); };

        void AddHeading(string text, int height, int top)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label
            {
                Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, top, 0, 0), UseMnemonic = false,
                Font = new Font("Segoe UI Variable Text", text == T("Settings") ? 19 : 14, FontStyle.Bold)
            });
        }

        void AddRow(string label, Control control, int height)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label
            {
                Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
            });
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 5, 0, 5);
            row.Controls.Add(control, 1, 0);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(row);
        }

        void AddWide(Control control, int height)
        {
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, 0, 6);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(control);
        }
    }

    internal void FocusFirst() => language.Focus();

    private bool Save(AppSettings next)
    {
        if (SettingsStore.Save(next, out var error))
        {
            status.Text = T("Settings saved.");
            return true;
        }
        status.Text = T("Could not save settings. ") + error;
        RefreshValues();
        return false;
    }

    private static string Seconds(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + T("seconds");

    private void RefreshValues()
    {
        loading = true;
        try
        {
            language.SelectedIndex = IsArabic ? 1 : 0;
            trayDevices.Clear();
            tray.Items.Clear();
            foreach (var tv in devices()) { trayDevices.Add(tv); tray.Items.Add(tv.Name); }
            if (trayDevices.Count == 0) { tray.Items.Add(T("No TVs saved yet.")); tray.SelectedIndex = 0; tray.Enabled = false; }
            else
            {
                var current = trayTv();
                var index = current is null ? -1 : trayDevices.IndexOf(current);
                tray.SelectedIndex = index >= 0 ? index : 0;
                tray.Enabled = true;
            }
            var settings = SettingsStore.Current;
            var nearest = Array.FindIndex(RefreshOptions, value => value >= settings.RefreshSeconds);
            refresh.SelectedIndex = nearest < 0 ? RefreshOptions.Length - 1 : nearest;
            quality.SelectedIndex = (int)settings.Quality;
            sound.SelectedIndex = (int)settings.Sound;
            mouse.Checked = settings.MouseControl;
#if !STORE_BUILD
            startup.Checked = Startup.Enabled;
#endif
            status.Text = "";
        }
        finally { loading = false; }
    }
}
