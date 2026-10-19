using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// RetroSync 2.0 - dark "Windows 11" look: palette, fonts, native dark-mode helpers, drawing helpers and line icons.
// Pure presentation: nothing in here knows about sync logic.
namespace RetroSync
{
    static class Theme
    {
        // ---- palette (solid equivalents of the mockup's translucent layers)
        public static readonly Color WindowBg = Color.FromArgb(18, 20, 24);      // #121418
        public static readonly Color GlowA = Color.FromArgb(29, 58, 60);         // #1d3a3c (top-left glow)
        public static readonly Color GlowB = Color.FromArgb(22, 32, 42);         // #16202a
        public static readonly Color Content = Color.FromArgb(20, 22, 26);       // content surface
        public static readonly Color Card = Color.FromArgb(30, 32, 36);          // .045 white on content
        public static readonly Color Card2 = Color.FromArgb(37, 39, 43);         // .07 white
        public static readonly Color Field = Color.FromArgb(24, 26, 30);         // text field background
        public static readonly Color Line = Color.FromArgb(44, 47, 51);          // .08 white hairline
        public static readonly Color LineStrong = Color.FromArgb(62, 66, 71);
        public static readonly Color Btn = Color.FromArgb(46, 49, 53);           // .08 white
        public static readonly Color BtnHover = Color.FromArgb(56, 59, 64);
        public static readonly Color BtnBorder = Color.FromArgb(56, 59, 63);
        public static readonly Color Text = Color.FromArgb(230, 232, 234);       // #e6e8ea
        public static readonly Color TextSoft = Color.FromArgb(207, 212, 216);
        public static readonly Color Muted = Color.FromArgb(154, 163, 171);      // #9aa3ab
        public static readonly Color Dim = Color.FromArgb(108, 117, 125);        // #6c757d
        public static readonly Color Accent = Color.FromArgb(44, 196, 181);      // #2cc4b5 teal
        public static readonly Color Accent2 = Color.FromArgb(31, 158, 147);     // #1f9e93
        public static readonly Color AccentHi = Color.FromArgb(95, 224, 210);    // #5fe0d2
        public static readonly Color OnAccent = Color.FromArgb(5, 32, 29);       // #05201d
        public static readonly Color Ok = Color.FromArgb(76, 208, 125);          // #4cd07d
        public static readonly Color Warn = Color.FromArgb(242, 184, 75);        // #f2b84b
        public static readonly Color Bad = Color.FromArgb(239, 106, 106);        // #ef6a6a
        public static readonly Color Blue = Color.FromArgb(90, 168, 255);        // #5aa8ff
        public static readonly Color Violet = Color.FromArgb(167, 139, 250);
        public static readonly Color Selection = Color.FromArgb(40, 44, 49);

        // ---- legacy names (used by the PS2 window, presentation only)
        public static Color Bg { get { return Content; } }
        public static Color Ink { get { return Text; } }

        // ---- fonts (pixel sized so the layout is identical on every machine)
        public static readonly string UiFamily = PickFamily(new[] { "Segoe UI Variable Text", "Segoe UI", "Open Sans", "Noto Sans", "DejaVu Sans" });
        public static readonly string SemiFamily = PickFamily(new[] { "Segoe UI Semibold", "Segoe UI Variable Display Semib", "Segoe UI", "Open Sans", "DejaVu Sans" });
        public static readonly string MonoFamily = PickFamily(new[] { "Cascadia Mono", "Consolas", "DejaVu Sans Mono", "Courier New" });
        static readonly bool semiIsBold = SemiFamily != "Segoe UI Semibold" && !SemiFamily.Contains("Semib");

        public static readonly Font F11 = Px(11f), F115 = Px(11.5f), F12 = Px(12f), F125 = Px(12.5f), F13 = Px(13f), F135 = Px(13.5f), F14 = Px(14f);
        public static readonly Font S11 = Semi(11f), S12 = Semi(12f), S13 = Semi(13f), S135 = Semi(13.5f), S15 = Semi(15f), S18 = Semi(18f), S24 = Semi(24f);
        public static readonly Font Mono11 = new Font(MonoFamily, 11.5f, GraphicsUnit.Pixel), Mono12 = new Font(MonoFamily, 12f, GraphicsUnit.Pixel);

