using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class FluentTheme
{
    internal static bool Dark { get; private set; }
    internal static Color Window => SystemInformation.HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
    internal static Color Surface => SystemInformation.HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(43, 43, 43) : Color.White;
    internal static Color Subtle => SystemInformation.HighContrast ? SystemColors.Control : Dark ? Color.FromArgb(38, 38, 38) : Color.FromArgb(249, 249, 249);
    internal static Color Text => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(247, 247, 247) : Color.FromArgb(28, 28, 28);
    internal static Color Muted => SystemInformation.HighContrast ? SystemColors.GrayText : Dark ? Color.FromArgb(183, 183, 183) : Color.FromArgb(96, 96, 96);
    internal static Color Border => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(66, 66, 66) : Color.FromArgb(225, 225, 225);
    internal static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
    internal static Color AccentText => SystemInformation.HighContrast ? SystemColors.HighlightText : Dark ? Color.FromArgb(0, 48, 70) : Color.White;
    internal static Color Selection => SystemInformation.HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(49, 63, 72) : Color.FromArgb(230, 242, 251);
    internal static Color Good => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(108, 209, 148) : Color.FromArgb(15, 115, 62);

    internal static void ReadSystem()
    {
        Dark = !SystemInformation.HighContrast &&
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
    }

    internal static GraphicsPath Round(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void Apply(Control control)
    {
        control.ForeColor = Text;
        if (control is FluentButton button)
        {
            if (button.BackColor.B > 170 && button.BackColor.R < 70) button.Accent = true;
            if (!(button.BackColor.R > 120 && button.BackColor.G < 60))
                button.BackColor = button.Accent ? Accent : Surface;
        }
        else if (control is FluentCard) control.BackColor = Surface;
        else if (control is Form) control.BackColor = Window;
        else if (control is DeviceList) control.BackColor = Window;
        else if (control is ListView or ListBox or TextBox or ComboBox or NumericUpDown)
        {
            control.BackColor = Surface;
        }
        else
        {
            control.BackColor = control.Parent?.BackColor ?? Window;
            if (control is Label && (Equals(control.Tag, "muted") || control.Font.Size <= 10)) control.ForeColor = Muted;
        }
        foreach (Control child in control.Controls) Apply(child);
        if (control is LinkLabel link)
        {
            link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = Accent;
            link.DisabledLinkColor = Muted;
        }
        control.Invalidate();
    }

    internal static void NativeWindow(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        int dark = Dark ? 1 : 0, corners = 2;
        DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(form.Handle, 33, ref corners, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int material = 2;
            DwmSetWindowAttribute(form.Handle, 38, ref material, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

class FluentForm : Form
{
    private bool prepared;
    internal FluentForm()
    {
        Font = new Font("Segoe UI Variable Text", 10);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
    }

    internal void PrepareLayout()
    {
        if (prepared) return;
        prepared = true;
        float scale = DeviceDpi / 96f;
        NormalizeButtons(this, scale);
        if (scale != 1) Scale(new SizeF(scale, scale));
        AutoScaleDimensions = new SizeF(DeviceDpi, DeviceDpi);
        AutoScaleMode = AutoScaleMode.Dpi;
        static void NormalizeButtons(Control parent, float scale)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is FluentButton { AutoSize: true } button)
                {
                    var measured = TextRenderer.MeasureText(button.Text, button.Font);
                    button.AutoSize = false;
                    button.Size = new Size(Math.Max(button.MinimumSize.Width, (int)Math.Ceiling(measured.Width / scale) + 24), Math.Max(36, button.MinimumSize.Height));
                }
                NormalizeButtons(child, scale);
            }
        }
    }

    protected override void OnShown(EventArgs e)
    {
        FluentTheme.ReadSystem();
        FluentTheme.Apply(this);
        FluentTheme.NativeWindow(this);
        var area = Screen.FromControl(this).WorkingArea;
        var margin = (int)(16 * DeviceDpi / 96f);
        if (Width > area.Width - margin * 2 || Height > area.Height - margin * 2)
            Size = new Size(Math.Min(Width, area.Width - margin * 2), Math.Min(Height, area.Height - margin * 2));
        if (!area.Contains(Bounds))
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        base.OnShown(e);
    }

    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            FluentTheme.ReadSystem();
            FluentTheme.Apply(this);
            FluentTheme.NativeWindow(this);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        base.Dispose(disposing);
    }
}

sealed class FluentButton : Button
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Accent { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Glyph { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool RemoteIcon { get; set; }
    private bool hovered, pressed;

    internal FluentButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? FluentTheme.Window);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? FluentTheme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = FluentTheme.Round(rect, 4 * scale);
        var danger = BackColor.R > 120 && BackColor.G < 60;
        var background = danger ? Color.FromArgb(165, 36, 42) : Accent ? FluentTheme.Accent : FluentTheme.Surface;
        if (!Enabled) background = FluentTheme.Subtle;
        else if (pressed) background = ControlPaint.Dark(background, .08f);
        else if (hovered) background = FluentTheme.Dark ? ControlPaint.Light(background, .08f) : ControlPaint.Dark(background, .04f);
        using var fill = new SolidBrush(background);
        g.FillPath(fill, path);
        using var stroke = new Pen(Accent && Enabled ? background : FluentTheme.Border);
        g.DrawPath(stroke, path);
        var color = !Enabled ? FluentTheme.Muted : danger ? Color.White : Accent ? FluentTheme.AccentText : FluentTheme.Text;
        var textRect = Rectangle.Inflate(ClientRectangle, -(int)(10 * scale), 0);
        if (Glyph.Length > 0 || RemoteIcon)
        {
            var glyphRect = new Rectangle(textRect.X, textRect.Y, (int)(24 * scale), textRect.Height);
            if (RightToLeft == RightToLeft.Yes) glyphRect.X = textRect.Right - glyphRect.Width;
            if (Text.Length == 0) glyphRect.X = (Width - glyphRect.Width) / 2;
            if (RemoteIcon)
            {
                var icon = new RectangleF(glyphRect.Left + 6 * scale, glyphRect.Top + (glyphRect.Height - 26 * scale) / 2, 12 * scale, 26 * scale);
                using var outline = FluentTheme.Round(icon, 3 * scale);
                using var pen = new Pen(color, Math.Max(1, 1.3f * scale));
                using var dot = new SolidBrush(color);
                g.DrawPath(pen, outline);
                g.FillEllipse(dot, icon.Left + 5 * scale, icon.Top + 3 * scale, 2 * scale, 2 * scale);
                g.DrawEllipse(pen, icon.Left + 3 * scale, icon.Top + 8 * scale, 6 * scale, 6 * scale);
                for (int row = 0; row < 2; row++)
                    for (int column = 0; column < 2; column++)
                        g.FillEllipse(dot, icon.Left + (3 + column * 4) * scale, icon.Top + (18 + row * 4) * scale, 2 * scale, 2 * scale);
            }
            else
            {
                using var iconFont = new Font("Segoe Fluent Icons", Font.Size * 1.3f);
                TextRenderer.DrawText(g, Glyph, iconFont, glyphRect, color, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }
            if (RightToLeft == RightToLeft.Yes) textRect.Width -= glyphRect.Width;
            else { textRect.X += glyphRect.Width; textRect.Width -= glyphRect.Width; }
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Font, textRect, color, flags);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(FluentTheme.Text, Math.Max(1, scale));
            using var outline = FluentTheme.Round(RectangleF.Inflate(rect, -3 * scale, -3 * scale), 3 * scale);
            g.DrawPath(focus, outline);
        }
    }
}

