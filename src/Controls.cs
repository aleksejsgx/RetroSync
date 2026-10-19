using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

// Custom-drawn dark controls (WinForms has no dark theme of its own). Presentation only.
namespace RetroSync
{
    enum BtnKind { Normal, Primary, Ghost, Danger }

    class DarkButton : Control
    {
        bool hover, down;
        BtnKind kind;
        bool small, selected;
        Ico? icon;

        public DarkButton(string text, BtnKind kind = BtnKind.Normal, Ico? icon = null, bool small = false)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Text = text; this.kind = kind; this.icon = icon; this.small = small;
            Height = small ? 28 : 32;
            Cursor = Cursors.Hand;
            BackColor = Theme.Content;
            Font = small ? Theme.F12 : Theme.F13;
            Width = PreferredWidth;
            TabStop = true;
        }

        public BtnKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public Ico? Icon { get { return icon; } set { icon = value; Invalidate(); } }
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
        public bool LeftAlign;
        public string SubText;    // optional second line (tiles)

        public int PreferredWidth
        {
            get
            {
                var f = kind == BtnKind.Primary ? Theme.Semi(Font.Size) : Font;
                int w = Theme.Measure(Text, f).Width + (small ? 24 : 32);
                if (icon.HasValue) w += small ? 20 : 23;
                return w;
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            g.Clear(BackColor);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color bg, border, fg;
            switch (kind)
            {
                case BtnKind.Primary:
                    bg = hover ? Theme.AccentHi : Theme.Accent; border = Theme.AccentHi; fg = Theme.OnAccent;
                    if (down) bg = Theme.Accent2;
                    break;
                case BtnKind.Ghost:
                    bg = hover ? Theme.Alpha(Color.White, 14) : Color.Transparent; border = Color.Transparent; fg = Theme.TextSoft; break;
                case BtnKind.Danger:
                    bg = hover ? Theme.Alpha(Theme.Bad, 60) : Theme.Alpha(Theme.Bad, 36); border = Theme.Alpha(Theme.Bad, 110); fg = Color.FromArgb(255, 200, 200); break;
                default:
                    bg = down ? Theme.Card2 : hover ? Theme.BtnHover : Theme.Btn; border = Theme.BtnBorder; fg = Theme.Text; break;
            }
            if (selected) { bg = Theme.Alpha(Theme.Accent, hover ? 52 : 38); border = Theme.Alpha(Theme.Accent, 170); fg = Color.FromArgb(150, 236, 226); }
            if (!Enabled)
            {
                if (kind == BtnKind.Primary) { bg = Theme.Mix(Theme.Accent, BackColor, 0.74f); border = bg; fg = Theme.Mix(Theme.OnAccent, bg, 0.35f); }
                else { bg = Theme.Mix(Theme.Btn, BackColor, 0.5f); border = Theme.Mix(Theme.BtnBorder, BackColor, 0.5f); fg = Theme.Dim; }
            }
            if (bg.A > 0) Theme.FillRound(g, bg, r, 5);
            if (border.A > 0) Theme.DrawRound(g, border, new RectangleF(0, 0, Width, Height), 5);

            var font = kind == BtnKind.Primary ? Theme.Semi(Font.Size) : Font;
            int iconW = icon.HasValue ? (small ? 15 : 16) : 0, gap = icon.HasValue ? 7 : 0;
            if (SubText != null)
            {
                // tile layout: icon tile on the left, title + subtitle
                int pad = 12, tile = Height - 18;
                if (icon.HasValue) Icons.Tile(g, icon.Value, new Rectangle(pad, 9, tile, tile), TileTint);
                int tx = pad + (icon.HasValue ? tile + 12 : 0);
                Theme.Text1(g, Text, Theme.S13, Enabled ? Theme.Text : Theme.Dim, new Rectangle(tx, 9, Width - tx - 8, (Height - 18) / 2 + 1));
                Theme.Text1(g, SubText, Theme.F115, Theme.Muted, new Rectangle(tx, Height / 2, Width - tx - 8, (Height - 18) / 2));
                return;
            }
            var ts = Theme.Measure(Text, font);
            int total = iconW + gap + ts.Width;
            int x = LeftAlign ? 12 : (Width - total) / 2;
            if (icon.HasValue) Icons.Draw(g, icon.Value, new RectangleF(x, (Height - iconW) / 2f, iconW, iconW), fg, 1.7f);
            TextRenderer.DrawText(g, Text, font, new Rectangle(x + iconW + gap, 0, Width - x - iconW - gap, Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        public Color TileTint = Theme.Accent;
    }

    enum ChipKind { Ok, Bad, Warn, Neutral, Info }

    class Chip : Control
    {
        ChipKind kind;
        public Chip(string text, ChipKind kind)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Content; Height = 26; Font = Theme.F12;
            Set(text, kind);
        }

        public void Set(string text, ChipKind k)
        {
            Text = text; kind = k;
            Width = Theme.Measure(text, Font).Width + 40;
            Invalidate();
        }

        public ChipKind Kind { get { return kind; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            Color c, fg;
            switch (kind)
            {
                case ChipKind.Ok: c = Theme.Ok; fg = Color.FromArgb(191, 240, 208); break;
                case ChipKind.Bad: c = Theme.Bad; fg = Color.FromArgb(255, 201, 201); break;
                case ChipKind.Warn: c = Theme.Warn; fg = Color.FromArgb(255, 226, 168); break;
                case ChipKind.Info: c = Theme.Blue; fg = Color.FromArgb(190, 220, 255); break;
                default: c = Theme.Muted; fg = Theme.TextSoft; break;
            }
            var r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, Theme.Alpha(c, 26), r, Height / 2f);
            Theme.DrawRound(g, Theme.Alpha(c, 90), r, Height / 2f);
            Theme.Dot(g, c, 15, Height / 2f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(26, 0, Width - 30, Height), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    class Toggle : Control
    {
        bool on;
        public event EventHandler CheckedChanged;
        public Toggle(string label, bool on)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Content; Font = Theme.F125; Height = 24; Cursor = Cursors.Hand;
            Text = label; this.on = on;
            Width = 40 + (string.IsNullOrEmpty(label) ? 0 : 9 + Theme.Measure(label, Font).Width + 2);
        }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
        }

        public void SetSilently(bool v) { on = v; Invalidate(); }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var r = new RectangleF(0.5f, (Height - 20) / 2f, 39, 20);
            if (on) { Theme.FillRound(g, Enabled ? Theme.Accent : Theme.Mix(Theme.Accent, BackColor, 0.6f), r, 10); }
            else Theme.DrawRound(g, Enabled ? Color.FromArgb(138, 146, 153) : Theme.Dim, new RectangleF(0, (Height - 20) / 2f, 40, 20), 10, 1.2f);
            float kx = on ? 24 : 5, ky = (Height - 12) / 2f;
            using (var b = new SolidBrush(on ? Theme.OnAccent : Color.FromArgb(200, 205, 209))) g.FillEllipse(b, kx, ky, 12, 12);
            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Text, Font, new Rectangle(49, 0, Width - 49, Height), Enabled ? Theme.TextSoft : Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    // Rounded card. Children should use BackColor = Theme.Card.
    class CardPanel : Panel
    {
        public string Title, Hint;
#pragma warning disable 649 // optional overrides, kept for future cards
        public Color? BorderColor, FillColor;
#pragma warning restore 649
        public int Radius = 8;

        public CardPanel(string title = null)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Title = title; BackColor = Theme.Content; Padding = new Padding(18, 16, 18, 16);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, FillColor ?? Theme.Card, r, Radius);
            Theme.DrawRound(g, BorderColor ?? Theme.Line, r, Radius);
            if (!string.IsNullOrEmpty(Title))
                Theme.Text1(g, Title, Theme.S135, Theme.Text, new Rectangle(18, 14, Width - 36, 22));
            if (!string.IsNullOrEmpty(Hint))
                Theme.Text1(g, Hint, Theme.F12, Theme.Muted, new Rectangle(18, 14, Width - 36, 22), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (PaintExtra != null) PaintExtra(g);
        }

        public Action<Graphics> PaintExtra;
    }

    static class Ui
    {
        public static Label Label(Control parent, string text, Font f, Color c, int x, int y, int w = 0, int h = 0)
        {
            var l = new Label
            {
                Text = text, Font = f, ForeColor = c, Location = new Point(x, y), AutoSize = w == 0,
                BackColor = parent is CardPanel ? ((CardPanel)parent).FillColor ?? Theme.Card : parent.BackColor,
                UseMnemonic = false, AutoEllipsis = w != 0
            };
            if (w != 0) l.Size = new Size(w, h == 0 ? f.Height + 4 : h);
            parent.Controls.Add(l);
            return l;
        }

        public static T Add<T>(Control parent, T c, int x, int y) where T : Control
        {
            c.Location = new Point(x, y);
            if (parent is CardPanel && !(c is TextBox)) c.BackColor = ((CardPanel)parent).FillColor ?? Theme.Card;
            parent.Controls.Add(c);
            return c;
        }

        public static TextBox LogBox(Control parent)
        {
            var t = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Theme.Field, ForeColor = Theme.TextSoft, Font = Theme.Mono11, WordWrap = true
            };
            t.HandleCreated += (s, e) => Theme.DarkScrollbars(t);
            parent.Controls.Add(t);
            return t;
        }
    }

    // TextBox inside a rounded dark frame.
    class DarkTextField : Panel
    {
        public readonly TextBox Box;
        bool focus;
        string placeholder;
        public DarkButton Browse;

        public DarkTextField(string text, string placeholder = null, bool password = false)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card; Height = 32;
            this.placeholder = placeholder;
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.F13, Text = text ?? "", UseSystemPasswordChar = password };
            Controls.Add(Box);
            Box.GotFocus += (s, e) => { focus = true; Invalidate(); };
            Box.LostFocus += (s, e) => { focus = false; Invalidate(); };
            Box.TextChanged += (s, e) => Invalidate();
            Click += (s, e) => Box.Focus();
        }

        public override string Text { get { return Box == null ? "" : Box.Text; } set { if (Box != null) Box.Text = value ?? ""; } }
        public bool ReadOnly { get { return Box.ReadOnly; } set { Box.ReadOnly = value; Box.ForeColor = value ? Theme.Muted : Theme.Text; } }

        public DarkButton AddBrowse(Action onClick)
        {
            Browse = new DarkButton("\u2026", BtnKind.Normal, null, true) { Width = 34, Height = 24 };
            Browse.BackColor = Theme.Field;
            Browse.Click += (s, e) => onClick();
            Controls.Add(Browse);
            OnLayout(new LayoutEventArgs(this, ""));
            return Browse;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Box == null) return;
            int right = Browse != null ? Browse.Width + 8 : 0;
            Box.SetBounds(10, (Height - Box.PreferredHeight) / 2 + 1, Width - 20 - right, Box.PreferredHeight);
            if (Browse != null) Browse.SetBounds(Width - Browse.Width - 4, (Height - 24) / 2, Browse.Width, 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, Theme.Field, r, 5);
            Theme.DrawRound(g, focus ? Theme.Accent : Theme.BtnBorder, r, 5);
            if (focus) using (var p = new Pen(Theme.Accent, 2)) g.DrawLine(p, 5, Height - 1, Width - 5, Height - 1);
            if (Box.Text.Length == 0 && !focus && !string.IsNullOrEmpty(placeholder))
                TextRenderer.DrawText(g, placeholder, Theme.F13, new Rectangle(10, 0, Width - 20, Height), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // Placeholder painting inside the TextBox itself does not work reliably, so the search field
    // draws its icon and hint on the frame and the TextBox stays transparent-looking.
    class SearchField : DarkTextField
    {
        public SearchField() : base("", "Cerca file\u2026") { }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Box != null) Box.SetBounds(32, (Height - Box.PreferredHeight) / 2 + 1, Width - 40, Box.PreferredHeight);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, Theme.Field, r, 5);
            Theme.DrawRound(g, Box.Focused ? Theme.Accent : Theme.BtnBorder, r, 5);
            Icons.Draw(g, Ico.Search, new RectangleF(10, (Height - 15) / 2f, 15, 15), Theme.Muted);
            if (Box.Text.Length == 0 && !Box.Focused)
                TextRenderer.DrawText(g, "Cerca file\u2026", Theme.F13, new Rectangle(32, 0, Width - 40, Height), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    class Segmented : Control
    {
        public readonly List<string> Items = new List<string>();
        public readonly List<Ico> Icns = new List<Ico>();
        int sel = -1, hover = -1;
        public event Action<int> ItemClicked;
        int[] xs = new int[0];

        public Segmented()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Content; Height = 36; Font = Theme.F13; Cursor = Cursors.Hand;
        }

        public void Add(string text, Ico ico) { Items.Add(text); Icns.Add(ico); Relayout(); }
        public int SelectedIndex { get { return sel; } set { sel = value; Invalidate(); } }

        void Relayout()
        {
            xs = new int[Items.Count + 1];
            int x = 4;
            for (int i = 0; i < Items.Count; i++) { xs[i] = x; x += Theme.Measure(Items[i], Font).Width + 46; }
            xs[Items.Count] = x;
            Width = x + 4;
            Invalidate();
        }

        int HitTest(int x) { for (int i = 0; i < Items.Count; i++) if (x >= xs[i] && x < xs[i + 1]) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e) { int h = HitTest(e.X); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int h = HitTest(e.X);
            if (h >= 0 && Enabled && ItemClicked != null) ItemClicked(h);
            base.OnMouseClick(e);
        }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, Theme.Card, r, 6);
            Theme.DrawRound(g, Theme.Line, r, 6);
            for (int i = 0; i < Items.Count; i++)
            {
                var ir = new RectangleF(xs[i], 3, xs[i + 1] - xs[i], Height - 6);
                Color fg = Enabled ? Theme.TextSoft : Theme.Dim;
                if (i == sel)
                {
                    Theme.FillRound(g, Theme.Alpha(Theme.Accent, 36), ir, 5);
                    Theme.DrawRound(g, Theme.Alpha(Theme.Accent, 150), ir, 5);
                    fg = Color.FromArgb(150, 236, 226);
                }
                else if (i == hover && Enabled) Theme.FillRound(g, Theme.Alpha(Color.White, 12), ir, 5);
                Icons.Draw(g, Icns[i], new RectangleF(ir.X + 13, (Height - 15) / 2f, 15, 15), fg);
                TextRenderer.DrawText(g, Items[i], Font, new Rectangle((int)ir.X + 34, 0, (int)ir.Width - 36, Height), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }
    }

    class DarkProgress : Control
    {
        int v;
        public DarkProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 4; BackColor = Theme.Card;
        }
        public int Value { get { return v; } set { int n = Math.Max(0, Math.Min(100, value)); if (n != v) { v = n; Invalidate(); } } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            Theme.FillRound(g, Theme.Alpha(Color.White, 20), new RectangleF(0, 0, Width, Height), Height / 2f);
            if (v > 0) Theme.FillRound(g, Theme.Accent, new RectangleF(0, 0, Math.Max(Height, Width * v / 100f), Height), Height / 2f);
        }
    }

