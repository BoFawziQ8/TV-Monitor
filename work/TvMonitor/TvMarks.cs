using System.Drawing.Drawing2D;
using System.Globalization;

sealed record TvBrand(string Key, string Name, Color Color, string Monogram, string Keywords, string? Art = null);

static class TvBrands
{
    // Official 24x24 single-path marks from Simple Icons (CC0 library, brand trademarks belong to their owners).
    private static readonly Dictionary<string, string> Marks = new()
    {
            ["lg"] = """M14.522 14.078h3.27v1.33h-4.847v-6.83h1.577v5.5zm6.74-1.274h1.284v1.195c-.236.09-.698.18-1.137.18-1.42 0-1.893-.721-1.893-2.186 0-1.398.45-2.221 1.869-2.221.791 0 1.24.248 1.612.722l.982-.903c-.6-.855-1.646-1.114-2.629-1.114-2.208 0-3.368 1.205-3.368 3.504 0 2.288 1.047 3.528 3.358 3.528 1.06 0 2.096-.27 2.66-.665V11.53h-2.739v1.274zM5.291 6.709a5.29 5.29 0 1 1 0 10.582 5.291 5.291 0 1 1 0-10.582m3.16 8.457a4.445 4.445 0 0 0 1.31-3.161v-.242l-.22.001H6.596v.494h2.662l-.001.015a3.985 3.985 0 0 1-3.965 3.708 3.95 3.95 0 0 1-2.811-1.165 3.952 3.952 0 0 1-1.164-2.811c0-1.061.414-2.059 1.164-2.81a3.951 3.951 0 0 1 2.81-1.164l.252.003v-.495l-.251-.003a4.475 4.475 0 0 0-4.47 4.469c0 1.194.465 2.316 1.309 3.161a4.444 4.444 0 0 0 3.16 1.31 4.444 4.444 0 0 0 3.162-1.31m-2.91-1.297V9.644H5.04v4.72h1.556v-.495H5.543zm-1.265-3.552a.676.676 0 1 0-.675.674.676.676 0 0 0 .675-.674""",
            ["samsung"] = """M19.8166 10.2808l.0459 2.6934h-.023l-.7793-2.6934h-1.2837v3.3925h.8481l-.0458-2.785h.023l.8366 2.785h1.2264v-3.3925zm-16.149 0l-.6418 3.427h.9284l.4699-3.1175h.0229l.4585 3.1174h.9169l-.6304-3.4269zm5.1805 0l-.424 2.6132h-.023l-.424-2.6132H6.5788l-.0688 3.427h.8596l.023-3.0832h.0114l.573 3.0831h.8711l.5731-3.083h.023l.0228 3.083h.8596l-.0802-3.4269zm-7.2664 2.4527c.0343.0802.0229.1949.0114.2522-.0229.1146-.1031.2292-.3324.2292-.2177 0-.3438-.126-.3438-.3095v-.3323H0v.2636c0 .7679.6074.9971 1.2493.9971.6189 0 1.1346-.2178 1.2149-.7794.0458-.298.0114-.4928 0-.5616-.1605-.722-1.467-.9283-1.5588-1.3295-.0114-.0688-.0114-.1375 0-.1834.023-.1146.1032-.2292.3095-.2292.2063 0 .321.126.321.3095v.2063h.8595v-.2407c0-.745-.6762-.8596-1.1576-.8596-.6074 0-1.1117.2063-1.2034.7564-.023.149-.0344.2866.0114.4585.1376.7106 1.364.9169 1.5358 1.3524m11.152 0c.0343.0803.0228.1834.0114.2522-.023.1146-.1032.2292-.3324.2292-.2178 0-.3438-.126-.3438-.3095v-.3323h-.917v.2636c0 .7564.596.9857 1.2379.9857.6189 0 1.1232-.2063 1.2034-.7794.0459-.298.0115-.4814 0-.5616-.1375-.7106-1.4327-.9284-1.5243-1.318-.0115-.0688-.0115-.1376 0-.1835.0229-.1146.1031-.2292.3094-.2292.1948 0 .321.126.321.3095v.2063h.848v-.2407c0-.745-.6647-.8596-1.146-.8596-.6075 0-1.1004.1948-1.192.7564-.023.149-.023.2866.0114.4585.1376.7106 1.341.9054 1.513 1.3524m2.8882.4585c.2407 0 .3094-.1605.3323-.2522.0115-.0343.0115-.0917.0115-.126v-2.533h.871v2.4642c0 .0688 0 .1948-.0114.2292-.0573.6419-.5616.8482-1.192.8482-.6303 0-1.1346-.2063-1.192-.8482 0-.0344-.0114-.1604-.0114-.2292v-2.4642h.871v2.533c0 .0458 0 .0916.0115.126 0 .0917.0688.2522.3095.2522m7.1518-.0344c.2522 0 .3324-.1605.3553-.2522.0115-.0343.0115-.0917.0115-.126v-.4929h-.3553v-.5043H24v.917c0 .0687 0 .1145-.0115.2292-.0573.6303-.596.8481-1.2034.8481-.6075 0-1.1461-.2178-1.2034-.8481-.0115-.1147-.0115-.1605-.0115-.2293v-1.444c0-.0574.0115-.172.0115-.2293.0802-.6419.596-.8482 1.2034-.8482s1.1347.2063 1.2034.8482c.0115.1031.0115.2292.0115.2292v.1146h-.8596v-.1948s0-.0803-.0115-.1261c-.0114-.0802-.0802-.2521-.3438-.2521-.2521 0-.321.1604-.3438.2521-.0115.0458-.0115.1032-.0115.1605v1.5702c0 .0458 0 .0916.0115.126 0 .0917.0917.2522.3323.2522""",
            ["xiaomi"] = """M12 0C8.016 0 4.756.255 2.493 2.516.23 4.776 0 8.033 0 12.012c0 3.98.23 7.235 2.494 9.497C4.757 23.77 8.017 24 12 24c3.983 0 7.243-.23 9.506-2.491C23.77 19.247 24 15.99 24 12.012c0-3.984-.233-7.243-2.502-9.504C19.234.252 15.978 0 12 0zM4.906 7.405h5.624c1.47 0 3.007.068 3.764.827.746.746.827 2.233.83 3.676v4.54a.15.15 0 0 1-.152.147h-1.947a.15.15 0 0 1-.152-.148V11.83c-.002-.806-.048-1.634-.464-2.051-.358-.36-1.026-.441-1.72-.458H7.158a.15.15 0 0 0-.151.147v6.98a.15.15 0 0 1-.152.148H4.906a.15.15 0 0 1-.15-.148V7.554a.15.15 0 0 1 .15-.149zm12.131 0h1.949a.15.15 0 0 1 .15.15v8.892a.15.15 0 0 1-.15.148h-1.949a.15.15 0 0 1-.151-.148V7.554a.15.15 0 0 1 .151-.149zM8.92 10.948h2.046c.083 0 .15.066.15.147v5.352a.15.15 0 0 1-.15.148H8.92a.15.15 0 0 1-.152-.148v-5.352a.15.15 0 0 1 .152-.147Z""",
            ["roku"] = """M16.34 9.853l-2.254 2.254v-2.26H12.13v5.744h1.957v-2.33l2.353 2.33h2.46l-2.988-2.99 2.477-2.476v3.411c0 1.133.679 2.177 2.393 2.177.815 0 1.56-.462 1.922-.88l.88.759H24v-5.74h-1.951v3.718c-.22.384-.528.627-1.002.627-.482 0-.703-.286-.703-1.198V9.853zm-4.591 2.869A3.004 3.004 0 1 1 8.738 9.73a2.997 2.997 0 0 1 3.011 2.99m-3.011-1.57c-.518 0-.956.704-.956 1.572 0 .867.438 1.57.956 1.57.528 0 .968-.702.968-1.57 0-.869-.438-1.572-.968-1.572zm-2.206 4.447H4.313L2.55 13.153h-.594v2.44H0V8.26h2.8c1.616 0 2.935 1.1 2.935 2.45 0 .826-.505 1.562-1.273 2.013l2.07 2.875m-2.75-4.888A1.226 1.226 0 0 0 2.56 9.478h-.604v2.453h.605a1.225 1.225 0 0 0 1.22-1.221Z""",
            ["google"] = """M12.48 10.92v3.28h7.84c-.24 1.84-.853 3.187-1.787 4.133-1.147 1.147-2.933 2.4-6.053 2.4-4.827 0-8.6-3.893-8.6-8.72s3.773-8.72 8.6-8.72c2.6 0 4.507 1.027 5.907 2.347l2.307-2.307C18.747 1.44 16.133 0 12.48 0 5.867 0 .307 5.387.307 12s5.56 12 12.173 12c3.573 0 6.267-1.173 8.373-3.36 2.16-2.16 2.84-5.213 2.84-7.667 0-.76-.053-1.467-.173-2.053H12.48z""",
            ["android"] = """M18.4395 5.5586c-.675 1.1664-1.352 2.3318-2.0274 3.498-.0366-.0155-.0742-.0286-.1113-.043-1.8249-.6957-3.484-.8-4.42-.787-1.8551.0185-3.3544.4643-4.2597.8203-.084-.1494-1.7526-3.021-2.0215-3.4864a1.1451 1.1451 0 0 0-.1406-.1914c-.3312-.364-.9054-.4859-1.379-.203-.475.282-.7136.9361-.3886 1.5019 1.9466 3.3696-.0966-.2158 1.9473 3.3593.0172.031-.4946.2642-1.3926 1.0177C2.8987 12.176.452 14.772 0 18.9902h24c-.119-1.1108-.3686-2.099-.7461-3.0683-.7438-1.9118-1.8435-3.2928-2.7402-4.1836a12.1048 12.1048 0 0 0-2.1309-1.6875c.6594-1.122 1.312-2.2559 1.9649-3.3848.2077-.3615.1886-.7956-.0079-1.1191a1.1001 1.1001 0 0 0-.8515-.5332c-.5225-.0536-.9392.3128-1.0488.5449zm-.0391 8.461c.3944.5926.324 1.3306-.1563 1.6503-.4799.3197-1.188.0985-1.582-.4941-.3944-.5927-.324-1.3307.1563-1.6504.4727-.315 1.1812-.1086 1.582.4941zM7.207 13.5273c.4803.3197.5506 1.0577.1563 1.6504-.394.5926-1.1038.8138-1.584.4941-.48-.3197-.5503-1.0577-.1563-1.6504.4008-.6021 1.1087-.8106 1.584-.4941z""",
            ["apple"] = """M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701""",
    };