sealed class FluentCard : Panel
{
    internal FluentCard()
    {
        DoubleBuffered = true;
        Padding = new Padding(18);
        BackColor = FluentTheme.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(FluentTheme.Border);
        using var path = FluentTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * DeviceDpi / 96f);
        e.Graphics.DrawPath(border, path);
    }
}

sealed class DeviceList : ListView
{
    private readonly ImageList spacing = new() { ImageSize = new Size(1, 92) };

    internal DeviceList()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        View = View.Details;
        BorderStyle = BorderStyle.None;
        FullRowSelect = true;
        HideSelection = false;
        HeaderStyle = ColumnHeaderStyle.None;
        SmallImageList = spacing;
        DrawColumnHeader += (_, e) => e.DrawDefault = false;
        DrawItem += (_, _) => { };
        DrawSubItem += DrawDevice;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BeginInvoke(UpdateSpacing);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        BeginInvoke(UpdateSpacing);
    }

    internal void UpdateSpacing()
    {
        if (IsDisposed) return;
        spacing.ImageSize = new Size(1, Math.Min(256, (int)(92 * DeviceDpi / 96f)));
        Invalidate();
    }

    private void DrawDevice(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 0 || e.Item is null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        int D(int n) => (int)(n * scale);
        using var background = new SolidBrush(FluentTheme.Window);
        g.FillRectangle(background, e.Bounds);
        var card = new Rectangle(e.Bounds.X + D(1), e.Bounds.Y + D(3), Math.Max(1, e.Bounds.Width - D(4)), e.Bounds.Height - D(9));
        using var shape = FluentTheme.Round(card, D(8));
        using var fill = new SolidBrush(e.Item.Selected ? FluentTheme.Selection : FluentTheme.Surface);
        using var border = new Pen(e.Item.Selected ? FluentTheme.Accent : FluentTheme.Border);
        g.FillPath(fill, shape);
        g.DrawPath(border, shape);
        bool rtl = IsArabic;
        var tile = new Size(D(46), D(32));
        var mark = new Rectangle(rtl ? card.Right - D(16) - tile.Width : card.Left + D(14), card.Top + (card.Height - tile.Height) / 2, tile.Width, tile.Height);
        if (e.Item.Tag is Tv device) TvBrands.Draw(g, device, mark, scale);
        var text = new Rectangle(rtl ? card.Left + D(14) : card.Left + D(72), card.Top + D(12), card.Width - D(88), D(24));
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | (rtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left);
        using var title = new Font(Font.FontFamily, Font.Size * 1.1f, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.Size * .9f);
        var foreground = SystemInformation.HighContrast && e.Item.Selected ? SystemColors.HighlightText : FluentTheme.Text;
        var muted = SystemInformation.HighContrast && e.Item.Selected ? SystemColors.HighlightText : FluentTheme.Muted;
        TextRenderer.DrawText(g, e.Item.Text, title, text, foreground, flags);
        text.Y += D(24); text.Height = D(20);
        var status = e.Item.SubItems.Count > 1 ? e.Item.SubItems[1].Text : "";
        var source = e.Item.SubItems.Count > 2 ? e.Item.SubItems[2].Text : "";
        var active = e.Item.Tag is Tv && status == T("On");
        TextRenderer.DrawText(g, source == "—" ? status : status + "  ·  " + source, small, text, active && !SystemInformation.HighContrast ? FluentTheme.Good : muted, flags);
        text.Y += D(20);
        var meta = e.Item.SubItems.Count > 4 ? e.Item.SubItems[3].Text + "  ·  " + e.Item.SubItems[4].Text : "";
        TextRenderer.DrawText(g, meta, small, text, muted, flags);
        if (e.Item.Focused && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(card, -D(4), -D(4)), FluentTheme.Text, FluentTheme.Surface);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) spacing.Dispose();
        base.Dispose(disposing);
    }
}