        public static Font Px(float px) { return new Font(UiFamily, px, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Semi(float px) { return new Font(SemiFamily, px, semiIsBold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }

        static string PickFamily(string[] names)
        {
            try
            {
                var installed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in FontFamily.Families) installed.Add(f.Name);
                foreach (var n in names) if (installed.Contains(n)) return n;
            }
            catch { }
            return names[names.Length - 1];
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        // ---- native dark mode (Windows 10 1809+ / Windows 11). Every call is best effort.
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        public static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (19 on Windows 10 before 20H1); caption colour = 35 (Windows 11 only).
        public static void DarkTitleBar(IntPtr hwnd)
        {
            if (!IsWindows || hwnd == IntPtr.Zero) return;
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int caption = WindowBg.R | (WindowBg.G << 8) | (WindowBg.B << 16);   // COLORREF 0x00BBGGRR
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
                int text = Text.R | (Text.G << 8) | (Text.B << 16);
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);
            }
            catch { }
        }

        // Dark scrollbars / header for native controls (TextBox, ListView).
        public static void DarkScrollbars(Control c)
        {
            if (!IsWindows) return;
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        // ---- drawing helpers
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            if (radius <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
        {
            using (var p = Round(new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width), radius))
            using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        public static void Text1(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine)
        {
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags);
        }

        public static Size Measure(string s, Font f)
        {
            return TextRenderer.MeasureText(s ?? "", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // Status dot with a soft halo, like the mockup's chips.
        public static void Dot(Graphics g, Color c, float cx, float cy)
        {
            using (var b = new SolidBrush(Alpha(c, 46))) g.FillEllipse(b, cx - 7, cy - 7, 14, 14);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 4, cy - 4, 8, 8);
        }

        public enum BadgeKind { Up, Down, Conflict, Neutral, Bad, Ok, Info, Merge }

        public static void BadgeColors(BadgeKind k, out Color bg, out Color fg)
        {
            switch (k)
            {
                case BadgeKind.Up: bg = Alpha(Accent, 38); fg = Color.FromArgb(111, 227, 214); break;
                case BadgeKind.Down: bg = Alpha(Blue, 38); fg = Color.FromArgb(156, 202, 255); break;
                case BadgeKind.Conflict: bg = Alpha(Warn, 44); fg = Color.FromArgb(255, 207, 115); break;
                case BadgeKind.Bad: bg = Alpha(Bad, 38); fg = Color.FromArgb(255, 157, 157); break;
                case BadgeKind.Ok: bg = Alpha(Ok, 34); fg = Color.FromArgb(150, 232, 177); break;
                case BadgeKind.Merge: bg = Alpha(Violet, 40); fg = Color.FromArgb(205, 190, 255); break;
                case BadgeKind.Info: bg = Alpha(Blue, 30); fg = Color.FromArgb(180, 205, 235); break;
                default: bg = Color.FromArgb(18, 255, 255, 255); fg = Muted; break;
            }
        }

        // Draws a small rounded badge; returns its width.
        public static int Badge(Graphics g, string text, BadgeKind k, int x, int cy, bool measureOnly = false)
        {
            var sz = Measure(text, S11);
            int w = sz.Width + 16, h = 20;
            if (measureOnly) return w;
            Color bg, fg; BadgeColors(k, out bg, out fg);
            var r = new Rectangle(x, cy - h / 2, w, h);
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            FillRound(g, bg, r, 4);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, text, S11, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return w;
        }

        // Applies the dark palette to a plain WinForms dialog (used for the few dialogs kept from v1).
        public static void ApplyDialog(Form f)
        {
            f.BackColor = Card; f.ForeColor = Text;
            f.Font = F13;
            f.HandleCreated += (s, e) => DarkTitleBar(f.Handle);
            ApplyTree(f);
        }

        public static void ApplyTree(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is TextBox)
                {
                    var t = (TextBox)c;
                    t.BackColor = Field; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is Button)
                {
                    var b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat; b.BackColor = Btn; b.ForeColor = Text; b.UseVisualStyleBackColor = false;
                    b.FlatAppearance.BorderColor = BtnBorder; b.FlatAppearance.MouseOverBackColor = BtnHover;
                }
                else if (c is CheckBox || c is Label || c is RadioButton)
                {
                    c.ForeColor = c is LinkLabel ? Accent : Text;
                    if (c is LinkLabel) ((LinkLabel)c).LinkColor = Accent;
                }
                else if (c is ListView)
                {
                    c.BackColor = Card; c.ForeColor = Text;
                }
                if (c.HasChildren) ApplyTree(c);
            }
        }
    }

    // Simple line icons drawn with GDI+ so they look the same on every system (no icon font needed).
    enum Ico { Dashboard, Gamepad, Card, Dolphin, Database, Gear, Log, Power, Sync, Eye, Folder, Play, Monitor, Console, Clock, Save, Search, Filter, Check, Warning, Swap, Close, Info, Download, Archive, Link }

