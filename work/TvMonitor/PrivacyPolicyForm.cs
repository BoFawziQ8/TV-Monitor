sealed class PrivacyPolicyForm : FluentForm
{
    internal PrivacyPolicyForm()
    {
        Text = IsArabic ? "سياسة الخصوصية" : "Privacy policy";
        Icon = Program.AppIcon;
        ClientSize = new Size(680, 540);
        MinimumSize = new Size(380, 300);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        using var source = typeof(PrivacyPolicyForm).Assembly.GetManifestResourceStream("TvMonitor.PrivacyPolicy")!;
        using var reader = new StreamReader(source);
        var body = new RichTextBox
        {
            ReadOnly = true, Text = reader.ReadToEnd(), Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11),
            DetectUrls = false, BackColor = FluentTheme.Surface, ForeColor = FluentTheme.Text
        };
        Controls.Add(body);
        var close = new FluentButton { Text = T("Close"), Dock = DockStyle.Bottom, Height = 42 };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;
        Padding = new Padding(16);
        PrepareLayout();
    }
}