    private static readonly TvBrand[] Catalog =
    [
        Brand("lg", "LG", 0xA50034, "LG", "lg|webos|web os"),
        Brand("samsung", "Samsung", 0x1428A0, "S", "samsung|tizen"),
        Brand("sony", "Sony", 0x111111, "SONY", "sony|bravia"),
        Brand("tcl", "TCL", 0xE2231A, "TCL", "tcl"),
        Brand("hisense", "Hisense", 0x009444, "H", "hisense"),
        Brand("philips", "Philips", 0x0B5ED7, "P", "philips"),
        Brand("xiaomi", "Xiaomi", 0xFF6900, "mi", "xiaomi|redmi|mi tv"),
        Brand("panasonic", "Panasonic", 0x0E2D96, "PANA", "panasonic|viera"),
        Brand("toshiba", "Toshiba", 0xE60012, "T", "toshiba|regza"),
        Brand("sharp", "Sharp", 0xE60012, "SH", "sharp|aquos"),
        Brand("vizio", "Vizio", 0x22334D, "VZ", "vizio"),
        Brand("skyworth", "Skyworth", 0x005EB8, "SK", "skyworth|coocaa"),
        Brand("roku", "Roku", 0x6F1AB1, "R", "roku"),
        Brand("amazon", "Amazon", 0xFF9900, "A", "amazon|fire tv|firetv"),
        Brand("google", "Google", 0x4285F4, "G", "google|chromecast"),
        Brand("apple", "Apple", 0x111111, "", "apple"),
        Brand("orca", "Orca", 0x64748B, "OR", "orca"),
        Brand("wansa", "Wansa", 0x64748B, "W", "wansa"),
        Brand("android", "Android TV", 0x3DDC84, "", "android"),
        Brand("webos", "LG webOS", 0xA50034, "w", "webos"),
        Brand("tv", "TV", 0x64748B, "TV", "")
    ];

