using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

static class Program
{
    internal static Icon AppIcon { get; } = LoadIcon();

    private static Icon LoadIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("TvMonitor.ico")!;
        return new Icon(stream);
    }

    [STAThread]
    static void Main(string[] args)
    {
        var startHidden = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        using var instance = new Mutex(true, "Local\\HomeTvMonitor", out var first);
        if (!first)
        {
            // The dashboard hides in the tray; a second launch brings the running one back.
            if (!startHidden)
            {
                var running = FindWindow(null, "TV Monitor");
                if (running != IntPtr.Zero) { ShowWindow(running, 9); SetForegroundWindow(running); }
            }
            return;
        }
        Startup.Refresh();
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        FluentTheme.ReadSystem();
        try { RestorePointStore.Default.RecoverPending(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            MessageBox.Show(T("An interrupted app restore could not be recovered. The recovery journal was kept. ") + ex.Message, "TV Monitor");
        }
        bool reload;
        do
        {
            AppText.ReloadPreference();
            SettingsStore.Reload();
            using var dashboard = new Dashboard(startHidden);
            Application.Run(dashboard);
            reload = dashboard.ReloadLanguage;
        } while (reload);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}

sealed class Dashboard : FluentForm
{
    private static readonly Color Navy = Color.FromArgb(15, 23, 42);
    private static readonly Color Surface = Color.FromArgb(30, 41, 59);
    private static readonly Color TextColor = Color.FromArgb(241, 245, 249);
    private static readonly Color Muted = Color.FromArgb(160, 178, 204);
    private static readonly Color Blue = Color.FromArgb(37, 99, 235);
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTVMonitor", "devices.json");

    private readonly List<Tv> devices;
    private readonly Dictionary<Tv, Reading> readings = [];
    private readonly Dictionary<Tv, Process> viewers = [];
    private readonly HashSet<Tv> openingViewers = [];
    private readonly Dictionary<Tv, StreamRemoteBar> streamControls = [];
    private readonly HashSet<Tv> audioViewers = [];
    private readonly Dictionary<Tv, Task> playbackCaptures = [];
    private readonly Dictionary<Tv, string> playbackStatus = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly TraySwitch tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 12000 };
    private readonly ListView table = new DeviceList();
    private readonly Label selectedTitle = new(), selectedMeta = new();
    private readonly TvMark selectedMark = new();
    private readonly Label summary = new(), details = new(), footer = new();
    private readonly Button watch, pair, message, powerOn, powerOff, restart, edit, remove, add, refresh, discover, remote, audio, install, tools, sendFiles, memory;
    private RemoteForm? remoteWindow;
    private AudioForm? audioWindow;
    private bool closingAudio;
    private bool busy;
    private bool refreshing;
    private bool backupBusy, backupTabActive;
    private bool quitting;
    private readonly bool startHidden;
    internal bool ReloadLanguage { get; private set; }

    internal Dashboard(bool startHidden = false)
    {
        this.startHidden = startHidden;
        SuspendLayout();
        Text = "TV Monitor";
        Icon = Program.AppIcon;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = IsArabic;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1040, 680);
        MinimumSize = new Size(640, 420);
        devices = LoadDevices();
        tray = new TraySwitch(
            () => Selected ?? devices.FirstOrDefault(),
            () => devices.ToArray(),
            SelectTv,
            tv => Network.Control(tv, TvAction.PowerOn, lifetime.Token),
            ShowTrayRemote,
            message =>
            {
                if (IsDisposed || !Visible) return;
                footer.Text = message;
                _ = Work(RefreshStatus);
            },
            ShowDashboard, ExitFromTray);

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24, 16, 24, 12) };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(shell);
        var pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 34, 48, 0, 44 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        pages.Controls.Add(layout);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 194));
        header.Controls.Add(new Label { Text = "TV Monitor", Dock = DockStyle.Fill, Font = new Font("Segoe UI Variable Display", 25, FontStyle.Bold), AutoEllipsis = true });
        var about = MakeButton(T("About"), ShowAbout);
        var language = MakeButton(IsArabic ? "English" : "العربية", () => ChangeLanguage());
        language.AccessibleName = IsArabic ? "Switch to English" : "Switch to Arabic";
        var headerActions = Bar(about, language);
        headerActions.Padding = new Padding(0, 6, 0, 0);
        foreach (var button in new[] { about, language }) { button.Width = 88; button.MinimumSize = Size.Empty; }
        header.Controls.Add(headerActions);
        shell.Controls.Add(header, 0, 0);
        var tvTab = MakeButton(T("TVs"), () => { }, true);
        var instructionsTab = MakeButton(T("Instructions"), () => { });
        var backupTab = MakeButton(T("Backup & restore"), () => { });
        var settingsTab = MakeButton(T("Settings"), () => { });
        var tabs = new[] { tvTab, instructionsTab, backupTab, settingsTab };
        foreach (var tab in tabs) { tab.Width = 136; tab.AccessibleRole = AccessibleRole.PageTab; }
        var navigation = Bar(tabs);
        navigation.AccessibleRole = AccessibleRole.PageTabList;
        navigation.AccessibleName = T("Main tabs");
        shell.Controls.Add(navigation, 0, 1);
        var instructions = new InstructionsPage { Dock = DockStyle.Fill, Visible = false };
        var backups = new BackupPage(() => devices.ToArray(),
            () => RestorePointStore.Default.Capture(devices, IsArabic ? "ar" : "en"),
            BeginBackup, EndBackup, () => { ReloadLanguage = true; Close(); })
            { Dock = DockStyle.Fill, Visible = false };
        var settingsPage = new SettingsPage(() => devices.ToArray(),
            () => TraySwitch.Recall(devices) ?? Selected ?? devices.FirstOrDefault(),
            target => SelectTv(target),
            seconds =>
            {
                timer.Interval = seconds * 1000;
                footer.Text = Tf($"Automatic refresh every {seconds} seconds.");
            },
            ChangeLanguage) { Dock = DockStyle.Fill, Visible = false };
        pages.Controls.Add(instructions);
        pages.Controls.Add(backups);
        pages.Controls.Add(settingsPage);
        shell.Controls.Add(pages, 0, 2);
        tvTab.Click += (_, _) => ShowPage(0);
        instructionsTab.Click += (_, _) => ShowPage(1);
        backupTab.Click += (_, _) => ShowPage(2);
        settingsTab.Click += (_, _) => ShowPage(3);
        void ShowPage(int index)
        {
            backupTabActive = index == 2;
            instructions.Visible = index == 1;
            backups.Visible = index == 2;
            settingsPage.Visible = index == 3;
            layout.Visible = index == 0;
            Control page = index switch { 1 => instructions, 2 => backups, 3 => settingsPage, _ => layout };
            page.BringToFront();
            for (var i = 0; i < tabs.Length; i++)
            {
                ((FluentButton)tabs[i]).Accent = i == index;
                tabs[i].BackColor = i == index ? FluentTheme.Accent : FluentTheme.Surface;
            }
            FluentTheme.Apply(navigation);
            if (index == 1) instructions.FocusTopic();
            if (index == 3) settingsPage.FocusFirst();
        }
        bool BeginBackup()
        {
            if (busy || backupBusy || viewers.Count > 0 || audioWindow is { Active: true } || playbackCaptures.Values.Any(task => !task.IsCompleted)) return false;
            remoteWindow?.Close();
            audioWindow?.Close();
            backupBusy = busy = true;
            language.Enabled = false;
            foreach (var control in new[] { refresh, discover, add }) if (control is not null) control.Enabled = false;
            UpdateSelection();
            return true;
        }
        void EndBackup()
        {
            backupBusy = busy = false;
            language.Enabled = true;
            foreach (var control in new[] { refresh, discover, add }) if (control is not null) control.Enabled = true;
            UpdateSelection();
        }
        StyleLabel(summary, T("Checking saved TVs…"));
        layout.Controls.Add(summary);

        refresh = MakeButton(T("Refresh"), async () => await Work(RefreshStatus));
        discover = MakeButton(T("Scan for TVs"), async () => await Work(Discover), true);
        add = MakeButton(T("Add TV"), () => Edit(null));
        layout.Controls.Add(Bar(discover, add, refresh));
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(body);
        table.Dock = DockStyle.Fill;
        table.MultiSelect = false;
        table.RightToLeft = RightToLeft.No;
        table.RightToLeftLayout = false;
        table.Margin = new Padding(0, 0, 16, 0);
        table.AccessibleName = T("Saved TVs");
        foreach (var name in new[] { "TV", "Power", "Current app / input", "IP address", "Type" }) table.Columns.Add(T(name));
        table.Resize += (_, _) => SizeColumns();
        table.DpiChangedAfterParent += (_, _) => BeginInvoke(SizeColumns);
        table.SelectedIndexChanged += (_, _) => { UpdateSelection(); tray.Remember(); };
        table.DoubleClick += (_, _) => { if (watch?.Enabled == true) StartViewer(); };
        body.Controls.Add(table, 0, 0);

        watch = MakeButton(T("Watch live"), StartViewer, true);
        pair = MakeButton(T("Pair LG"), async () => await Work(Pair));
        message = MakeButton(T("Send message"), async () => await Work(Compose));
        powerOn = MakeButton(T("Power on"), async () => await Work(() => RunAction(TvAction.PowerOn)));
        powerOff = MakeButton(T("Power off"), async () => await Work(() => RunAction(TvAction.PowerOff)));
        restart = MakeButton(T("Restart"), async () => await Work(() => RunAction(TvAction.Restart)));
        edit = MakeButton(T("Edit TV"), () => Edit(Selected));
        remove = MakeButton(T("Remove"), Remove);
        remote = MakeButton(T("Remote"), OpenRemote);
        audio = MakeButton(T("Audio"), OpenAudio);
        install = MakeButton(T("Google Play"), async () => await Work(OpenTvTools));
        tools = MakeButton(T("TV Tools"), async () => await Work(OpenTvTools));
        sendFiles = MakeButton(T("Files on TV"), async () => await Work(() =>
        {
            if (Selected is { Platform: Platform.Android } target)
            {
                using var dialog = new FileManagerForm(target);
                dialog.ShowDialog(this);
            }
            return Task.CompletedTask;
        }));
        ((FluentButton)sendFiles).Glyph = "\uE8B7";
        memory = MakeButton(T("Memory & storage"), async () => await Work(() =>
        {
            if (Selected is { Platform: Platform.Android } target)
            {
                using var dialog = new TvMemoryForm(target);
                dialog.ShowDialog(this);
            }
            return Task.CompletedTask;
        }));
        ((FluentButton)watch).Glyph = "\uE768";
        ((FluentButton)remote).RemoteIcon = true;
        ((FluentButton)audio).Glyph = "\uE767";
        ((FluentButton)tools).Glyph = "\uE713";
        var card = new FluentCard { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16) };
        body.Controls.Add(card, 1, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        card.Controls.Add(scroll);
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(actions);
        selectedTitle.Font = new Font("Segoe UI Variable Text", 17, FontStyle.Bold);
        selectedTitle.AutoSize = false;
        selectedTitle.Dock = DockStyle.Fill;
        selectedTitle.AutoEllipsis = true;
        selectedMeta.Tag = "muted";
        selectedMeta.AutoEllipsis = true;
        var nameRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selectedMark.Dock = DockStyle.Fill;
        selectedMark.Margin = new Padding(0, 0, 10, 0);
        selectedMark.AccessibleName = T("TV type and brand");
        nameRow.Controls.Add(selectedMark, 0, 0);
        nameRow.Controls.Add(selectedTitle, 1, 0);
        AddRow(nameRow, 44);
        AddRow(selectedMeta, 30);
        AddRow(watch, 44);
        AddRow(Heading("Control"), 34);
        AddRow(Grid(remote, audio, message, pair), 88);
        AddRow(Heading("Power"), 34);
        AddRow(Grid(powerOn, powerOff, restart), 88);
        AddRow(Heading("Apps & customization"), 34);
        AddRow(sendFiles, 42);
        AddRow(memory, 42);
        AddRow(Grid(tools, install, edit, remove), 88);
        StyleLabel(details, "");
        AddRow(details, 94);
        StyleLabel(footer, Tf($"Automatic refresh every {SettingsStore.Current.RefreshSeconds} seconds."));
        footer.AutoEllipsis = true;
        layout.Controls.Add(footer);
        Render(TraySwitch.Recall(devices));
        timer.Interval = SettingsStore.Current.RefreshSeconds * 1000;
        timer.Tick += async (_, _) => await Work(RefreshStatus);
        Shown += async (_, _) =>
        {
            SizeColumns();
            try
            {
                var existing = RestorePointStore.Default.List(out _);
                if (!existing.Any(point => point.Kind == "app"))
                    RestorePointStore.Default.Save("app", "Initial app settings", RestorePointStore.Default.Capture(devices, IsArabic ? "ar" : "en"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            { footer.Text = ex.Message; }
            timer.Start(); await Work(RefreshStatus);
        };
        ShowPage(0);
        ResumeLayout(true);
        PrepareLayout();

        void AddRow(Control control, int height)
        {
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, 0, 6);
            actions.Controls.Add(control);
        }
        Label Heading(string text) => new() { Text = T(text), UseMnemonic = false, TextAlign = ContentAlignment.BottomLeft, Font = new Font(Font, FontStyle.Bold) };
        TableLayoutPanel Grid(params Button[] buttons)
        {
            var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Dock = DockStyle.Fill;
                buttons[i].MinimumSize = Size.Empty;
                buttons[i].Margin = new Padding(0, 0, i % 2 == 0 ? 6 : 0, 6);
                grid.Controls.Add(buttons[i], i % 2, i / 2);
            }
            return grid;
        }
    }

    private bool ChangeLanguage()
    {
        if (backupBusy || (busy && !refreshing) || viewers.Count > 0 || audioWindow is { Active: true })
        {
            footer.Text = T("Stop any live or audio session and wait for the current TV action before switching languages.");
            return false;
        }
        try { SwitchLanguage(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            footer.Text = T("Could not save the language. ") + ex.Message;
            return false;
        }
        ReloadLanguage = true;
        Close();
        return true;
    }

    private void ShowAbout()
    {
        using var source = typeof(Dashboard).Assembly.GetManifestResourceStream("TvMonitor.BoFawziLogo")!;
        using var logo = Image.FromStream(source);
        using var dialog = new FluentForm
        {
            Text = T("About TV Monitor"), Icon = Program.AppIcon, ClientSize = new Size(350, 418),
            StartPosition = FormStartPosition.CenterParent,
            RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No, RightToLeftLayout = IsArabic,
            FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false,
            MaximizeBox = false, MinimizeBox = false, BackColor = Navy, ForeColor = TextColor
        };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 204, 34, 22, 24, 32, 32, 36 })
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        dialog.Controls.Add(content);
        content.Controls.Add(new PictureBox
        {
            Image = logo, Anchor = AnchorStyles.None, Size = new Size(168, 194), SizeMode = PictureBoxSizeMode.Zoom,
            AccessibleName = "BoFawzi logo"
        });
        content.Controls.Add(new Label
        {
            Text = "TV Monitor", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 18, FontStyle.Bold)
        });
        content.Controls.Add(new Label
        {
            Text = "Made By BoFawzi", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted
        });
        content.Controls.Add(new Label
        {
            Text = Tf($"Version {Application.ProductVersion}  ·  Created {new DateOnly(2026, 11, 8):yyyy-MM-dd}"),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted
        });
        var website = new LinkLabel
        {
            Text = "bofawzi.blogspot.com", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, RightToLeft = RightToLeft.No,
            AccessibleName = T("Creator website"), TabStop = true
        };
        website.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://bofawzi.blogspot.com/") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(dialog, T("Could not open the website. ") + ex.Message, dialog.Text);
            }
        };
        content.Controls.Add(website);
        var privacy = new LinkLabel
        {
            Text = IsArabic ? "سياسة الخصوصية" : "Privacy policy", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, TabStop = true
        };
        privacy.LinkClicked += (_, _) => { using var policy = new PrivacyPolicyForm(); policy.ShowDialog(dialog); };
        content.Controls.Add(privacy);
        var close = MakeButton(T("Close"), () => dialog.Close(), true);
        close.Anchor = AnchorStyles.None;
        close.Margin = Padding.Empty;
        dialog.AcceptButton = dialog.CancelButton = close;
        content.Controls.Add(close);
        dialog.PrepareLayout();
        dialog.ShowDialog(this);
    }

    private static void StyleLabel(Label label, string text)
    {
        label.Dock = DockStyle.Fill;
        label.Text = text;
        label.ForeColor = Muted;
        label.TextAlign = ContentAlignment.MiddleLeft;
    }

    private static FlowLayoutPanel Bar(params Control[] children)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        bar.Controls.AddRange(children);
        return bar;
    }

    private static Button MakeButton(string text, Action action, bool primary = false)
    {
        var button = new FluentButton
        {
            Text = text, AutoSize = false, Size = new Size(112, 36), MinimumSize = new Size(94, 36), Accent = primary, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Blue : Surface, ForeColor = TextColor, Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(7, 0, 7, 0)
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => action();
        return button;
    }

    private void SizeColumns()
    {
        if (table.Columns.Count == 0) return;
        table.Columns[0].Width = Math.Max(1, table.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        for (var i = 1; i < table.Columns.Count; i++) table.Columns[i].Width = 0;
    }

    private Tv? Selected => table.SelectedItems.Count == 0 ? null : table.SelectedItems[0].Tag as Tv;
    private Reading Get(Tv tv) => readings.GetValueOrDefault(tv) ?? new(Power.Checking, "", T("Waiting for the first check."));

    private void Render(Tv? select = null)
    {
        select ??= Selected;
        table.BeginUpdate();
        table.Items.Clear();
        foreach (var tv in devices)
        {
            var r = Get(tv);
            var label = r.Pair ? T("Pair to check") : r.Power switch
            {
                Power.On => T("On"), Power.Standby => T("Standby"), Power.ScreenOff => T("Screen off"),
                Power.Unknown => T("Unknown"), _ => T("Checking…")
            };
            var row = new ListViewItem(new[] { tv.Name, label, r.Source.Length == 0 ? "—" : r.Source,
                tv.Address, tv.Platform switch { Platform.Android => T("Android TV"), Platform.WebOS => T("LG webOS"), _ => T("Unverified") } })
            { Tag = tv, UseItemStyleForSubItems = false };
            row.SubItems[1].ForeColor = r.Power switch
            {
                Power.On => Color.LightGreen, Power.Standby or Power.ScreenOff => Color.LightSkyBlue, _ => Color.Khaki
            };
            table.Items.Add(row);
            row.Selected = tv == select;
        }
        if (table.SelectedItems.Count == 0 && table.Items.Count > 0) table.Items[0].Selected = true;
        table.EndUpdate();
        SizeColumns();
        int Count(Power power) => devices.Count(tv => Get(tv).Power == power);
        summary.Text = Tf($"{Count(Power.On)} on    ·    {Count(Power.Standby)} standby    ·    {Count(Power.ScreenOff)} screen off    ·    {Count(Power.Unknown)} unknown    ·    {devices.Count} saved");
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (watch is null) return;
        var tv = Selected;
        var reading = tv is null ? null : Get(tv);
        selectedTitle.Text = tv?.Name ?? T("Choose a TV");
        selectedMark.Device = tv;
        var brand = tv is null ? null : TvBrands.Of(tv);
        var kind = tv?.Platform switch { Platform.Android => T("Android TV"), Platform.WebOS => T("LG webOS"), _ => T("Unverified") };
        selectedMeta.Text = tv is null ? T("Select a saved TV to see its controls.")
            : brand is not null && brand.Name is not ("Android TV" or "LG webOS" or "TV")
            ? brand.Name + "  ·  " + kind + "  ·  " + tv.Address
            : tv.Address + "  ·  " + kind;
        details.Text = tv is not null && playbackStatus.TryGetValue(tv, out var captureStatus)
            ? captureStatus : reading?.Detail ?? T("Add a TV by IP address or try Discover TVs.");
        var running = tv is not null && viewers.TryGetValue(tv, out var viewer) && !viewer.HasExited;
        watch.Text = running ? T("Stop live view") : T("Watch live");
        watch.Enabled = running || (tv?.Platform == Platform.Android && reading?.Power == Power.On &&
            !playbackCaptures.ContainsKey(tv));
        pair.Enabled = !busy && tv?.Platform == Platform.WebOS;
        var supported = tv is { Platform: Platform.Android or Platform.WebOS };
        message.Enabled = !busy && supported && reading?.Power == Power.On;
        powerOn.Enabled = !busy && tv is not null && (supported || Network.ValidMac(tv.Mac)) && reading?.Power != Power.On;
        powerOff.Enabled = !busy && supported && reading?.Power is Power.On or Power.ScreenOff;
        restart.Enabled = !busy && tv?.Platform == Platform.Android && reading?.Power == Power.On;
        remote.Enabled = tv is { Platform: Platform.Android } || tv is { Platform: Platform.WebOS, Key: not null };
        audio.Enabled = tv is not null;
        tools.Enabled = !busy && tv?.Platform == Platform.Android;
        install.Enabled = !busy && tv?.Platform == Platform.Android;
        sendFiles.Enabled = !busy && tv?.Platform == Platform.Android;
        memory.Enabled = !busy && tv?.Platform == Platform.Android;
        edit.Enabled = remove.Enabled = !busy && tv is not null && !running;
        if (backupBusy) watch.Enabled = remote.Enabled = audio.Enabled = false;
        tray.RefreshText();
    }

    private void SelectTv(Tv tv)
    {
        var row = table.Items.Cast<ListViewItem>().FirstOrDefault(item => item.Tag is Tv other && other == tv);
        if (row is null) { Render(tv); return; }
        row.Selected = true;
        row.Focused = true;
        table.EnsureVisible(row.Index);
        tray.Remember();
    }

    private void ShowDashboard()
    {
        if (IsDisposed) return;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _ = Work(RefreshStatus);
    }

    private void ExitFromTray()
    {
        quitting = true;
        Close();
    }

    private void HideToTray()
    {
        Hide();
        tray.RemindInTray();
    }

    private async Task Work(Func<Task> action)
    {
        if (busy || backupBusy || lifetime.IsCancellationRequested || (backupTabActive && action == RefreshStatus)) return;
        busy = true;
        refreshing = action == RefreshStatus;
        refresh.Enabled = discover.Enabled = add.Enabled = false;
        UpdateSelection();
        try { await action(); }
        catch (Exception ex) when (ex is IOException or SocketException or System.Net.WebSockets.WebSocketException or
                                    UnauthorizedAccessException or OperationCanceledException or JsonException or
                                    System.ComponentModel.Win32Exception or System.Net.NetworkInformation.NetworkInformationException)
        {
            if (!lifetime.IsCancellationRequested)
                footer.Text = ex is OperationCanceledException ? T("Timed out. Check the TV and try again.") : ex.Message;
        }
        finally
        {
            busy = false;
            refreshing = false;
            if (!lifetime.IsCancellationRequested)
            {
                refresh.Enabled = discover.Enabled = add.Enabled = true;
                UpdateSelection();
            }
        }
    }

    private async Task RefreshStatus()
    {
        footer.Text = T("Checking TV power and current apps…");
        var current = devices.ToArray();
        var results = await Task.WhenAll(current.Select(tv => Network.Read(tv, lifetime.Token)));
        if (lifetime.IsCancellationRequested) return;
        var learn = current.Select(async (tv, i) => tv.Mac is null && !viewers.ContainsKey(tv)
            ? await Task.Run(() => Network.LearnMac(tv.Address), lifetime.Token) : null);
        var macs = await Task.WhenAll(learn);
        if (lifetime.IsCancellationRequested) return;
        var changed = false;
        var selected = Selected;
        for (var i = 0; i < current.Length; i++)
        {
            var tv = current[i];
            var updated = tv;
            if (Network.ValidMac(macs[i]) && !viewers.ContainsKey(tv)) updated = updated with { Mac = macs[i] };
            if (updated.Brand is null && TvBrands.KeyFor(results[i].Maker) is { } brand) updated = updated with { Brand = brand };
            if (updated != tv)
            {
                devices[i] = updated;
                readings.Remove(tv);
                if (selected == tv) selected = updated;
                tv = updated;
                changed = true;
            }
            readings[tv] = results[i];
        }
        if (changed) SaveDevices();
        if (audioWindow is { } window && devices.FirstOrDefault(window.Matches) is { } audioTv)
            window.UpdateReading(Get(audioTv));
        Render(selected);
        footer.Text = Tf($"Updated {DateTime.Now:t}  ·  Refreshes every {SettingsStore.Current.RefreshSeconds} seconds");
    }

    private async Task Discover()
    {
        footer.Text = T("Scanning your local network for Wi-Fi and wired TVs…");
        var found = await Network.Discover(lifetime.Token, devices.ToArray());
        if (lifetime.IsCancellationRequested) return;
        var added = 0;
        var identified = 0;
        foreach (var tv in found)
        {
            var existing = devices.FindIndex(saved => saved.Address == tv.Address ||
                (Network.ValidMac(tv.Mac) && Network.MacKey(saved.Mac) == Network.MacKey(tv.Mac)));
            if (existing < 0)
            {
                devices.Add(tv);
                added++;
            }
            else if ((devices[existing].Address != tv.Address || devices[existing].Port != tv.Port) &&
                Network.ValidMac(tv.Mac) && Network.MacKey(devices[existing].Mac) == Network.MacKey(tv.Mac) &&
                !viewers.ContainsKey(devices[existing]) && !AudioInUse(devices[existing]))
            {
                var saved = devices[existing]; readings.Remove(saved);
                devices[existing] = saved with { Address = tv.Address, Port = tv.Port };
                identified++;
            }
            else if (devices[existing].Platform == Platform.Other && tv.Platform != Platform.Other)
            {
                var saved = devices[existing];
                readings.Remove(saved);
                devices[existing] = saved with { Platform = tv.Platform, Port = tv.Port };
                identified++;
            }
            else if (devices[existing].Brand is null && tv.Brand is not null)
            {
                var saved = devices[existing];
                readings.Remove(saved);
                devices[existing] = saved with { Brand = tv.Brand };
                identified++;
            }
        }
        if (added + identified > 0) SaveDevices();
        await RefreshStatus();
        if (!lifetime.IsCancellationRequested)
            footer.Text = Tf($"Scan found {found.Count} devices · {added} added · {identified} identified. Use Add TV for a known IP.");
    }

    private async Task Pair()
    {
        if (Selected is not { Platform: Platform.WebOS } tv) return;
        CloseRemote(tv);
        footer.Text = T("Approve the TV Monitor prompt on the LG screen. Waiting up to 45 seconds…");
        var (reading, key) = await Network.WebOs(tv, true, lifetime.Token);
        if (lifetime.IsCancellationRequested) return;
        if (!string.IsNullOrEmpty(key))
        {
            var paired = tv with { Key = key };
            devices[devices.IndexOf(tv)] = paired;
            readings.Remove(tv);
            tv = paired;
            SaveDevices();
        }
        readings[tv] = reading;
        Render(tv);
        footer.Text = string.IsNullOrEmpty(key) ? reading.Detail : T("LG paired. The dashboard will refresh automatically.");
    }

    private void OpenRemote()
    {
        if (Selected is { } tv) OpenRemote(tv, null);
    }

    // The tray may open the remote while the dashboard is hidden, so it owns itself then.
    private void ShowTrayRemote()
    {
        if (Selected is { } tv) OpenRemote(tv, null, standalone: true);
    }

    private void OpenRemote(Tv tv, StreamRemoteBar? live, bool standalone = false)
    {
        if (backupBusy || live is { IsDisposed: true }) return;
        Form? owner = live;
        if (owner is null && !standalone) owner = this;
        if (remoteWindow is { IsDisposed: false } && remoteWindow.Matches(tv))
        {
            if (remoteWindow.WindowState == FormWindowState.Minimized) remoteWindow.WindowState = FormWindowState.Normal;
            if (owner is not null) remoteWindow.Owner = owner;
            if (live is not null) live.PlaceRemote(remoteWindow);
            remoteWindow.Show();
            remoteWindow.Activate();
            return;
        }
        remoteWindow?.Close();
        var window = new RemoteForm(tv);
        if (live is not null)
        {
            window.StartPosition = FormStartPosition.Manual;
            window.Shown += (_, _) => live.PlaceRemote(window);
        }
        remoteWindow = window;
        window.FormClosed += (_, _) => { if (remoteWindow == window) remoteWindow = null; };
        if (owner is null) window.Show();
        else window.Show(owner);
    }

    private void AttachStreamControls(Tv tv, Process process)
    {
        if (streamControls.Remove(tv, out var previous) && !previous.IsDisposed) previous.Close();
        var controls = new StreamRemoteBar(tv, process, (target, bar) => OpenRemote(target, bar));
        streamControls[tv] = controls;
        controls.FormClosed += (_, _) =>
        {
            if (streamControls.GetValueOrDefault(tv) == controls) streamControls.Remove(tv);
        };
        controls.StartTracking();
        if (SettingsStore.Current.MouseControl) new LiveMouseHint(process).Show();
    }

    private void CloseRemote(Tv tv)
    {
        if (remoteWindow?.Matches(tv) == true) remoteWindow.Close();
    }

    private void OpenAudio()
    {
        if (Selected is not { } tv) return;
        if (audioWindow?.Matches(tv) == true)
        {
            audioWindow.Activate();
            return;
        }
        if (audioWindow is { Active: true })
        {
            footer.Text = T("Stop the current audio session before switching TVs.");
            audioWindow.Activate();
            return;
        }
        audioWindow?.Close();
        var window = new AudioForm(tv, Get(tv), () => audioViewers.Any(other =>
            other.Address == tv.Address && other.Port == tv.Port));
        audioWindow = window;
        window.FormClosed += (_, _) => { if (audioWindow == window) audioWindow = null; };
        window.Show(this);
    }

    private async Task OpenTvTools()
    {
        if (Selected is not { Platform: Platform.Android } tv) return;
        using var dialog = new TvToolsForm(tv);
        dialog.ShowDialog(this);
        await RefreshStatus();
    }

    private async Task Compose()
    {
        if (Selected is not { } tv) return;
        using var dialog = new MessageDialog(tv);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        footer.Text = Tf($"Sending a message to {tv.Name}…");
        try { footer.Text = await Network.MessageTv(tv, dialog.Message, lifetime.Token); }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, T("Message could not be confirmed")); }
    }

    private async Task RunAction(TvAction action)
    {
        if (Selected is not { } tv) return;
        footer.Text = action == TvAction.Restart ? Tf($"Restarting {tv.Name} and waiting for it to reconnect…") :
            Tf($"Checking {tv.Name} after the power command…");
        try
        {
            var result = await Network.Control(tv, action, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            await RefreshStatus();
            if (!lifetime.IsCancellationRequested) footer.Text = result;
        }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, T("TV action could not be confirmed")); }
    }

    private void Edit(Tv? existing)
    {
        if (existing is not null && AudioInUse(existing)) return;
        using var dialog = new DeviceDialog(existing);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Value is not { } tv) return;
        if (devices.Any(other => other != existing && other.Address == tv.Address && other.Port == tv.Port))
        {
            MessageBox.Show(this, T("A TV is already saved at that address and port."), Text);
            return;
        }
        if (existing is null) devices.Add(tv);
        else
        {
            CloseRemote(existing);
            if (audioWindow?.Matches(existing) == true) audioWindow.Close();
            devices[devices.IndexOf(existing)] = tv;
            readings.Remove(existing);
        }
        SaveDevices();
        Render(tv);
        _ = Work(RefreshStatus);
    }

    private void Remove()
    {
        if (Selected is not { } tv) return;
        if (AudioInUse(tv)) return;
        CloseRemote(tv);
        if (audioWindow?.Matches(tv) == true) audioWindow.Close();
        devices.Remove(tv);
        readings.Remove(tv);
        SaveDevices();
        Render();
    }

    private bool AudioInUse(Tv tv)
    {
        if (audioWindow is not { Active: true } window || !window.Matches(tv)) return false;
        footer.Text = T("Stop this TV’s audio session first.");
        window.Activate();
        return true;
    }

    private async void StartViewer()
    {
        if (Selected is not { } tv) return;
        if (viewers.TryGetValue(tv, out var current) && !current.HasExited)
        {
            if (!current.CloseMainWindow()) current.Kill();
            return;
        }
        if (tv.Platform != Platform.Android || playbackCaptures.ContainsKey(tv) || !openingViewers.Add(tv)) return;
        try
        {
            // scrcpy needs a live ADB session; the last status check may predate a dropped connection.
            footer.Text = Tf($"Checking {tv.Name} before opening the live view…");
            var reading = await Network.Read(tv, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            readings[tv] = reading;
            Render(tv);
            if (reading.Power != Power.On)
            {
                footer.Text = reading.Detail;
                return;
            }
            var live = SettingsStore.Current;
            var audioAvailable = live.Sound != TvSound.Off && !(audioWindow?.Matches(tv) == true && audioWindow.Active);
            var withAudio = reading.Sdk >= 30 && audioAvailable;
            var withPlaybackCapture = reading.Sdk == 29 && audioAvailable;
            var info = Network.Scrcpy(tv);
            var (maxSize, maxFps, bitRate) = SettingsStore.Profile(live.Quality);
            foreach (var arg in new[] { "--window-title=" + tv.Name + " · Live", "--no-power-on",
                $"--max-size={maxSize}", $"--max-fps={maxFps}", $"--video-bit-rate={bitRate}",
                live.MouseControl ? "--mouse=uhid" : "--mouse=disabled", "--keyboard=disabled", "--gamepad=disabled" })
                info.ArgumentList.Add(arg);
            if (withAudio) Network.AddScrcpyAudio(info, reading.Sdk);
            else info.ArgumentList.Add("--no-audio");
            try
            {
                var process = Process.Start(info) ?? throw new IOException(T("Could not open scrcpy."));
                viewers[tv] = process;
                if (withAudio || withPlaybackCapture) audioViewers.Add(tv);
                footer.Text = withPlaybackCapture ? Tf($"Opening {tv.Name}. Approve audio sharing on the TV when prompted.")
                    : withAudio
                    ? reading.Sdk >= 33 ? Tf($"Opening {tv.Name} with audio…") : Tf($"Opening {tv.Name} with audio routed to the PC…")
                    : Tf($"Opening {tv.Name} in video-only mode…");
                UpdateSelection();
                _ = TrackViewer(tv, process, withPlaybackCapture);
                AttachStreamControls(tv, process);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
                footer.Text = ex.Message;
            }
        }
        finally { openingViewers.Remove(tv); }
    }

    private async Task TrackViewer(Tv tv, Process process, bool withPlaybackCapture)
    {
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if (withPlaybackCapture) playbackCaptures[tv] = PlayCapture(tv, cancellation.Token);
            await process.WaitForExitAsync();
            if (streamControls.TryGetValue(tv, out var controls) && controls.Matches(process))
            {
                streamControls.Remove(tv);
                if (!controls.IsDisposed) controls.Close();
            }
            cancellation.Cancel();
            if (withPlaybackCapture)
            {
                await playbackCaptures[tv];
                playbackCaptures.Remove(tv);
                playbackStatus.Remove(tv);
            }
            var log = (await output) + "\n" + (await errors);
            if (viewers.GetValueOrDefault(tv) == process)
            {
                viewers.Remove(tv);
                audioViewers.Remove(tv);
            }
            if (lifetime.IsCancellationRequested) return;
            footer.Text = process.ExitCode == 0 ? Tf($"{tv.Name} live view closed.") :
                log.Split('\n', StringSplitOptions.TrimEntries).LastOrDefault(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                ?? T("Live view closed unexpectedly. Refresh and check ADB authorization.");
            UpdateSelection();
        }
    }

    private async Task PlayCapture(Tv tv, CancellationToken ct)
    {
        void Show(string message)
        {
            playbackStatus[tv] = message;
            if (!IsDisposed && !ct.IsCancellationRequested) UpdateSelection();
        }

        Show(T("Preparing Android 10 playback audio…"));
        try
        {
            await using var capture = await Network.OpenPlaybackCapture(tv,
                () => Show(T("On the TV, allow audio access if asked, then choose Start now to share TV sound.")), ct);
            Show(T("TV audio connected. Play a clip to check the sound."));
            await PlaybackAudio.Play(capture.Stream, ct,
                () => Show(T("Receiving TV sound on Windows. Closing the live view stops audio sharing.")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or
                                    System.ComponentModel.Win32Exception or NAudio.MmException)
        {
            Show(ex is OperationCanceledException ? T("A TV setup command timed out. Reopen Watch live to retry.") : ex.Message);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (backupBusy)
        {
            e.Cancel = true;
            MessageBox.Show(this, T("Wait for the backup or restore operation to finish before closing."), Text);
            return;
        }
        if (audioWindow is { Active: true } || playbackCaptures.Values.Any(capture => !capture.IsCompleted))
        {
            e.Cancel = true;
            if (!closingAudio) _ = CloseAfterAudio();
            return;
        }
        // The tray icon is the app's switch, so a window close keeps the app running
        // unless the app itself is quitting or Windows is ending the session.
        if (!quitting && !ReloadLanguage &&
            e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing or CloseReason.ApplicationExitCall))
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        timer.Stop();
        lifetime.Cancel();
        remoteWindow?.Close();
        audioWindow?.Close();
        foreach (var controls in streamControls.Values.ToArray()) if (!controls.IsDisposed) controls.Close();
        streamControls.Clear();
        foreach (var viewer in viewers.Values)
            try { if (!viewer.HasExited && !viewer.CloseMainWindow()) viewer.Kill(); }
            catch (InvalidOperationException) { }
        base.OnFormClosing(e);
    }

    private async Task CloseAfterAudio()
    {
        closingAudio = true;
        lifetime.Cancel();
        if (audioWindow is { } window) await window.Shutdown();
        await Task.WhenAll(playbackCaptures.Values.ToArray());
        closingAudio = false;
        if (!IsDisposed) { quitting = true; Close(); }
    }

    private bool wasHidden;
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startHidden) Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (IsDisposed) return;
        if (Visible)
        {
            timer.Start();
            if (wasHidden) { wasHidden = false; _ = Work(RefreshStatus); }
        }
        else
        {
            wasHidden = true;
            timer.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tray.Dispose();
        base.Dispose(disposing);
    }

    private List<Tv> LoadDevices()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<List<Tv>>(File.ReadAllText(ConfigPath));
                if (saved is not null && saved.All(tv => tv is not null && !string.IsNullOrWhiteSpace(tv.Name) &&
                    TvAddress.Valid(tv.Address) &&
                    tv.Port is >= 1 and <= 65535 && Enum.IsDefined(tv.Platform))) return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(T("Saved TV settings could not be loaded. ") + ex.Message, Text);
        }
        #if STORE_BUILD
        return [];
        #else
        return
        [
            new("Basement Skyworth", "192.168.1.28", 5555, Platform.Android, Brand: "skyworth"),
            new("Orca 65", "192.168.1.46", 5555, Platform.Android, Brand: "orca"),
            new("LG C1", "192.168.1.249", 3000, Platform.WebOS, Brand: "lg"),
            new("Wansa", "192.168.0.236", 5555, Platform.Other, Brand: "wansa")
        ];
        #endif
    }

    private void SaveDevices()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var temp = ConfigPath + ".tmp";
            var json = JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true });
            RestorePointStore.Default.BeforeConfigChange("devices.json", System.Text.Encoding.UTF8.GetBytes(json));
            File.WriteAllText(temp, json);
            File.Move(temp, ConfigPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, T("The TV list changed for this session. Windows could not save it. ") + ex.Message, Text);
        }
    }
}

sealed class MessageDialog : FluentForm
{
    private readonly TextBox body = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 400 };
    internal string Message => body.Text.Trim();

    internal MessageDialog(Tv tv)
    {
        Text = T("Send message");
        Icon = Program.AppIcon;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = IsArabic;
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 245);
        MaximizeBox = MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = Tf($"To {tv.Name}"), Dock = DockStyle.Fill });
        body.Dock = DockStyle.Fill;
        body.AccessibleName = T("Message to display on the TV");
        layout.Controls.Add(body);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = tv.Platform == Platform.Android
                ? T("The first send installs a small popup helper. The message may pause playback and closes after 30 seconds.")
                : T("The TV may display a brief notification if it declines a message box.")
        });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var send = new FluentButton { Text = T("Send"), AutoSize = true, Accent = true, DialogResult = DialogResult.OK, Enabled = false };
        var cancel = new FluentButton { Text = T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { cancel, send });
        layout.Controls.Add(buttons);
        CancelButton = cancel;
        body.TextChanged += (_, _) => send.Enabled = Message.Length > 0;
        ActiveControl = body;
        PrepareLayout();
    }
}

