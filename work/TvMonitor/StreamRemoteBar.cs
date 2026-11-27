using System.Diagnostics;
using System.Runtime.InteropServices;

// A separate owned utility window keeps the control above SDL without embedding
// scrcpy or changing its video/audio lifecycle.
sealed class StreamRemoteBar : FluentForm
{
    private readonly Tv tv;
    private readonly Process viewer;
    private readonly System.Windows.Forms.Timer follow = new() { Interval = 200 };
    private readonly ToolTip tip = new();
    private readonly Action<Tv, StreamRemoteBar> open;
    private readonly FluentButton button;
    private IntPtr streamHandle;
    private bool tracking;

    internal StreamRemoteBar(Tv tv, Process viewer, Action<Tv, StreamRemoteBar> open)
    {
        this.tv = tv; this.viewer = viewer; this.open = open;
        Text = Tf($"{tv.Name} · Live controls");
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(128, 44);
        Padding = new Padding(2);
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        button = new FluentButton
        {
            Text = T("Remote"), RemoteIcon = true, Accent = true, Dock = DockStyle.Fill,
            AccessibleName = Tf($"Open remote for {tv.Name}")
        };
        button.Click += (_, _) => this.open(this.tv, this);
        tip.SetToolTip(button, Tf($"Open remote for {tv.Name}") + " · " + T("Alt releases the TV mouse"));
        Controls.Add(button);
        follow.Tick += (_, _) => FollowStream();
        PrepareLayout();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; } // WS_EX_TOOLWINDOW
    }

    internal bool Matches(Process process) => ReferenceEquals(viewer, process);
    internal void StartTracking()
    {
        if (tracking) return;
        tracking = true;
        follow.Start();
        FollowStream();
    }

    private void FollowStream()
    {
        if (IsDisposed) return;
        try
        {
            if (viewer.HasExited) { Close(); return; }
            viewer.Refresh();
            var handle = viewer.MainWindowHandle;
            if (handle == IntPtr.Zero || !IsWindow(handle)) return;
            if (IsIconic(handle) || !IsWindowVisible(handle)) return;
            if (!TryClient(handle, out var client)) return;
            var dpi = GetDpiForWindow(handle);
            var compact = client.Width * 96f / Math.Max(96, dpi) < 600;
            button.Text = compact ? "" : T("Remote");
            var targetSize = ButtonSize(client.Width, dpi);
            if (streamHandle != handle)
            {
                streamHandle = handle;
                if (Visible) Hide();
                Show(new StreamOwner(handle));
            }
            if (ClientSize != targetSize) ClientSize = targetSize;
            var margin = Math.Max(4, (int)(8 * GetDpiForWindow(handle) / 96f));
            var area = Screen.FromHandle(handle).WorkingArea;
            var location = OverlayPosition(client, area, Size, margin);
            if (Location != location) Location = location;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        { Close(); }
    }

    internal void PlaceRemote(Form remote)
    {
        if (streamHandle == IntPtr.Zero || !TryClient(streamHandle, out var client)) return;
        var area = Screen.FromHandle(streamHandle).WorkingArea;
        var gap = Math.Max(4, (int)(8 * GetDpiForWindow(streamHandle) / 96f));
        remote.Location = RemotePosition(client, area, remote.Size, Height, gap);
    }

    internal static Point OverlayPosition(Rectangle video, Rectangle area, Size size, int margin) => new(
        Clamp(Math.Max(video.Left + margin, video.Right - size.Width - margin), area.Left, area.Right - size.Width),
        Clamp(video.Top + margin, area.Top, area.Bottom - size.Height));

    internal static Size ButtonSize(int videoWidth, uint dpi)
    {
        var scale = Math.Max(96, dpi) / 96f;
        return new((int)((videoWidth / scale < 600 ? 48 : 128) * scale), (int)(44 * scale));
    }

    internal static Point RemotePosition(Rectangle video, Rectangle area, Size size, int toolbarHeight, int gap)
    {
        int left = video.Right + gap + size.Width <= area.Right ? video.Right + gap
            : video.Left - gap - size.Width >= area.Left ? video.Left - gap - size.Width
            : video.Right - size.Width - gap;
        return new(Clamp(left, area.Left, area.Right - size.Width),
            Clamp(video.Top + toolbarHeight + gap * 2, area.Top, area.Bottom - size.Height));
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, Math.Max(min, max));
    internal static bool TryClient(IntPtr handle, out Rectangle rectangle)
    {
        rectangle = Rectangle.Empty;
        if (!GetClientRect(handle, out var client)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin)) return false;
        if (client.Right <= 0 || client.Bottom <= 0) return false;
        rectangle = new(origin.X, origin.Y, client.Right, client.Bottom);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { follow.Stop(); follow.Dispose(); tip.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record StreamOwner(IntPtr Handle) : IWin32Window;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