    private static readonly Dictionary<string, TvBrand> ByKey = Catalog.ToDictionary(b => b.Key, StringComparer.Ordinal);

    internal static IReadOnlyList<TvBrand> Selectable { get; } =
        Catalog.Where(b => b.Key is not ("android" or "webos" or "tv")).ToList();

    internal static TvBrand? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var brand) ? brand : null;

    internal static TvBrand Of(Tv tv)
    {
        if (Find(tv.Brand) is { } saved) return saved;
        if (KeyFor(tv.Name) is { } detected) return ByKey[detected];
        return TypeOf(tv.Platform);
    }

    internal static TvBrand TypeOf(Platform platform) => ByKey[platform switch
    {
        Platform.Android => "android",
        Platform.WebOS => "webos",
        _ => "tv"
    }];

    internal static string? KeyFor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var brand in Catalog)
            foreach (var keyword in brand.Keywords.Split('|', StringSplitOptions.RemoveEmptyEntries))
                if (Mentions(text, keyword)) return brand.Key;
        return null;
    }

    private static bool Mentions(string text, string keyword)
    {
        for (var index = 0; (index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0; index += keyword.Length)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var after = index + keyword.Length;
            if (beforeOk && (after >= text.Length || !char.IsLetterOrDigit(text[after]))) return true;
        }
        return false;
    }

    private static TvBrand Brand(string key, string name, int color, string monogram, string keywords) => new(
        key, name, Color.FromArgb((color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF), monogram, keywords,
        Marks.GetValueOrDefault(key));

    internal static void Draw(Graphics g, Tv? tv, Rectangle tile, float scale)
    {
        if (tv is null || tile.Width < 4 || tile.Height < 4) return;
        var brand = Of(tv);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = FluentTheme.Round(tile, 8 * scale);
        using var fill = new SolidBrush(Color.FromArgb(FluentTheme.Dark ? 52 : 26, brand.Color));
        using var border = new Pen(Color.FromArgb(FluentTheme.Dark ? 150 : 105, brand.Color), Math.Max(1f, scale));
        g.FillPath(fill, shape);
        g.DrawPath(border, shape);

        var inner = RectangleF.Inflate(tile, -7 * scale, -6 * scale);
        var ink = Ink(brand);
        var type = TypeOf(tv.Platform);
        var badges = type.Key != brand.Key && type.Art != brand.Art;
        var canvas = badges ? new RectangleF(inner.X, inner.Y, Math.Max(8 * scale, inner.Width - 9 * scale), inner.Height) : inner;
        if (brand.Art is { } art) Fill(g, art, canvas, ink);
        else Monogram(g, brand.Monogram, canvas, ink);

        if (badges) Badge(g, type, tile, scale);
    }

    private static Color Ink(TvBrand brand)
    {
        if (brand.Key == "tv") return FluentTheme.Muted;
        var color = brand.Color;
        if (FluentTheme.Dark && Luminance(color) < 0.35f) color = Blend(color, Color.White, 0.5f);
        return color;
    }

    private static float Luminance(Color color) => (0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B) / 255f;

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    private static void Monogram(Graphics g, string text, RectangleF area, Color ink)
    {
        if (text.Length == 0) return;
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var size = Math.Max(7f, area.Height * 0.62f);
        while (size > 6f)
        {
            using var font = new Font("Segoe UI Variable Text", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), flags);
            if (measured.Width <= area.Width && measured.Height <= area.Height * 1.1f)
            {
                TextRenderer.DrawText(g, text, font, Rectangle.Round(area), ink, flags);
                return;
            }
            size -= 0.5f;
        }
    }

    private static void Badge(Graphics g, TvBrand type, Rectangle tile, float scale)
    {
        var diameter = 10 * scale;
        var rect = new RectangleF(tile.Right - diameter - 1 * scale, tile.Bottom - diameter - 1 * scale, diameter, diameter);
        using var background = new SolidBrush(FluentTheme.Surface);
        g.FillEllipse(background, rect);
        using var border = new Pen(Color.FromArgb(170, type.Color), Math.Max(1f, scale));
        g.DrawEllipse(border, rect);
        var inner = RectangleF.Inflate(rect, -2.2f * scale, -2.2f * scale);
        if (type.Art is { } art) Fill(g, art, inner, type.Color);
        else Screen(g, inner, type.Color, scale);
    }

    private static void Screen(Graphics g, RectangleF area, Color ink, float scale)
    {
        using var pen = new Pen(ink, Math.Max(1f, 1.1f * scale));
        var screen = new RectangleF(area.X, area.Y + area.Height * 0.05f, area.Width, area.Height * 0.68f);
        g.DrawRectangle(pen, screen.X, screen.Y, screen.Width, screen.Height);
        g.DrawLine(pen, screen.Left + screen.Width * 0.42f, screen.Bottom, screen.Left + screen.Width * 0.42f, area.Bottom);
        g.DrawLine(pen, screen.Left + screen.Width * 0.2f, area.Bottom, screen.Right - screen.Width * 0.2f, area.Bottom);
    }

    private static void Fill(Graphics g, string art, RectangleF area, Color ink)
    {
        var path = Art(art);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var scale = Math.Min(area.Width / bounds.Width, area.Height / bounds.Height);
        using var placed = (GraphicsPath)path.Clone();
        using var transform = new Matrix(
            scale, 0, 0, scale,
            area.X + (area.Width - bounds.Width * scale) / 2f - bounds.X * scale,
            area.Y + (area.Height - bounds.Height * scale) / 2f - bounds.Y * scale);
        placed.Transform(transform);
        using var brush = new SolidBrush(ink);
        g.FillPath(brush, placed);
    }

    internal static GraphicsPath Art(string data)
    {
        lock (Parsed)
        {
            if (Parsed.TryGetValue(data, out var cached)) return cached;
            var built = SvgPath(data);
            Parsed.Add(data, built);
            return built;
        }
    }
    private static readonly Dictionary<string, GraphicsPath> Parsed = [];

    private static GraphicsPath SvgPath(string data)
    {
        var path = new GraphicsPath(FillMode.Winding);
        var tokens = Tokens(data);
        int i = 0;
        double x = 0, y = 0, startX = 0, startY = 0, controlX = 0, controlY = 0, quadX = 0, quadY = 0;
        var command = '\0';
        var previous = '\0';
        var open = false;
        double Number()
        {
            if (i >= tokens.Count) throw new FormatException("Incomplete path data.");
            return double.Parse(tokens[i++], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        void Begin()
        {
            if (open) return;
            path.StartFigure();
            open = true;
        }
        void Line(double nx, double ny)
        {
            Begin();
            path.AddLine((float)x, (float)y, (float)nx, (float)ny);
            x = nx; y = ny;
        }
        void Curve(double c1x, double c1y, double c2x, double c2y, double nx, double ny)
        {
            Begin();
            path.AddBezier((float)x, (float)y, (float)c1x, (float)c1y, (float)c2x, (float)c2y, (float)nx, (float)ny);
            controlX = c2x; controlY = c2y; x = nx; y = ny;
        }
        void Quad(double qx, double qy, double nx, double ny)
        {
            quadX = qx; quadY = qy;
            Curve(x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), nx + 2.0 / 3 * (qx - nx), ny + 2.0 / 3 * (qy - ny), nx, ny);
        }
        void Arc(double rx, double ry, double angle, bool large, bool sweep, double nx, double ny)
        {
            if (rx == 0 || ry == 0) { Line(nx, ny); return; }
            var phi = angle * Math.PI / 180;
            var cos = Math.Cos(phi);
            var sin = Math.Sin(phi);
            var dx = (x - nx) / 2;
            var dy = (y - ny) / 2;
            var px = cos * dx + sin * dy;
            var py = -sin * dx + cos * dy;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            var lambda = px * px / (rx * rx) + py * py / (ry * ry);
            if (lambda > 1) { var s = Math.Sqrt(lambda); rx *= s; ry *= s; }
            var denominator = rx * rx * py * py + ry * ry * px * px;
            var numerator = rx * rx * ry * ry - denominator;
            var coefficient = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, numerator / denominator));
            var cxp = coefficient * rx * py / ry;
            var cyp = -coefficient * ry * px / rx;
            var cx = cos * cxp - sin * cyp + (x + nx) / 2;
            var cy = sin * cxp + cos * cyp + (y + ny) / 2;
            var start = Angle(1, 0, (px - cxp) / rx, (py - cyp) / ry);
            var delta = Angle((px - cxp) / rx, (py - cyp) / ry, (-px - cxp) / rx, (-py - cyp) / ry);
            if (!sweep && delta > 0) delta -= 2 * Math.PI;
            else if (sweep && delta < 0) delta += 2 * Math.PI;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2)));
            var step = delta / steps;
            var handle = 4.0 / 3 * Math.Tan(step / 4);
            double PointX(double t) => cx + cos * rx * Math.Cos(t) - sin * ry * Math.Sin(t);
            double PointY(double t) => cy + sin * rx * Math.Cos(t) + cos * ry * Math.Sin(t);
            double DerivX(double t) => -cos * rx * Math.Sin(t) - sin * ry * Math.Cos(t);
            double DerivY(double t) => -sin * rx * Math.Sin(t) + cos * ry * Math.Cos(t);
            for (var s = 0; s < steps; s++)
            {
                var t1 = start + step * s;
                var t2 = t1 + step;
                Curve(PointX(t1) + handle * DerivX(t1), PointY(t1) + handle * DerivY(t1),
                      PointX(t2) - handle * DerivX(t2), PointY(t2) - handle * DerivY(t2), PointX(t2), PointY(t2));
            }
            x = nx; y = ny;
        }

        while (i < tokens.Count)
        {
            if (tokens[i].Length == 1 && char.IsLetter(tokens[i][0])) command = tokens[i++][0];
            var relative = char.IsLower(command);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    var mx = Number(); var my = Number();
                    if (relative) { mx += x; my += y; }
                    x = startX = mx; y = startY = my;
                    open = false;
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    var lx = Number(); var ly = Number();
                    Line(relative ? x + lx : lx, relative ? y + ly : ly);
                    break;
                case 'H':
                    var hx = Number();
                    Line(relative ? x + hx : hx, y);
                    break;
                case 'V':
                    var vy = Number();
                    Line(x, relative ? y + vy : vy);
                    break;
                case 'C':
                    var c1x = Number(); var c1y = Number(); var c2x = Number(); var c2y = Number(); var cx2 = Number(); var cy2 = Number();
                    if (relative) { c1x += x; c1y += y; c2x += x; c2y += y; cx2 += x; cy2 += y; }
                    Curve(c1x, c1y, c2x, c2y, cx2, cy2);
                    break;
                case 'S':
                    var sx = Number(); var sy = Number(); var ex = Number(); var ey = Number();
                    if (relative) { sx += x; sy += y; ex += x; ey += y; }
                    var reflectX = previous is 'C' or 'S' ? 2 * x - controlX : x;
                    var reflectY = previous is 'C' or 'S' ? 2 * y - controlY : y;
                    Curve(reflectX, reflectY, sx, sy, ex, ey);
                    break;
                case 'Q':
                    var qx = Number(); var qy = Number(); var qex = Number(); var qey = Number();
                    if (relative) { qx += x; qy += y; qex += x; qey += y; }
                    Quad(qx, qy, qex, qey);
                    break;
                case 'T':
                    var tx = Number(); var ty = Number();
                    if (relative) { tx += x; ty += y; }
                    var tqx = previous is 'Q' or 'T' ? 2 * x - quadX : x;
                    var tqy = previous is 'Q' or 'T' ? 2 * y - quadY : y;
                    Quad(tqx, tqy, tx, ty);
                    break;
                case 'A':
                    var rx = Number(); var ry = Number(); var rotation = Number();
                    var large = Number() != 0; var sweep = Number() != 0;
                    var ax = Number(); var ay = Number();
                    Arc(rx, ry, rotation, large, sweep, relative ? x + ax : ax, relative ? y + ay : ay);
                    break;
                case 'Z':
                    path.CloseFigure();
                    open = false;
                    x = startX; y = startY;
                    break;
                default:
                    throw new FormatException("Unsupported path command.");
            }
            previous = char.ToUpperInvariant(command);
        }
        return path;

        static double Angle(double ux, double uy, double vx, double vy)
        {
            var dot = ux * vx + uy * vy;
            var length = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            var value = Math.Acos(Math.Clamp(dot / length, -1, 1));
            return ux * vy - uy * vx < 0 ? -value : value;
        }
    }

    private static List<string> Tokens(string data)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
        }
        foreach (var c in data)
        {
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                Flush();
                tokens.Add(c.ToString());
            }
            else if (c is ',' or ' ' or '\r' or '\n' or '\t')
                Flush();
            else
            {
                var exponent = current.Length > 0 && current[^1] is 'e' or 'E';
                if (!exponent && (c is '+' or '-' || (c == '.' && current.ToString().Contains('.'))))
                    Flush();
                current.Append(c);
            }
        }
        Flush();
        return tokens;
    }
}

sealed class TvMark : Control
{
    private Tv? device;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Tv? Device
    {
        get => device;
        set
        {
            if (Equals(device, value)) return;
            device = value;
            Invalidate();
        }
    }

    internal TvMark()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? FluentTheme.Surface);
        if (device is null || Width < 8 || Height < 8) return;
        var pad = (int)Math.Ceiling(2 * DeviceDpi / 96f);
        TvBrands.Draw(e.Graphics, device, new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2), DeviceDpi / 96f);
    }
}
