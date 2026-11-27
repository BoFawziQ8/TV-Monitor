using System.Net.Sockets;
using System.Text.Json;

sealed class TvToolsForm : FluentForm
{
    private readonly Tv tv;
    private readonly ListBox apps = new();
    private readonly Label status = new();
    private readonly List<Button> actions = [];
    private readonly Button launch;
    private readonly TextBox playInput = new() { MaxLength = 2048 };
    private bool busy;

    internal TvToolsForm(Tv tv)
    {
        this.tv = tv;
        Icon = Program.AppIcon;
        Text = T("TV Tools");
        StartPosition = FormStartPosition.CenterParent;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = IsArabic;
        ClientSize = new Size(620, 664);
        MinimumSize = new Size(560, 600);
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI Variable Text", 10);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 30, 58, 36, 44, 44, 44, 25, 40, 40, 0, 62 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = tv.Name, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 15, FontStyle.Bold) });
        layout.Controls.Add(new Label
        {
            Text = T("Google Play: enter an app name, package ID or Play Store link. Choose Install on the TV using its remote or the TV remote button below. Google account, compatibility and purchase prompts stay on the TV."),
            Dock = DockStyle.Fill
        });
        playInput.Dock = DockStyle.Fill;
        playInput.PlaceholderText = T("App name, package ID or Google Play link");
        playInput.AccessibleName = T("Google Play app to find");
        layout.Controls.Add(playInput);
        layout.Controls.Add(Row(Button("Find on Google Play", async () =>
        {
            await Network.OpenGooglePlay(tv, PlayStoreRequest.Find(playInput.Text), CancellationToken.None);
            status.Text = T("Google Play opened on the TV. Select Install there; use TV remote below if needed. Then check installation using the app's link or package ID.");
        }, true), Button("Open Google Play", async () =>
        {
            await Network.OpenGooglePlay(tv, null, CancellationToken.None);
            status.Text = T("Google Play opened on the TV. Browse and install using the TV remote.");
        }), Button("Check installation", async () =>
        {
            var package = PlayStoreRequest.PackageFrom(playInput.Text);
            var installed = await Network.GooglePlayAppInstalled(tv, package, CancellationToken.None);
            status.Text = installed ? T("The app is installed for the current TV user. Refresh TV apps to launch it.")
                : T("The app is not installed for the current TV user yet. Finish installation in Google Play, then check again.");
        })));
        layout.Controls.Add(Row(Button("TV remote", () =>
        {
            using var remote = new RemoteForm(tv);
            remote.ShowDialog(this);
            return Task.CompletedTask;
        }), Button("Restore previous Home", async () =>
        {
            status.Text = await Network.RestoreTvSetup(tv, CancellationToken.None);
        }), Button("Install APK", async () =>
        {
            using var file = new OpenFileDialog
            {
                Title = Tf($"Choose an APK to install on {tv.Name}"), Filter = "Android app package (*.apk)|*.apk", CheckFileExists = true
            };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            await Network.InstallApp(tv, file.FileName, CancellationToken.None);
            status.Text = Tf($"{Path.GetFileName(file.FileName)} installed on {tv.Name}.");
        })));

        layout.Controls.Add(Row(Button("Files on TV", () =>
        {
            using var files = new FileManagerForm(tv);
            files.ShowDialog(this);
            return Task.CompletedTask;
        }, true), Button("Memory & storage", () =>
        {
            using var memory = new TvMemoryForm(tv);
            memory.ShowDialog(this);
            return Task.CompletedTask;
        })));
        layout.Controls.Add(new Label { Text = T("Open settings on TV"), Dock = DockStyle.Fill });
        layout.Controls.Add(Row(
            Button("General", () => Settings(TvSettingsPage.General)),
            Button("Display", () => Settings(TvSettingsPage.Display)),
            Button("Sound", () => Settings(TvSettingsPage.Sound)),
            Button("Network", () => Settings(TvSettingsPage.Network)),
            Button("Apps", () => Settings(TvSettingsPage.Apps))));
        launch = Button("Launch selected app", LaunchApp, true);
        layout.Controls.Add(Row(Button("Refresh TV apps", RefreshApps), launch));
        apps.Dock = DockStyle.Fill;
        apps.BackColor = Color.FromArgb(30, 41, 59);
        apps.ForeColor = ForeColor;
        apps.AccessibleName = T("Installed TV apps");
        apps.SelectedIndexChanged += (_, _) => UpdateButtons();
        apps.DoubleClick += (_, _) => _ = Work(LaunchApp);
        layout.Controls.Add(apps);
        status.Dock = DockStyle.Fill;
        status.ForeColor = Color.LightSteelBlue;
        status.Text = T("Turn on the TV, then choose a tool.");
        layout.Controls.Add(status);
        Shown += async (_, _) => await Work(RefreshApps);
        UpdateButtons();
        PrepareLayout();
    }

    private static FlowLayoutPanel Row(params Button[] buttons)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(buttons);
        return row;
    }

    private Button Button(string label, Func<Task> action, bool primary = false)
    {
        var button = new FluentButton
        {
            Text = T(label), AutoSize = true, MinimumSize = new Size(68, 31),
            Padding = new Padding(6, 0, 6, 0), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59),
            ForeColor = ForeColor
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += async (_, _) => await Work(action);
        actions.Add(button);
        return button;
    }

    private async Task Work(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        status.Text = T("Connecting to the TV…");
        UpdateButtons();
        try { await action(); }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or
                                    System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        {
            status.Text = ex is OperationCanceledException
                ? T("The TV command timed out. Reconnect and check the result before retrying.") : ex.Message;
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        foreach (var action in actions) action.Enabled = !busy;
        playInput.Enabled = !busy;
        launch.Enabled = !busy && apps.SelectedItem is TvApp;
    }

    private async Task RefreshApps()
    {
        var components = await Network.ListTvApps(tv, CancellationToken.None);
        apps.Items.Clear();
        apps.Items.AddRange(components.Select(component => new TvApp(component)).ToArray());
        if (apps.Items.Count > 0) apps.SelectedIndex = 0;
        status.Text = components.Length == 0 ? T("No launchable TV apps were reported.")
            : Tf($"{components.Length} TV apps found. Select one to launch it.");
    }

    private async Task Settings(TvSettingsPage page)
    {
        await Network.OpenTvSettings(tv, page, CancellationToken.None);
        status.Text = T("Settings opened on the TV.");
    }

    private async Task LaunchApp()
    {
        if (apps.SelectedItem is not TvApp app) return;
        await Network.LaunchTvApp(tv, app.Component, CancellationToken.None);
        status.Text = Tf($"Opened {app} on the TV.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy) e.Cancel = true;
        base.OnFormClosing(e);
    }

    private sealed record TvApp(string Component)
    {
        public override string ToString() => Network.Friendly(Component.Split('/')[0]);
    }
}
