using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// PS2 page: hosts the v1 memory card window (Ps2CardForm, logic unchanged) inside the nevern window.
// Only presentation is adapted here: title row hidden, dark list header, dark progress bar, card widths.
namespace RetroSync
{
    class Ps2Page : Page
    {
        Ps2CardForm form;
        bool formDry;
        DarkProgress prog;
        readonly Timer progTimer = new Timer { Interval = 120 };
        ProgressBar origBar;
        Panel cardA, cardB;
        bool lastBusy;

        public Ps2Page(MainWindow w) : base(w, "ps2", "PS2 \u00B7 Memory card")
        {
            progTimer.Tick += (s, e) => Poll();
        }

        public override string Subtitle
        {
            get
            {
                var c = S.Ps2;
                string sub = "PCSX2 \u21C4 PS5SX2 \u00B7 \u201C" + c.CardName + "\u201D \u00B7 saves moved with mymc, cards always archived first";
                return (formDry ? "PREVIEW (dry-run): analysis only, no card is written \u00B7 " : "") + sub;
            }
        }

        public override void Entered() { if (form == null) Open(false); }

        // fresh = start a new analysis (also recreates the window if the dry-run state changed)
        public void Open(bool fresh)
        {
            if (form != null && !fresh && formDry == W.DryRun) return;
            if (form != null && form.IsBusy && !Demo.On) return;
            Create();
            if (!Demo.On) form.StartAnalysis();   // no-op if the window already started it on Shown
        }

        void Create()
        {
            if (form != null) { Controls.Remove(form); form.Dispose(); form = null; }
            formDry = W.DryRun;
            origBar = null; cardA = cardB = null; lastBusy = false;
            form = new Ps2CardForm(S, formDry) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill, ShowInTaskbar = false };
            form.LogHook = OnLog;
            // hide the title + subtitle row (the page header shows them), move the rest up
            foreach (Control c in form.Controls.Cast<Control>().ToList())
            {
                if (c is Label && c.Top < 70) { c.Visible = false; continue; }
                c.Top -= 66;
            }
            foreach (Control c in form.Controls)
            {
                var lv = c as ListView;
                if (lv != null) StyleList(lv);
                var tb = c as TextBox;
                if (tb != null) { tb.HandleCreated += (s, e) => Theme.DarkScrollbars(tb); }
                var pb = c as ProgressBar;
                if (pb != null) { origBar = pb; pb.Visible = false; }
            }
            var cards = form.Controls.OfType<Panel>().Where(p => p.Height == 105).OrderBy(p => p.Left).ToList();
            cardA = cards.Count == 2 ? cards[0] : null; cardB = cards.Count == 2 ? cards[1] : null;
            if (origBar != null)
            {
                prog = new DarkProgress();
                prog.Bounds = origBar.Bounds; prog.Anchor = origBar.Anchor; prog.BackColor = Theme.Content;
                form.Controls.Add(prog); prog.BringToFront();
            }
            form.Resize += (s, e) => FitCards();
            foreach (var l in AllLabels(form)) l.UseMnemonic = false;   // "Ratchet & Clank"
            foreach (var b in form.Controls.OfType<Button>())
            {
                var btn = b; var orig = b.BackColor;
                EventHandler upd = (o, e) =>
                {
                    btn.BackColor = btn.Enabled ? orig : Theme.Mix(orig, Theme.Content, 0.65f);
                    btn.FlatAppearance.BorderColor = btn.Enabled ? (orig == Theme.Accent ? Theme.Accent : Theme.BtnBorder) : Theme.Mix(Theme.BtnBorder, Theme.Content, 0.5f);
                };
                btn.EnabledChanged += upd; upd(null, EventArgs.Empty);
            }
            Controls.Add(form);
            if (Demo.On) Demo.FillPs2(form);   // before Show(): the window's own Shown -> Analyze then finds it busy
            form.Show();
            FitCards();
            progTimer.Start();
            W.HeaderChanged();
        }

        void FitCards()
        {
            if (form == null || cardA == null || cardB == null) return;
            int w = form.ClientSize.Width - 40, gap = 20;
            int cw = (w - gap) / 2;
            cardA.Anchor = AnchorStyles.Top | AnchorStyles.Left; cardB.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            cardA.SetBounds(20, cardA.Top, cw, cardA.Height);
            cardB.SetBounds(20 + cw + gap, cardB.Top, cw, cardB.Height);
            foreach (Control c in cardA.Controls) if (c.Width > 100) c.Width = cw - 25;
            foreach (Control c in cardB.Controls) if (c.Width > 100) c.Width = cw - 25;
        }

        static System.Collections.Generic.IEnumerable<Label> AllLabels(Control c)
        {
            foreach (Control x in c.Controls)
            {
                if (x is Label) yield return (Label)x;
                foreach (var y in AllLabels(x)) yield return y;
            }
        }

        // columns scaled to fill the list, so no light header area is left on the right
        static void FitColumns(ListView lv, int[] baseW)
        {
            int total = baseW.Sum(), avail = lv.ClientSize.Width - 2;
            if (avail <= 0 || lv.Columns.Count != baseW.Length) return;
            int used = 0;
            for (int i = 0; i < baseW.Length; i++)
            {
                int w = i == baseW.Length - 1 ? avail - used : baseW[i] * avail / total;
                lv.Columns[i].Width = Math.Max(30, w); used += w;
            }
        }

        static void StyleList(ListView lv)
        {
            var baseW = lv.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
            lv.Resize += (s, e) => FitColumns(lv, baseW);
            lv.HandleCreated += (s, e) => FitColumns(lv, baseW);
            lv.OwnerDraw = true;
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Theme.Card2)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.S12, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                bool sel = e.Item.Selected;
                using (var b = new SolidBrush(sel ? Theme.Selection : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Theme.F125, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), e.Item.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            lv.HandleCreated += (s, e) => Theme.DarkScrollbars(lv);
        }

        void Poll()
        {
            if (form == null) return;
            if (origBar != null && prog != null) prog.Value = form.ProgressValue;
            bool b = form.IsBusy;
            if (b != lastBusy && !Demo.On)
            {
                lastBusy = b;
                // the PS2 window runs its own work; keep the rest of the app from starting a second sync meanwhile
                W.SetBusy(b);
            }
        }

        void OnLog(string s)
        {
            W.Log("[PS2] " + s);
            string t = s ?? "";
            if (t.StartsWith("Sync completed"))
                ActivityLog.Add(new ActivityEntry { Kind = "PS2", Title = "PS2 memory card", Detail = "saves synced with mymc", Status = "ok" });
            else if (t.StartsWith("Done"))
                ActivityLog.Add(new ActivityEntry { Kind = "PS2", Title = "PS2 memory card", Detail = "whole card copied", Status = "ok" });
        }

        public override void DryRunChanged()
        {
            if (form != null && (!form.IsBusy || Demo.On) && formDry != W.DryRun && Visible) Open(true);
            W.HeaderChanged();
        }

        public override void SettingsChanged() { if (form != null && !form.IsBusy) { Controls.Remove(form); form.Dispose(); form = null; } }

        protected override void DoLayout() { }
    }
}
