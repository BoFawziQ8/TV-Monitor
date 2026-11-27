using System.Net.Sockets;
using System.Net.WebSockets;

sealed class RemoteForm : FluentForm
{
    private readonly Tv tv;
    private readonly Network.RemoteSession session;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendQueue = new(1);
    private readonly Label status = new();
    private int pending;
    private bool closed;

    internal RemoteForm(Tv tv)
    {
        this.tv = tv;
        session = new Network.RemoteSession(tv);
        Text = Tf($"{tv.Name} · Remote");
        Icon = Program.AppIcon;
        ClientSize = new Size(316, 430);
        MinimumSize = new Size(300, 460);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9);
        MaximizeBox = false;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 12, Padding = new Padding(10)
        };
        for (var i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        for (var i = 0; i < 10; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(grid);

        var title = new Label
        {
            Text = tv.Name, Dock = DockStyle.Fill, AutoEllipsis = true,
            Font = new Font("Segoe UI", 13, FontStyle.Bold)
        };
        grid.Controls.Add(title, 0, 0);
        grid.SetColumnSpan(title, 3);
        Add(T("Home"), RemoteKey.Home, 0, 1);
        Add(T("Back"), RemoteKey.Back, 1, 1);
        Add(T("Input"), RemoteKey.Input, 2, 1);
        Add(T("Settings"), RemoteKey.Settings, 0, 2);
        Add("↑", RemoteKey.Up, 1, 2);
        Add("←", RemoteKey.Left, 0, 3);
        Add(T("OK"), RemoteKey.Ok, 1, 3);
        Add("→", RemoteKey.Right, 2, 3);
        Add(T("CH −"), RemoteKey.ChannelDown, 0, 4);
        Add("↓", RemoteKey.Down, 1, 4);
        Add(T("CH +"), RemoteKey.ChannelUp, 2, 4);
        Add(T("Vol −"), RemoteKey.VolumeDown, 0, 5);
        Add(T("Mute"), RemoteKey.Mute, 1, 5);
        Add(T("Vol +"), RemoteKey.VolumeUp, 2, 5);
        for (var i = 1; i <= 9; i++)
            Add(i.ToString(), RemoteKey.Zero + i, (i - 1) % 3, 6 + (i - 1) / 3);
        Add(T("Play"), RemoteKey.Play, 0, 9);
        Add("0", RemoteKey.Zero, 1, 9);
        Add(T("Pause"), RemoteKey.Pause, 2, 9);

        var hints = new Label
        {
            Text = T("Keyboard  ↑ ↓ ← →   Enter   Esc   0–9"), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.LightSteelBlue
        };
        grid.Controls.Add(hints, 0, 10);
        grid.SetColumnSpan(hints, 3);
        status.Text = T("Choose a button to connect.");
        status.Dock = DockStyle.Fill;
        status.RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        status.ForeColor = Color.LightSteelBlue;
        grid.Controls.Add(status, 0, 11);
        grid.SetColumnSpan(status, 3);

        PrepareLayout();

        void Add(string text, RemoteKey key, int column, int row)
        {
            var button = new FluentButton
            {
                Text = text, AccessibleName = key.ToString(), Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 2, 3, 2),
                BackColor = key == RemoteKey.Ok ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59),
                ForeColor = ForeColor, UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => _ = Press(key);
            grid.Controls.Add(button, column, row);
        }
    }

    internal bool Matches(Tv candidate) => tv.Address == candidate.Address && tv.Port == candidate.Port;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        RemoteKey? key = keyData switch
        {
            Keys.Up => RemoteKey.Up, Keys.Down => RemoteKey.Down, Keys.Left => RemoteKey.Left,
            Keys.Right => RemoteKey.Right, Keys.Enter => RemoteKey.Ok, Keys.Escape or Keys.Back => RemoteKey.Back,
            Keys.Home => RemoteKey.Home,
            >= Keys.D0 and <= Keys.D9 => RemoteKey.Zero + (keyData - Keys.D0),
            >= Keys.NumPad0 and <= Keys.NumPad9 => RemoteKey.Zero + (keyData - Keys.NumPad0),
            _ => null
        };
        if (key is null) return base.ProcessCmdKey(ref msg, keyData);
        _ = Press(key.Value);
        return true;
    }

    private async Task Press(RemoteKey key)
    {
        if (closed || pending >= 8) return;
        pending++;
        var token = lifetime.Token;
        try
        {
            await sendQueue.WaitAsync(token);
            try
            {
                status.Text = Tf($"Sending {key}…");
                await session.Press(key, token);
                if (!closed) status.Text = Tf($"Sent {key}.");
            }
            finally { sendQueue.Release(); }
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or
                                    OperationCanceledException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            if (!closed)
                status.Text = ex is OperationCanceledException ? T("The TV did not respond in time.") : ex.Message;
        }
        finally { pending--; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ReleaseSession();
        base.OnFormClosed(e);
    }

    private void ReleaseSession()
    {
        if (closed) return;
        closed = true;
        lifetime.Cancel();
        session?.Dispose();
        lifetime.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseSession();
        base.Dispose(disposing);
    }
}
