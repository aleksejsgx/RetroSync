using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RetroSync
{
    // Small painting helpers shared by the overview cards.
    static class CardPaint
    {
        public static void Header(Graphics g, Control card, Ico ico, Color tint, string title, string sub, string pill, Theme.BadgeKind pillKind)
        {
            Icons.Tile(g, ico, new Rectangle(16, 14, 34, 34), tint);
            int right = card.Width - 16;
            if (!string.IsNullOrEmpty(pill))
            {
                int pw = Theme.Badge(g, pill, pillKind, 0, 0, true);
                Theme.Badge(g, pill, pillKind, right - pw, 26);
                right -= pw + 8;
            }
            Theme.Text1(g, title, Theme.S135, Theme.Text, new Rectangle(60, 13, right - 60, 20));
            if (!string.IsNullOrEmpty(sub)) Theme.Text1(g, sub, Theme.F115, Theme.Muted, new Rectangle(60, 32, right - 60, 17));
        }

        // "C:\Games\RetroArch" -> "C:\Games\…\RetroArch" when it does not fit
        public static string FitPath(string p, Font f, int w)
        {
            if (string.IsNullOrEmpty(p) || Theme.Measure(p, f).Width <= w) return p;
            char sep = p.Contains("\\") ? '\\' : '/';
            var parts = p.Split(sep).ToList();
            for (int keep = parts.Count - 2; keep >= 1; keep--)
            {
                for (int head = Math.Min(2, keep); head >= 1; head--)
                {
                    int tail = Math.Min(parts.Count - head, keep - head + 1);
                    string t = string.Join(sep.ToString(), parts.Take(head)) + sep + "\u2026" + sep + string.Join(sep.ToString(), parts.Skip(parts.Count - tail));
                    if (Theme.Measure(t, f).Width <= w) return t;
                }
            }
            return "\u2026" + sep + parts.Last();
        }

        public static void Kv(Graphics g, int x, int y, int keyW, int w, string k, string v, bool mono = false, Color? vc = null)
        {
            Theme.Text1(g, k, Theme.F125, Theme.Muted, new Rectangle(x, y, keyW, 20));
            if (mono) v = FitPath(v, Theme.Mono11, w - keyW - 4);
            Theme.Text1(g, v ?? "\u2014", mono ? Theme.Mono11 : Theme.F125, vc ?? Theme.TextSoft, new Rectangle(x + keyW, y, w - keyW, 20),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        public static Theme.BadgeKind StatusKind(string st)
        {
            switch (st) { case "ok": return Theme.BadgeKind.Ok; case "dry": return Theme.BadgeKind.Conflict; case "error": return Theme.BadgeKind.Bad; default: return Theme.BadgeKind.Neutral; }
        }

        public static string StatusText(string st)
        {
            switch (st) { case "ok": return Lang.T("Done"); case "dry": return Lang.T("Dry-run"); case "error": return Lang.T("Error"); case "cancel": return Lang.T("Cancelled", "Annullato"); default: return Lang.T("Info"); }
        }

        public static string Free(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return null;
                var d = new DriveInfo(root);
                if (!d.IsReady) return null;
                return Util.Size(d.AvailableFreeSpace) + " of " + Util.Size(d.TotalSize);
            }
            catch { return null; }
        }
    }

    class DashboardPage : Page
    {
        readonly CardPanel pcCard, psCard, lastCard, actions, recent, issues;
        readonly DarkButton tSaves, tConfig, tPs2, tBackup, all, preview, wake, wakeSync, rest, openLog;
        string pcFree;

        public DashboardPage(MainWindow w) : base(w, "dashboard", "Dashboard")
        {
            pcCard = new CardPanel { PaintExtra = PaintPc };
            psCard = new CardPanel { PaintExtra = PaintPs5 };
            lastCard = new CardPanel { PaintExtra = PaintLast };
            actions = new CardPanel(Lang.T("Quick actions")) { Hint = Lang.T("Every action shows a preview first \u2014 nothing is written without confirmation") };
            recent = new CardPanel(Lang.T("Recent activity")) { PaintExtra = PaintRecent };
            issues = new CardPanel { PaintExtra = PaintIssues };
            foreach (var c in new[] { pcCard, psCard, lastCard, actions, recent, issues }) Controls.Add(c);

            tSaves = Tile(Lang.T("Sync saves"), Ico.Save, Theme.Accent, () => W.StartRetroArch(SyncScope.Saves));
            tConfig = Tile(Lang.T("Sync RetroArch"), Ico.Gamepad, Theme.Blue, () => W.StartRetroArch(SyncScope.Config));
            tPs2 = Tile(Lang.T("PS2 memory card"), Ico.Card, Theme.Violet, () => W.OpenPs2(true));
            tBackup = Tile(Lang.T("PS5 backup"), Ico.Database, Theme.Warn, () => { W.Navigate("backup"); W.Backup.StartMirror(); });
            all = new DarkButton(Lang.T("Sync all\u2026", "Sincronizza tutto\u2026"), BtnKind.Primary, Ico.Sync);
            all.Click += (s, e) => W.StartRetroArch(SyncScope.All);
            preview = new DarkButton(Lang.T("Preview only"), BtnKind.Normal, Ico.Eye);
            preview.Click += (s, e) => { W.SetDryRun(true); W.StartRetroArch(SyncScope.All); };
            wake = new DarkButton(Lang.T("Wake PS5"), BtnKind.Normal, Ico.Power);
            wake.Click += (s, e) => W.Wake(false);
            wakeSync = new DarkButton(Lang.T("Wake and sync"), BtnKind.Normal, Ico.Power);
            wakeSync.Click += (s, e) => W.Wake(true);
            rest = new DarkButton(Lang.T("Rest mode"), BtnKind.Normal, Ico.Power);
            rest.Click += (s, e) => W.Rest();
            foreach (var b in new[] { all, preview, rest, wake, wakeSync }) { b.BackColor = Theme.Card; actions.Controls.Add(b); }
            openLog = new DarkButton(Lang.T("Open Log"), BtnKind.Normal, null, true) { BackColor = Theme.Card };
            openLog.Click += (s, e) => W.Navigate("log");
            recent.Controls.Add(openLog);
            ActivityLog.Changed += () => W.Ui(() => { lastCard.Invalidate(); recent.Invalidate(); UpdateTiles(); });
        }

        DarkButton Tile(string title, Ico ico, Color tint, Action click)
        {
            var b = new DarkButton(title, BtnKind.Normal, ico) { SubText = "", TileTint = tint, Height = 52, BackColor = Theme.Card };
            b.Click += (s, e) => click();
            actions.Controls.Add(b);
            return b;
        }

        public override string Subtitle { get { return Lang.T("Overview of PC, PS5 and recent syncs"); } }

        public override void Entered()
        {
            pcFree = Demo.On ? "412 GB of 2 TB" : CardPaint.Free(S.PcRetroArchRoot);
            UpdateTiles();
            Invalidate(true);
        }

        public override void StatusChanged() { UpdateTiles(); psCard.Invalidate(); issues.Invalidate(); }
        public override void BusyChanged(bool busy) { foreach (var b in new[] { tSaves, tConfig, tPs2, tBackup, all, preview, rest, wake, wakeSync }) b.Enabled = !busy; }
        public override void SettingsChanged() { Entered(); }

        void UpdateTiles()
        {
            var ra = W.RetroArch;
            tSaves.SubText = ra.HasPlan && ra.Selected > 0 ? ra.Selected + Lang.T(" changes pending", " changes in attesa") : Lang.T("Saves and states");
            tConfig.SubText = "Config, core, shader, playlist";
            tPs2.SubText = "\u201C" + S.Ps2.CardName + "\u201D";
            var lb = ActivityLog.Last(a => a.Kind == "Backup" && a.Title.StartsWith("Mirror") && a.Status != "dry");
            tBackup.SubText = lb == null ? Lang.T("Full mirror, read-only") : Lang.T("Last: ", "Ultimo: ") + ActivityLog.Ago(lb.Time);
            wake.Text = Lang.T("Wake PS5"); wakeSync.Text = Lang.T("Wake and sync"); rest.Text = Lang.T("Rest mode");
            wake.Width = wake.PreferredWidth; wakeSync.Width = wakeSync.PreferredWidth; rest.Width = rest.PreferredWidth;
            foreach (var b in new[] { tSaves, tConfig, tPs2, tBackup }) b.Invalidate();
            if (Ready && Width > 0) DoLayout();
        }

        protected override void DoLayout()
        {
            int w = Width, gap = 14;
            int cw = (w - 2 * gap) / 3;
            pcCard.SetBounds(0, 0, cw, 126);
            psCard.SetBounds(cw + gap, 0, cw, 126);
            lastCard.SetBounds(2 * (cw + gap), 0, w - 2 * (cw + gap), 126);
            actions.SetBounds(0, 140, w, 152);
            int tw = (w - 32 - 3 * 12) / 4;
            var tiles = new[] { tSaves, tConfig, tPs2, tBackup };
            for (int i = 0; i < 4; i++) tiles[i].SetBounds(16 + i * (tw + 12), 44, tw, 52);
            all.Location = new Point(16, 106);
            preview.Location = new Point(all.Right + 10, 106);
            wakeSync.Location = new Point(w - 16 - wakeSync.Width, 106);
            wake.Location = new Point(wakeSync.Left - 10 - wake.Width, 106);
            rest.Location = new Point(wake.Left - 10 - rest.Width, 106);
            int top = 306, h = Math.Max(150, Height - top);
            int rw = (int)(w * 0.58);
            recent.SetBounds(0, top, rw, h);
            issues.SetBounds(rw + gap, top, w - rw - gap, h);
            openLog.Location = new Point(recent.Width - 16 - openLog.Width, 10);
        }

        // ------------------------------------------------------------------ cards
        void PaintPc(Graphics g)
        {
            bool raOk = Demo.On || Directory.Exists(S.PcRetroArchRoot);
            CardPaint.Header(g, pcCard, Ico.Monitor, Theme.Accent, "PC Windows", W.MachineName, raOk ? Lang.T("Ready") : Lang.T("Needs attention"), raOk ? Theme.BadgeKind.Ok : Theme.BadgeKind.Bad);
            int y = 60, cw = pcCard.Width - 32;
            CardPaint.Kv(g, 16, y, 92, cw, "RetroArch", S.PcRetroArchRoot, true, raOk ? (Color?)null : Theme.Bad); y += 19;
            CardPaint.Kv(g, 16, y, 92, cw, "PCSX2", S.Ps2.LocalDir, true); y += 19;
            CardPaint.Kv(g, 16, y, 92, cw, Lang.T("Free space"), pcFree ?? "\u2014");
        }

        void PaintPs5(Graphics g)
        {
            var st = W.Status;
            string pill; Theme.BadgeKind k;
            switch (st.Power)
            {
                case Ps5Power.FtpOnline: pill = Lang.T("Online"); k = Theme.BadgeKind.Ok; break;
                case Ps5Power.RestMode: pill = Lang.T("In rest"); k = Theme.BadgeKind.Down; break;
                case Ps5Power.AwakeNoFtp: pill = Lang.T("FTP down"); k = Theme.BadgeKind.Conflict; break;
                case Ps5Power.Offline: pill = Lang.T("Offline"); k = Theme.BadgeKind.Bad; break;
                default: pill = "\u2026"; k = Theme.BadgeKind.Neutral; break;
            }
            CardPaint.Header(g, psCard, Ico.Console, Theme.Blue, "PS5 (jailbreak)", "FTP " + S.FtpHost + ":" + S.FtpPort, pill, k);
            int y = 60, cw = psCard.Width - 32;
            CardPaint.Kv(g, 16, y, 92, cw, "Homebrew", S.Ps5RetroArchRoot, true); y += 19;
            string fw = st.Ddp != null && st.Ddp.Firmware.Length > 0 ? st.Ddp.Firmware + (st.Ddp.HostName.Length > 0 ? " \u00B7 " + st.Ddp.HostName : "") : "\u2014";
            CardPaint.Kv(g, 16, y, 92, cw, "Firmware", fw); y += 19;
            bool wk = S.Wake.IsConfigured();
            CardPaint.Kv(g, 16, y, 92, cw, Lang.T("Wake"), wk ? Lang.T("configured") + (S.Wake.Nickname.Length > 0 ? " \u00B7 " + S.Wake.Nickname : "") : Lang.T("not configured"), false, wk ? (Color?)null : Theme.Muted);
        }

        static bool IsSync(ActivityEntry a) { return (a.Kind == "Saves" || a.Kind == "RetroArch" || a.Kind == "All" || a.Kind == "PS2" || a.Kind == "Dolphin") && (a.Status == "ok" || a.Status == "error") && a.Title != "Preview" && a.Title != "Analysis"; }

        void PaintLast(Graphics g)
        {
            var a = ActivityLog.Last(IsSync);
            if (a == null)
            {
                CardPaint.Header(g, lastCard, Ico.Clock, Theme.Muted, Lang.T("Last sync"), Lang.T("never"), null, Theme.BadgeKind.Neutral);
                Theme.Text1(g, Lang.T("No sync recorded yet."), Theme.F125, Theme.Muted, new Rectangle(16, 60, lastCard.Width - 32, 20));
                return;
            }
            CardPaint.Header(g, lastCard, Ico.Clock, Theme.Ok, Lang.T("Last sync"), ActivityLog.Ago(a.Time), a.Status == "ok" ? "OK" : Lang.T("Errors"), a.Status == "ok" ? Theme.BadgeKind.Ok : Theme.BadgeKind.Bad);
            int y = 60, cw = lastCard.Width - 32;
            CardPaint.Kv(g, 16, y, 92, cw, Lang.T("Type"), a.Kind == "PS2" ? "PS2 memory card" : a.Kind == "Dolphin" ? "Dolphin \u21C4 Porpoise" : "RetroArch \u00B7 " + a.Kind); y += 19;
            CardPaint.Kv(g, 16, y, 92, cw, Lang.T("Transferred"), a.Files > 0 ? a.Files + (a.Files == 1 ? " file" : " files") + (a.Bytes > 0 ? " \u00B7 " + Util.Size(a.Bytes) : "") : a.Detail); y += 19;
            Theme.Text1(g, Lang.T("Direction"), Theme.F125, Theme.Muted, new Rectangle(16, y, 92, 20));
            int x = 108;
            x += Theme.Badge(g, "PC \u2192 PS5 " + a.ToPs5, Theme.BadgeKind.Up, x, y + 10) + 6;
            Theme.Badge(g, "PS5 \u2192 PC " + a.ToPc, Theme.BadgeKind.Down, x, y + 10);
        }

        void PaintRecent(Graphics g)
        {
            var items = ActivityLog.Recent(Math.Max(1, (recent.Height - 56) / 30));
            int y = 50, w = recent.Width;
            if (items.Count == 0) { Theme.Text1(g, Lang.T("No activity yet."), Theme.F125, Theme.Muted, new Rectangle(16, y + 4, w - 32, 22)); return; }
            using (var p = new Pen(Theme.Line))
                foreach (var a in items)
                {
                    g.DrawLine(p, 16, y, w - 16, y);
                    var r = new Rectangle(16, y, w - 32, 30);
                    Theme.Text1(g, ActivityLog.When(a.Time), Theme.F125, Theme.Muted, new Rectangle(r.X, r.Y, 50, 30));
                    Theme.Text1(g, a.Kind, Theme.F125, Theme.TextSoft, new Rectangle(r.X + 52, r.Y, 92, 30));
                    int bw = Theme.Badge(g, CardPaint.StatusText(a.Status), CardPaint.StatusKind(a.Status), 0, 0, true);
                    Theme.Badge(g, CardPaint.StatusText(a.Status), CardPaint.StatusKind(a.Status), r.Right - bw, r.Y + 15);
                    string text = a.Title.Length > 0 && a.Detail.Length > 0 ? a.Title + " \u00B7 " + a.Detail : a.Title + a.Detail;
                    Theme.Text1(g, text, Theme.F125, Theme.Text, new Rectangle(r.X + 148, r.Y, r.Width - 148 - bw - 10, 30));
                    y += 30;
                }
        }

        void PaintIssues(Graphics g)
        {
            Icons.Draw(g, Ico.Warning, new RectangleF(16, 15, 17, 17), Theme.Warn, 1.7f);
            Theme.Text1(g, Lang.T("Needs attention"), Theme.S135, Theme.Text, new Rectangle(42, 13, issues.Width - 60, 22));
            var rows = new List<KeyValuePair<Theme.BadgeKind, string>>();
            var st = W.Status;
            if (st.Power == Ps5Power.Offline) rows.Add(new KeyValuePair<Theme.BadgeKind, string>(Theme.BadgeKind.Bad, "PS5 unreachable at " + S.FtpHost + ":" + S.FtpPort));
            else if (st.Power == Ps5Power.AwakeNoFtp) rows.Add(new KeyValuePair<Theme.BadgeKind, string>(Theme.BadgeKind.Conflict, "PS5 awake but FTP is not answering: start the FTP payload"));
            else if (st.Power == Ps5Power.RestMode) rows.Add(new KeyValuePair<Theme.BadgeKind, string>(Theme.BadgeKind.Down, S.Wake.IsConfigured() ? "PS5 in rest mode: usa \u201CWake PS5\u201D" : "PS5 in rest mode: set up remote power-on"));
            foreach (var it in W.RetroArch.LastIssues)
            {
                var k = RetroArchPage.KindOf(it);
                rows.Add(new KeyValuePair<Theme.BadgeKind, string>(k, (it.Display ?? "").Split('/').Last() + " \u00B7 " + it.Reason));
            }
            if (!S.Wake.IsConfigured() && st.Power != Ps5Power.RestMode) rows.Add(new KeyValuePair<Theme.BadgeKind, string>(Theme.BadgeKind.Info, "Remote power-on not configured (Settings)"));
            if (S.Dolphin == null || string.IsNullOrEmpty(S.Dolphin.PcUserDir)) rows.Add(new KeyValuePair<Theme.BadgeKind, string>(Theme.BadgeKind.Info, "Dolphin \u21C4 Porpoise: set the Dolphin User folder in Settings"));
            int y = 46, max = Math.Max(1, (issues.Height - 52) / 28);
            foreach (var r in rows.Take(max))
            {
                string tag = r.Key == Theme.BadgeKind.Conflict ? "Conflict" : r.Key == Theme.BadgeKind.Down ? "PS5 \u2192 PC" : r.Key == Theme.BadgeKind.Up ? "PC \u2192 PS5" : r.Key == Theme.BadgeKind.Merge ? "Merge" : r.Key == Theme.BadgeKind.Bad ? "Offline" : "Info";
                if (r.Key == Theme.BadgeKind.Down && r.Value.StartsWith("PS5 in rest mode")) tag = "Rest";
                if (r.Key == Theme.BadgeKind.Conflict && r.Value.StartsWith("PS5 awake")) tag = "FTP";
                int bw = Theme.Badge(g, tag, r.Key, 16, y + 11);
                Theme.Text1(g, r.Value, Theme.F125, Theme.TextSoft, new Rectangle(16 + bw + 8, y, issues.Width - 40 - bw, 22));
                y += 28;
            }
        }
    }
}
