using System.Diagnostics;

// Brief, non-activating label over the video so the Alt release key is visible
// before scrcpy captures the pointer in UHID mouse mode.
sealed class LiveMouseHint : Form
{
    private readonly Process viewer;
    private readonly System.Windows.Forms.Timer wait = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer life = new() { Interval = 9000 };
    private readonly Label text;

    internal LiveMouseHint(Process viewer)
    {
        this.viewer = viewer;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(15, 23, 42);
        Padding = new Padding(14, 8, 14, 8);
        Opacity = 0;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        text = new Label
        {
            AutoSize = true, BackColor = BackColor, ForeColor = Color.FromArgb(241, 245, 249),
            Font = new Font("Segoe UI", 10), Text = T("Mouse controls the TV · Alt or Win releases it")
        };
        Controls.Add(text);
        wait.Tick += (_, _) => Place();
        life.Tick += (_, _) => Close();
        Shown += (_, _) => { wait.Start(); life.Start(); };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x80 | 0x20 | 0x08000000; // tool window, click-through, no activate
            return parameters;
        }
    }

    private void Place()
    {
        if (IsDisposed) return;
        if (viewer.HasExited) { Close(); return; }
        viewer.Refresh();
        var handle = viewer.MainWindowHandle;
        if (handle == IntPtr.Zero || !StreamRemoteBar.TryClient(handle, out var video)) return;
        wait.Stop();
        var area = Screen.FromHandle(handle).WorkingArea;
        var x = Math.Clamp(video.Left + (video.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(video.Top + 14, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
        if (Opacity == 0) Opacity = 1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { wait.Stop(); wait.Dispose(); life.Stop(); life.Dispose(); }
        base.Dispose(disposing);
    }
}