    // ------------------------------------------------------------------ grid
    class GridColumn
    {
        public string Header;
        public int Width;
        public bool Fill;
        public GridColumn(string h, int w, bool fill = false) { Header = h; Width = w; Fill = fill; }
    }

    enum RowLook { Normal, Muted, Conflict }

    class DarkGrid : Control
    {
        public readonly List<GridColumn> Columns = new List<GridColumn>();
        List<object> rows = new List<object>();
        public int RowHeight = 40, HeaderHeight = 34;
        public bool CheckColumn;
        public Func<object, int> CheckState;           // 1 checked, 0 unchecked, -1 not checkable
        public int HeaderCheck;                        // 1, 0 or 2 (mixed)
        public event Action<object> CheckClicked;
        public event EventHandler HeaderCheckClicked;
        public Action<Graphics, Rectangle, object, int> PaintCell;
        public Func<object, RowLook> LookOf;
        public event EventHandler SelectionChanged;
        public event Action<object> RowActivated;
        public string EmptyText = "";
        int top, sel = -1, hover = -1;
        bool dragThumb; int dragY, dragTop;

        public DarkGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Card; Font = Theme.F13; TabStop = true;
        }

        public IList<object> Rows { get { return rows; } }
        public void SetRows(IEnumerable<object> r, bool keepSelection)
        {
            object old = SelectedRow;
            rows = r.ToList();
            sel = keepSelection && old != null ? rows.IndexOf(old) : -1;
            if (sel < 0 && rows.Count > 0 && keepSelection) sel = 0;
            ClampTop(); Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public object SelectedRow { get { return sel >= 0 && sel < rows.Count ? rows[sel] : null; } }
        public void Select(int i) { sel = Math.Max(-1, Math.Min(rows.Count - 1, i)); EnsureVisible(sel); Invalidate(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }

        int ContentHeight { get { return rows.Count * RowHeight; } }
        int ViewHeight { get { return Math.Max(0, Height - HeaderHeight); } }
        void ClampTop() { top = Math.Max(0, Math.Min(top, Math.Max(0, ContentHeight - ViewHeight))); }
        void EnsureVisible(int i)
        {
            if (i < 0) return;
            int y = i * RowHeight;
            if (y < top) top = y;
            else if (y + RowHeight > top + ViewHeight) top = y + RowHeight - ViewHeight;
            ClampTop();
        }

        int[] ColX()
        {
            int fixedW = Columns.Where(c => !c.Fill).Sum(c => c.Width) + (CheckColumn ? 40 : 0);
            int fills = Columns.Count(c => c.Fill);
            int avail = Math.Max(0, Width - 8 - fixedW - 16);
            var xs = new int[Columns.Count + 1];
            int x = 16 + (CheckColumn ? 40 : 0) - (CheckColumn ? 8 : 0);
            for (int i = 0; i < Columns.Count; i++)
            {
                xs[i] = x;
                x += Columns[i].Fill ? Math.Max(80, avail / Math.Max(1, fills)) : Columns[i].Width;
            }
            xs[Columns.Count] = x;
            return xs;
        }

        Rectangle ThumbRect()
        {
            if (ContentHeight <= ViewHeight || ViewHeight <= 0) return Rectangle.Empty;
            int track = ViewHeight - 8;
            int h = Math.Max(28, track * ViewHeight / ContentHeight);
            int y = HeaderHeight + 4 + (track - h) * top / Math.Max(1, ContentHeight - ViewHeight);
            return new Rectangle(Width - 9, y, 5, h);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Space || k == Keys.Home || k == Keys.End || k == Keys.PageDown || k == Keys.PageUp || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int page = Math.Max(1, ViewHeight / RowHeight - 1);
            if (e.KeyCode == Keys.Down) Select(sel + 1);
            else if (e.KeyCode == Keys.Up) Select(Math.Max(0, sel - 1));
            else if (e.KeyCode == Keys.PageDown) Select(sel + page);
            else if (e.KeyCode == Keys.PageUp) Select(Math.Max(0, sel - page));
            else if (e.KeyCode == Keys.Home) Select(0);
            else if (e.KeyCode == Keys.End) Select(rows.Count - 1);
            else if (e.KeyCode == Keys.Space && SelectedRow != null && CheckColumn && CheckClicked != null && CheckState(SelectedRow) >= 0) { CheckClicked(SelectedRow); Invalidate(); }
            else if (e.KeyCode == Keys.Enter && SelectedRow != null && RowActivated != null) RowActivated(SelectedRow);
            base.OnKeyDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            top -= Math.Sign(e.Delta) * RowHeight * 3; ClampTop(); Invalidate(); base.OnMouseWheel(e);
        }

        int RowAt(int y) { if (y < HeaderHeight) return -2; int i = (y - HeaderHeight + top) / RowHeight; return i < rows.Count ? i : -1; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var th = ThumbRect();
            if (!th.IsEmpty && e.X >= Width - 14)
            {
                if (th.Contains(new Point(th.X + 1, e.Y))) { dragThumb = true; dragY = e.Y; dragTop = top; }
                else { top += (e.Y < th.Y ? -1 : 1) * ViewHeight; ClampTop(); Invalidate(); }
                return;
            }
            int i = RowAt(e.Y);
            if (i == -2)
            {
                if (CheckColumn && e.X < 44 && HeaderCheckClicked != null) HeaderCheckClicked(this, EventArgs.Empty);
                return;
            }
            if (i >= 0)
            {
                if (i != sel) { sel = i; if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
                if (CheckColumn && e.X < 44 && e.Button == MouseButtons.Left && CheckState(rows[i]) >= 0 && CheckClicked != null) CheckClicked(rows[i]);
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragThumb)
            {
                var th = ThumbRect(); int track = ViewHeight - 8 - th.Height;
                if (track > 0) { top = dragTop + (e.Y - dragY) * (ContentHeight - ViewHeight) / track; ClampTop(); Invalidate(); }
                return;
            }
            int i = RowAt(e.Y); if (i < 0) i = -1;
            if (i != hover) { hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragThumb = false; base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int i = RowAt(e.Y);
            if (i >= 0 && !(CheckColumn && e.X < 44) && RowActivated != null) RowActivated(rows[i]);
            base.OnMouseDoubleClick(e);
        }
        protected override void OnResize(EventArgs e) { ClampTop(); base.OnResize(e); }

        public static void DrawCheck(Graphics g, int x, int cy, int state)
        {
            var r = new RectangleF(x, cy - 8, 16, 16);
            if (state == 1 || state == 2)
            {
                Theme.FillRound(g, Theme.Accent, r, 4);
                using (var p = new Pen(Theme.OnAccent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    if (state == 1) g.DrawLines(p, new[] { new PointF(x + 4, cy), new PointF(x + 7, cy + 3), new PointF(x + 12.5f, cy - 3.5f) });
                    else g.DrawLine(p, x + 4, cy, x + 12, cy);
                }
            }
            else Theme.DrawRound(g, state < 0 ? Theme.Alpha(Color.FromArgb(138, 146, 153), 70) : Color.FromArgb(138, 146, 153), r, 4, 1.1f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var xs = ColX();
            // header
            using (var b = new SolidBrush(Theme.Alpha(Color.White, 6))) g.FillRectangle(b, 0, 0, Width, HeaderHeight);
            if (CheckColumn) DrawCheck(g, 16, HeaderHeight / 2, HeaderCheck);
            for (int c = 0; c < Columns.Count; c++)
                Theme.Text1(g, Columns[c].Header, Theme.F12, Theme.Muted, new Rectangle(xs[c], 0, xs[c + 1] - xs[c] - 8, HeaderHeight));
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, 0, HeaderHeight - 1, Width, HeaderHeight - 1);

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, HeaderHeight, Width, ViewHeight));
            if (rows.Count == 0 && !string.IsNullOrEmpty(EmptyText))
                TextRenderer.DrawText(g, EmptyText, Theme.F13, new Rectangle(20, HeaderHeight, Width - 40, Math.Min(ViewHeight, 160)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int first = top / RowHeight, last = Math.Min(rows.Count - 1, (top + ViewHeight) / RowHeight);
            for (int i = first; i <= last; i++)
            {
                int y = HeaderHeight + i * RowHeight - top;
                var look = LookOf != null ? LookOf(rows[i]) : RowLook.Normal;
                if (look == RowLook.Conflict) using (var b = new SolidBrush(Theme.Alpha(Theme.Warn, 20))) g.FillRectangle(b, 0, y, Width, RowHeight);
                if (i == sel) using (var b = new SolidBrush(Theme.Alpha(Color.White, 16))) g.FillRectangle(b, 0, y, Width, RowHeight);
                else if (i == hover) using (var b = new SolidBrush(Theme.Alpha(Color.White, 8))) g.FillRectangle(b, 0, y, Width, RowHeight);
                if (look == RowLook.Conflict) using (var b = new SolidBrush(Theme.Warn)) g.FillRectangle(b, 0, y, 3, RowHeight);
                else if (i == sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, y + 8, 3, RowHeight - 16);
                if (CheckColumn) DrawCheck(g, 16, y + RowHeight / 2, CheckState(rows[i]));
                for (int c = 0; c < Columns.Count; c++)
                    if (PaintCell != null) PaintCell(g, new Rectangle(xs[c], y, xs[c + 1] - xs[c] - 8, RowHeight), rows[i], c);
                using (var p = new Pen(Theme.Alpha(Color.White, 13))) g.DrawLine(p, 0, y + RowHeight - 1, Width, y + RowHeight - 1);
            }
            g.Clip = clip;
            var th = ThumbRect();
            if (!th.IsEmpty) Theme.FillRound(g, Theme.Alpha(Color.White, dragThumb ? 90 : 55), th, 2.5f);
        }
    }

    // ------------------------------------------------------------------ dark modal dialog
    class DarkDialog : Form
    {
        DialogResult result = DialogResult.Cancel;

        DarkDialog(string title, string message, string okText, string cancelText, Ico ico, Color tint, string thirdText)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Card; ForeColor = Theme.Text; Font = Theme.F13;
            HandleCreated += (s, e) => Theme.DarkTitleBar(Handle);
            int w = 520;
            var msgSize = TextRenderer.MeasureText(message, Theme.F13, new Size(w - 92, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int msgH = Math.Min(460, msgSize.Height + 6);
            ClientSize = new Size(w, 24 + 28 + 10 + msgH + 22 + 52);
            var head = new Panel { Location = new Point(0, 0), Size = new Size(w, 60 + msgH), BackColor = Theme.Card };
            head.Paint += (s, e) =>
            {
                Theme.Hq(e.Graphics);
                Icons.Tile(e.Graphics, ico, new Rectangle(22, 22, 34, 34), tint);
                Theme.Text1(e.Graphics, title, Theme.S15, Theme.Text, new Rectangle(72, 20, w - 90, 26));
            };
            Controls.Add(head);
            var msg = new Label { Text = message, Font = Theme.F13, ForeColor = Theme.TextSoft, BackColor = Theme.Card, Location = new Point(72, 52), Size = new Size(w - 92, msgH), UseMnemonic = false };
            head.Controls.Add(msg);
            var bar = new Panel { Location = new Point(0, ClientSize.Height - 60), Size = new Size(w, 60), BackColor = Theme.Content };
            Controls.Add(bar);
            int x = w - 20;
            if (cancelText != null)
            {
                var c = new DarkButton(cancelText) { BackColor = Theme.Content };
                c.Width = Math.Max(96, c.PreferredWidth); x -= c.Width; c.Location = new Point(x, 14); x -= 10;
                c.Click += (s, e) => { result = DialogResult.Cancel; Close(); };
                bar.Controls.Add(c);
            }
            if (thirdText != null)
            {
                var t = new DarkButton(thirdText) { BackColor = Theme.Content };
                t.Width = Math.Max(96, t.PreferredWidth);
                t.Click += (s, e) => { result = DialogResult.No; Close(); };
                bar.Controls.Add(t);
                x -= t.Width; t.Location = new Point(x, 14); x -= 10;
            }
            var ok = new DarkButton(okText, BtnKind.Primary) { BackColor = Theme.Content };
            ok.Width = Math.Max(96, ok.PreferredWidth); x -= ok.Width; ok.Location = new Point(x, 14);
            ok.Click += (s, e) => { result = thirdText != null ? DialogResult.Yes : DialogResult.OK; Close(); };
            bar.Controls.Add(ok);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { result = DialogResult.Cancel; Close(); } };
            Shown += (s, e) => ok.Focus();
        }

        public static DialogResult Ask(IWin32Window owner, string title, string message, string okText = "Continue", string cancelText = "Cancel", Ico ico = Ico.Info, Color? tint = null)
        {
            using (var d = new DarkDialog(title, message, okText, cancelText, ico, tint ?? Theme.Accent, null))
            {
                d.ShowDialog(owner);
                return d.result;
            }
        }

        // Yes / No / Cancel variant.
        public static DialogResult Ask3(IWin32Window owner, string title, string message, string yesText, string noText, string cancelText, Ico ico, Color tint)
        {
            using (var d = new DarkDialog(title, message, yesText, cancelText, ico, tint, noText))
            {
                d.ShowDialog(owner);
                return d.result;
            }
        }

        public static void Info(IWin32Window owner, string title, string message, Ico ico = Ico.Info, Color? tint = null)
        {
            using (var d = new DarkDialog(title, message, "OK", null, ico, tint ?? Theme.Accent, null)) d.ShowDialog(owner);
        }
    }

    // Dark context menus.
    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dim; base.OnRenderItemText(e); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Muted; base.OnRenderArrow(e); }

        class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card2; } }
            public override Color MenuBorder { get { return Theme.LineStrong; } }
            public override Color MenuItemBorder { get { return Theme.Alpha(Theme.Accent, 120); } }
            public override Color MenuItemSelected { get { return Theme.BtnHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.BtnHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.BtnHover; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card2; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card2; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card2; } }
            public override Color SeparatorDark { get { return Theme.Line; } }
            public override Color SeparatorLight { get { return Theme.Line; } }
        }
    }

    enum BannerKind { Neutral, Info, Warn, Ok, Bad, Busy }

    // One-line status banner: icon + bold title + text (+ optional progress bar).
    class Banner : Control
    {
        public string Title = "", Body = "";
        public BannerKind Kind = BannerKind.Neutral;
        public int RightReserve;   // space kept free on the right (for a button placed over the banner)
        int progress = -1;
        public Banner()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Content; Height = 42;
        }

        public void Set(BannerKind k, string title, string body, int progress = -1)
        {
            Kind = k; Title = title ?? ""; Body = body ?? ""; this.progress = progress; Invalidate();
        }

        public int Progress { get { return progress; } set { if (progress != value) { progress = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            Color c; Ico ico;
            switch (Kind)
            {
                case BannerKind.Warn: c = Theme.Warn; ico = Ico.Warning; break;
                case BannerKind.Ok: c = Theme.Ok; ico = Ico.Check; break;
                case BannerKind.Bad: c = Theme.Bad; ico = Ico.Warning; break;
                case BannerKind.Info: c = Theme.Accent; ico = Ico.Info; break;
                case BannerKind.Busy: c = Theme.Accent; ico = Ico.Sync; break;
                default: c = Theme.Muted; ico = Ico.Info; break;
            }
            var r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, Kind == BannerKind.Neutral ? Theme.Card : Theme.Alpha(c, 24), r, 6);
            Theme.DrawRound(g, Kind == BannerKind.Neutral ? Theme.Line : Theme.Alpha(c, 95), r, 6);
            Icons.Draw(g, ico, new RectangleF(14, (Height - 17) / 2f, 17, 17), c, 1.7f);
            int x = 42;
            int barW = (progress >= 0 ? 160 : 0) + RightReserve;
            if (Title.Length > 0)
            {
                var tw = Theme.Measure(Title, Theme.S13).Width;
                Theme.Text1(g, Title, Theme.S13, Theme.Text, new Rectangle(x, 0, Math.Min(tw + 4, Width - x - barW - 20), Height));
                x += tw + 6;
            }
            if (Body.Length > 0)
                Theme.Text1(g, (Title.Length > 0 ? "\u2014 " : "") + Body, Theme.F13, Kind == BannerKind.Neutral ? Theme.Muted : Theme.TextSoft, new Rectangle(x, 0, Math.Max(10, Width - x - barW - 24), Height));
            if (progress >= 0)
            {
                barW -= RightReserve;
                var pr = new RectangleF(Width - RightReserve - barW - 16, Height / 2f - 2, barW, 4);
                Theme.FillRound(g, Theme.Alpha(Color.White, 22), pr, 2);
                if (progress > 0) Theme.FillRound(g, Theme.Accent, new RectangleF(pr.X, pr.Y, Math.Max(4, barW * progress / 100f), 4), 2);
            }
        }
    }
}