    static class Icons
    {
        public static void Draw(Graphics g, Ico ico, RectangleF box, Color c, float stroke = 1.6f)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(box.Width, box.Height) / 24f;
            g.TranslateTransform(box.X + (box.Width - 24 * s) / 2, box.Y + (box.Height - 24 * s) / 2);
            g.ScaleTransform(s, s);
            using (var p = new Pen(c, stroke / s * 1.0f * (s < 0.8f ? 1.1f : 1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(c))
            {
                p.Width = stroke / s;
                switch (ico)
                {
                    case Ico.Dashboard:
                        R(g, p, 3, 3, 7.5f, 7.5f, 1.5f); R(g, p, 13.5f, 3, 7.5f, 7.5f, 1.5f); R(g, p, 3, 13.5f, 7.5f, 7.5f, 1.5f); R(g, p, 13.5f, 13.5f, 7.5f, 7.5f, 1.5f); break;
                    case Ico.Gamepad:
                        R(g, p, 2, 6.5f, 20, 11.5f, 5);
                        g.DrawLine(p, 7, 10, 7, 14.5f); g.DrawLine(p, 4.8f, 12.2f, 9.2f, 12.2f);
                        g.FillEllipse(b, 15.2f, 9.6f, 2.2f, 2.2f); g.FillEllipse(b, 17.4f, 12.6f, 2.2f, 2.2f); break;
                    case Ico.Card:
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new[] { new PointF(6, 2.5f), new PointF(15, 2.5f), new PointF(19, 6.5f), new PointF(19, 21.5f), new PointF(6, 21.5f) });
                            path.CloseFigure(); g.DrawPath(p, path);
                        }
                        g.DrawLine(p, 9.5f, 6, 9.5f, 9); g.DrawLine(p, 12.5f, 6, 12.5f, 9); g.DrawLine(p, 9, 15, 16, 15); break;
                    case Ico.Dolphin:
                        g.DrawEllipse(p, 3, 3, 18, 18); g.DrawEllipse(p, 9.5f, 9.5f, 5, 5); g.DrawArc(p, 5.5f, 5.5f, 13, 13, 200, 70); break;
                    case Ico.Database:
                        g.DrawEllipse(p, 4, 3, 16, 5.5f);
                        g.DrawLine(p, 4, 5.8f, 4, 18.2f); g.DrawLine(p, 20, 5.8f, 20, 18.2f);
                        g.DrawArc(p, 4, 9.2f, 16, 5.5f, 0, 180); g.DrawArc(p, 4, 15.5f, 16, 5.5f, 0, 180); break;
                    case Ico.Gear:
                        g.DrawEllipse(p, 9, 9, 6, 6);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(p, 12 + (float)Math.Cos(a) * 6.5f, 12 + (float)Math.Sin(a) * 6.5f, 12 + (float)Math.Cos(a) * 9.3f, 12 + (float)Math.Sin(a) * 9.3f);
                        }
                        g.DrawEllipse(p, 5.5f, 5.5f, 13, 13); break;
                    case Ico.Log:
                        R(g, p, 4, 2.5f, 16, 19, 2);
                        g.DrawLine(p, 8, 8, 16, 8); g.DrawLine(p, 8, 12, 16, 12); g.DrawLine(p, 8, 16, 13, 16); break;
                    case Ico.Power:
                        g.DrawArc(p, 4, 4.5f, 16, 16, -50, 280); g.DrawLine(p, 12, 2.5f, 12, 11.5f); break;
                    case Ico.Sync:
                        g.DrawArc(p, 4, 4, 16, 16, 200, 140); g.DrawArc(p, 4, 4, 16, 16, 20, 140);
                        g.DrawLines(p, new[] { new PointF(17.5f, 2.8f), new PointF(18.8f, 6.6f), new PointF(15, 7.6f) });
                        g.DrawLines(p, new[] { new PointF(6.5f, 21.2f), new PointF(5.2f, 17.4f), new PointF(9, 16.4f) }); break;
                    case Ico.Eye:
                        using (var path = new GraphicsPath())
                        {
                            path.AddBezier(2, 12, 6, 5, 18, 5, 22, 12); path.AddBezier(22, 12, 18, 19, 6, 19, 2, 12);
                            g.DrawPath(p, path);
                        }
                        g.DrawEllipse(p, 9, 9, 6, 6); break;
                    case Ico.Folder:
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new[] { new PointF(2.5f, 6), new PointF(2.5f, 19.5f), new PointF(21.5f, 19.5f), new PointF(21.5f, 8), new PointF(11.5f, 8), new PointF(9.5f, 5), new PointF(2.5f, 5) });
                            path.CloseFigure(); g.DrawPath(p, path);
                        }
                        break;
                    case Ico.Play:
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new[] { new PointF(7, 4.5f), new PointF(19, 12), new PointF(7, 19.5f) }); path.CloseFigure(); g.DrawPath(p, path);
                        }
                        break;
                    case Ico.Monitor:
                        R(g, p, 2.5f, 4, 19, 12.5f, 2); g.DrawLine(p, 8.5f, 20.5f, 15.5f, 20.5f); g.DrawLine(p, 12, 16.5f, 12, 20.5f); break;
                    case Ico.Console:
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new[] { new PointF(8, 21), new PointF(10.5f, 3), new PointF(13.5f, 3), new PointF(16, 21) }); path.CloseFigure(); g.DrawPath(p, path);
                        }
                        g.DrawLine(p, 6, 21, 18, 21); break;
                    case Ico.Clock:
                        g.DrawEllipse(p, 3, 3, 18, 18); g.DrawLines(p, new[] { new PointF(12, 7), new PointF(12, 12), new PointF(15.5f, 14) }); break;
                    case Ico.Save:
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new[] { new PointF(4, 3), new PointF(17, 3), new PointF(21, 7), new PointF(21, 21), new PointF(4, 21) }); path.CloseFigure(); g.DrawPath(p, path);
                        }
                        R(g, p, 8, 13, 9, 8, 0.5f); g.DrawLine(p, 8, 3, 8, 8); g.DrawLine(p, 8, 8, 15, 8); g.DrawLine(p, 15, 8, 15, 3); break;
                    case Ico.Search:
                        g.DrawEllipse(p, 3.5f, 3.5f, 12.5f, 12.5f); g.DrawLine(p, 14.2f, 14.2f, 20.5f, 20.5f); break;
                    case Ico.Filter:
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new[] { new PointF(3, 4), new PointF(21, 4), new PointF(14, 12.5f), new PointF(14, 19.5f), new PointF(10, 21), new PointF(10, 12.5f) }); path.CloseFigure(); g.DrawPath(p, path);
                        }
                        break;
                    case Ico.Check:
                        g.DrawLines(p, new[] { new PointF(4.5f, 12.5f), new PointF(9.5f, 17.5f), new PointF(19.5f, 6.5f) }); break;
                    case Ico.Warning:
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new[] { new PointF(12, 3), new PointF(22, 20.5f), new PointF(2, 20.5f) }); path.CloseFigure(); g.DrawPath(p, path);
                        }
                        g.DrawLine(p, 12, 9.5f, 12, 14); g.FillEllipse(b, 11, 16.3f, 2, 2); break;
                    case Ico.Swap:
                        g.DrawLine(p, 4, 8, 20, 8); g.DrawLines(p, new[] { new PointF(16, 4), new PointF(20, 8), new PointF(16, 12) });
                        g.DrawLine(p, 20, 16, 4, 16); g.DrawLines(p, new[] { new PointF(8, 12), new PointF(4, 16), new PointF(8, 20) }); break;
                    case Ico.Close:
                        g.DrawLine(p, 6, 6, 18, 18); g.DrawLine(p, 18, 6, 6, 18); break;
                    case Ico.Info:
                        g.DrawEllipse(p, 3, 3, 18, 18); g.DrawLine(p, 12, 11, 12, 16.5f); g.FillEllipse(b, 11, 6.8f, 2, 2); break;
                    case Ico.Download:
                        g.DrawLine(p, 12, 3, 12, 15); g.DrawLines(p, new[] { new PointF(7, 10.5f), new PointF(12, 15.5f), new PointF(17, 10.5f) }); g.DrawLine(p, 4, 20.5f, 20, 20.5f); break;
                    case Ico.Archive:
                        R(g, p, 3, 4, 18, 5, 1); g.DrawLines(p, new[] { new PointF(4.5f, 9), new PointF(4.5f, 20), new PointF(19.5f, 20), new PointF(19.5f, 9) }); g.DrawLine(p, 10, 13, 14, 13); break;
                    case Ico.Link:
                        g.DrawArc(p, 2.5f, 8, 11, 8, 90, 180); g.DrawArc(p, 10.5f, 8, 11, 8, 270, 180); g.DrawLine(p, 8, 12, 16, 12); break;
                }
            }
            g.Restore(st);
        }

        static void R(Graphics g, Pen p, float x, float y, float w, float h, float r)
        {
            using (var path = Theme.Round(new RectangleF(x, y, w, h), r)) g.DrawPath(p, path);
        }

        // Icon on a rounded tinted tile (the 34x34 "ico" squares of the mockup).
        public static void Tile(Graphics g, Ico ico, Rectangle r, Color tint)
        {
            Theme.FillRound(g, Theme.Alpha(tint, 40), r, 8);
            Draw(g, ico, new RectangleF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.24f, r.Width * 0.52f, r.Height * 0.52f), Theme.Mix(tint, Color.White, 0.25f), 1.7f);
        }
    }
}
