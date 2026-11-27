using System.Net;
using System.Net.Sockets;

sealed class DeviceDialog : FluentForm
{
    private readonly TextBox name = new(), address = new(), mac = new();
    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox brand = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535 };
    internal Tv? Value { get; private set; }

    internal DeviceDialog(Tv? existing)
    {
        Text = existing is null ? T("Add a TV") : T("Edit TV");
        Icon = Program.AppIcon;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = IsArabic;
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 340);
        MaximizeBox = MinimizeBox = false;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(grid);
        AddRow(T("TV name"), name);
        AddRow(T("IP address"), address);
        address.MaxLength = 64;
        address.PlaceholderText = T("IPv4 or IPv6 address");
        address.AccessibleName = T("IP address");
        address.Font = new Font("Consolas", 10);
        AddRow(T("Type"), kind);
        AddRow(T("Brand"), brand);
        AddRow(T("Port"), port);
        AddRow(T("Wake MAC"), mac);
        AddRow(T("IPv6 adapter"), adapter);
        adapter.Items.Add(T("Select adapter for link-local IPv6"));
        try { foreach (var scope in TvAddress.Scopes()) adapter.Items.Add(scope); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        adapter.SelectedIndex = adapter.Items.Count == 2 ? 1 : 0;
        address.TextChanged += (_, _) => UpdateAdapter();
        address.RightToLeft = mac.RightToLeft = port.RightToLeft = RightToLeft.No;
        mac.PlaceholderText = T("Optional • TV network MAC address");
        mac.MaxLength = 64;
        mac.AccessibleName = T("Wake MAC");
        mac.Text = existing?.Mac ?? "";
        kind.Items.AddRange(new[] { T("Android TV / Google TV"), T("LG webOS"), T("Other / unknown") });
        kind.SelectedIndex = (int)(existing?.Platform ?? Platform.Android);
        brand.AccessibleName = T("Brand");
        brand.Items.Add(T("Auto (detect)"));
        foreach (var known in TvBrands.Selectable) brand.Items.Add(known.Name);
        var savedBrand = TvBrands.Selectable.Select((known, index) => (known, index)).FirstOrDefault(item => item.known.Key == existing?.Brand);
        brand.SelectedIndex = savedBrand.known is null ? 0 : savedBrand.index + 1;
        port.Value = existing?.Port ?? 5555;
        kind.SelectedIndexChanged += (_, _) => port.Value = kind.SelectedIndex == (int)Platform.WebOS ? 3000 : 5555;
        name.Text = existing?.Name ?? "";
        name.MaxLength = 80;
        address.Text = existing?.Address ?? "";
        if (IPAddress.TryParse(address.Text, out var saved) && saved.AddressFamily == AddressFamily.InterNetworkV6 && saved.ScopeId > 0 && saved.ScopeId <= int.MaxValue)
        {
            var matching = adapter.Items.OfType<TvNetworkScope>().FirstOrDefault(s => s.Index == saved.ScopeId);
            if (matching is null)
            {
                matching = new((int)saved.ScopeId, T("Saved adapter (not currently connected)"));
                adapter.Items.Add(matching);
            }
            adapter.SelectedItem = matching;
        }
        adapter.SelectedIndexChanged += (_, _) =>
        {
            if (adapter.SelectedItem is TvNetworkScope selected && IPAddress.TryParse(address.Text, out var ip) && ip.IsIPv6LinkLocal && ip.ScopeId != 0 && ip.ScopeId != selected.Index)
            { ip.ScopeId = selected.Index; address.Text = ip.ToString(); }
        };
        UpdateAdapter();
        var ok = new FluentButton { Text = T("Save"), AutoSize = true, Accent = true };
        var cancel = new FluentButton { Text = T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        grid.Controls.Add(buttons, 0, 7);
        grid.SetColumnSpan(buttons, 2);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                MessageBox.Show(this, T("Enter a TV name and a valid IPv4 or IPv6 address."), Text);
                return;
            }
            IPAddress ip;
            try { ip = TvAddress.Parse(address.Text, (adapter.SelectedItem as TvNetworkScope)?.Index ?? 0); }
            catch (IOException ex) { MessageBox.Show(this, ex.Message, Text); return; }
            var platform = (Platform)kind.SelectedIndex;
            if (platform == Platform.WebOS && port.Value == 3001)
            {
                MessageBox.Show(this, T("Enter the LG webOS local WebSocket port, usually 3000."), Text);
                return;
            }
            var key = existing?.Address == ip.ToString() && existing.Port == (int)port.Value &&
                existing.Platform == platform ? existing.Key : null;
            var wake = mac.Text.Trim();
            if (IPAddress.TryParse(wake, out var mistaken) && mistaken.AddressFamily == AddressFamily.InterNetworkV6)
            {
                MessageBox.Show(this, T("This is an IPv6 address. Enter it in IP address, not Wake MAC."), Text);
                return;
            }
            if (existing?.Address != ip.ToString() && wake == existing?.Mac) wake = "";
            if (wake.Length > 0 && !Network.ValidMac(wake))
            {
                MessageBox.Show(this, T("Enter the TV’s MAC address, such as 00:11:22:33:44:55, or leave it blank."), Text);
                return;
            }
            Value = new(name.Text.Trim(), ip.ToString(), (int)port.Value, platform, key, wake.Length > 0 ? wake : null,
                brand.SelectedIndex > 0 ? TvBrands.Selectable[brand.SelectedIndex - 1].Key : null);
            DialogResult = DialogResult.OK;
        };

        PrepareLayout();

        void UpdateAdapter()
        {
            adapter.Enabled = IPAddress.TryParse(address.Text.Trim(), out var ip) && ip.IsIPv6LinkLocal;
        }

        void AddRow(string label, Control control)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill,
                TextAlign = IsArabic ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft });
            control.Dock = DockStyle.Fill;
            grid.Controls.Add(control);
        }
    }
}









