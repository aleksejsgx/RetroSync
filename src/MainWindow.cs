using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

// RetroSync 2.1 main window: sidebar navigation, page host, global header (status chips + dry-run),
// PS5 status polling and the remote power-on flow. The sync engines are used exactly as in v1.
namespace RetroSync
{
    abstract class Page : Panel
    {
        protected readonly MainWindow W;
        public readonly string Key;
        public string Title;

        protected Page(MainWindow w, string key, string title)
        {
            W = w; Key = key; Title = title;
            BackColor = Theme.Content;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public virtual string Subtitle { get { return ""; } }
        public virtual void Entered() { }
        public virtual void BusyChanged(bool busy) { }
        public virtual void StatusChanged() { }
        public virtual void DryRunChanged() { }
        public virtual void SettingsChanged() { }
        protected abstract void DoLayout();
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); if (Ready && Width > 0 && Height > 0) DoLayout(); }
        public bool Ready;   // set by MainWindow once the page is fully constructed
        protected AppSettings S { get { return W.Settings; } }
    }

    class NavItem
    {
        public string Key, Text, Tag;
        public Ico Icon;
        public bool Enabled = true;
        public int Count;
        public Rectangle Bounds;
    }

    class MainWindow : Form
    {
        public AppSettings Settings;
        public bool Busy { get; private set; }
        public bool DryRun;
        public Ps5State Status = new Ps5State { Power = Ps5Power.Unknown };
        public event Action<string> LogAdded;
        public readonly List<string> LogLines = new List<string>();
        public string MachineName = Environment.MachineName;

        const int SideW = 224;
        readonly List<NavItem> nav = new List<NavItem>();
        NavItem hoverNav;
        string current;
        readonly ContentHost host;
        readonly Dictionary<string, Page> pages = new Dictionary<string, Page>();
        readonly Chip chipPc, chipPs5;
        readonly Toggle dryToggle;
        readonly Banner wakeBanner;
        readonly DarkButton wakeCancel;
        bool wakeCancelRequested, wakeRunning, polling;
        Color wakeTint = Theme.Accent;
        readonly Timer pollTimer;

        public DashboardPage Dashboard { get { return (DashboardPage)pages["dashboard"]; } }
        public RetroArchPage RetroArch { get { return (RetroArchPage)pages["retroarch"]; } }
        public Ps2Page Ps2 { get { return (Ps2Page)pages["ps2"]; } }
        public BackupPage Backup { get { return (BackupPage)pages["backup"]; } }

        public MainWindow(string startPage)
        {
            string err;
            Settings = SettingsStore.Load(out err); Lang.Apply(Settings);
            if (Demo.On) Demo.PrepareSettings(Settings);
            Text = "RetroSync 2.1" + (Demo.On ? "  \u2014  DEMO (sample data)" : "");
            ClientSize = new Size(1280, 800);
            MinimumSize = new Size(1120, 720);
            StartPosition = Demo.On ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            if (Demo.On) Location = new Point(0, 0);
            BackColor = Theme.WindowBg; ForeColor = Theme.Text; Font = Theme.F13;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            nav.Add(new NavItem { Key = "dashboard", Text = Lang.T("Dashboard"), Icon = Ico.Dashboard });
            foreach (var m in Modules.All) nav.Add(new NavItem { Key = m.Id, Text = m.NavTitle, Icon = m.Icon, Enabled = m.Available, Tag = m.Available ? null : Lang.T("soon") });
            nav.Add(new NavItem { Key = "backup", Text = Lang.T("Backup"), Icon = Ico.Database });
            nav.Add(new NavItem { Key = "settings", Text = Lang.T("Settings"), Icon = Ico.Gear });
            nav.Add(new NavItem { Key = "log", Text = Lang.T("Log"), Icon = Ico.Log });

            host = new ContentHost(this);
            Controls.Add(host);
            chipPc = new Chip("PC", ChipKind.Neutral);
            chipPs5 = new Chip("PS5 \u2026", ChipKind.Neutral);
            dryToggle = new Toggle(Lang.T("Dry-run"), false);
            dryToggle.CheckedChanged += (s, e) => SetDryRun(dryToggle.Checked);
            host.Controls.Add(chipPc); host.Controls.Add(chipPs5); host.Controls.Add(dryToggle);
            wakeBanner = new Banner { Visible = false };
            wakeCancel = new DarkButton(Lang.T("Cancel"), BtnKind.Normal, null, true) { Visible = false };
            wakeCancel.Click += (s, e) =>
            {
                if (wakeRunning) { wakeCancelRequested = true; wakeCancel.Enabled = false; }
                else HideWake();
            };
            host.Controls.Add(wakeBanner); host.Controls.Add(wakeCancel);

            AddPage(new DashboardPage(this));
            foreach (var m in Modules.All) if (m.Available) AddPage(m.CreatePage(this));
            AddPage(new BackupPage(this));
            AddPage(new SettingsPage(this));
            AddPage(new LogPage(this));

            pollTimer = new Timer { Interval = Math.Max(10, Settings.Ui.StatusPollSeconds) * 1000 };
            pollTimer.Tick += (s, e) => RefreshStatus(false);
            Shown += (s, e) =>
            {
                if (Demo.On) { Demo.Apply(this); }
                else
                {
                    RefreshStatus(true);
                    if (Settings.Ui.StatusPollSeconds > 0) pollTimer.Start();
                }
            };
            FormClosing += (s, e) =>
            {
                if (Busy && e.CloseReason == CloseReason.UserClosing &&
                    DarkDialog.Ask(this, "Operation in progress", "An operation is in progress. Closing now may leave it half-done.\n\nClose anyway?", "Close", "Continue", Ico.Warning, Theme.Warn) != DialogResult.OK)
                    e.Cancel = true;
            };
            UpdateChips();
            Navigate(startPage == "wake" ? "settings" : startPage ?? "dashboard");
            if (startPage == "wakebusy" && Demo.On) Shown += (s, e) => BeginInvoke(new Action(() => { Navigate("dashboard"); ShowWake(BannerKind.Busy, "Wake PS5", "signal sent to " + Settings.FtpHost + " \u00B7 waiting for the FTP server (14 s of " + Settings.Wake.WaitSeconds + " s)\u2026", 8); }));
            if (startPage == "wake") Shown += (s, e) => BeginInvoke(new Action(() => ((SettingsPage)pages["settings"]).ScrollToWake()));
            if (err != null) Log(err);
            Log("RetroSync 2.1 \u2014 settings: " + SettingsStore.SettingsPath);
        }

        void AddPage(Page p) { pages[p.Key] = p; p.Visible = false; host.Controls.Add(p); p.Ready = true; p.PerformLayout(); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(Handle); }

        // ------------------------------------------------------------------ backdrop + sidebar
        public void PaintBackdrop(Graphics g, int offX, int offY)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            using (var b = new SolidBrush(Theme.WindowBg)) g.FillRectangle(b, -offX, -offY, w, h);
            float cx = w * 0.15f - offX, cy = -h * 0.10f - offY, rx = 1200, ry = 700;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterPoint = new PointF(cx, cy);
                    pg.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Theme.WindowBg, Theme.WindowBg, Theme.GlowB, Theme.GlowA },
                        Positions = new[] { 0f, 0.30f, 0.65f, 1f }
                    };
                    g.FillPath(pg, path);
                }
            }
        }

        void LayoutNav()
        {
            int y = 8;
            var bottomKeys = new[] { "settings", "log" };
            foreach (var n in nav.Where(n => !bottomKeys.Contains(n.Key))) { n.Bounds = new Rectangle(8, y, SideW - 16, 38); y += 40; }
            int by = ClientSize.Height - 12 - 76 - 12 - 2 * 40;
            foreach (var k in bottomKeys) { var n = nav.First(x => x.Key == k); n.Bounds = new Rectangle(8, by, SideW - 16, 38); by += 40; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            PaintBackdrop(g, 0, 0);
            LayoutNav();
            foreach (var n in nav)
            {
                var r = n.Bounds;
                bool on = n.Key == current;
                if (on) Theme.FillRound(g, Theme.Alpha(Color.White, 18), r, 6);
                else if (n == hoverNav && n.Enabled) Theme.FillRound(g, Theme.Alpha(Color.White, 10), r, 6);
                if (on) Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y + 11, 3, r.Height - 22), 1.5f);
                Color fg = !n.Enabled ? Theme.Dim : on ? Color.White : Color.FromArgb(215, 219, 223);
                Icons.Draw(g, n.Icon, new RectangleF(r.X + 12, r.Y + (r.Height - 17) / 2f, 17, 17), fg);
                Theme.Text1(g, n.Text, Theme.F135, fg, new Rectangle(r.X + 41, r.Y, r.Width - 90, r.Height));
                if (n.Tag != null)
                {
                    var tw = Theme.Measure(n.Tag, Theme.F11).Width + 14;
                    var tr = new Rectangle(r.Right - tw - 10, r.Y + 10, tw, 18);
                    Theme.FillRound(g, Theme.Alpha(Color.White, 18), tr, 9);
                    TextRenderer.DrawText(g, n.Tag, Theme.F11, tr, Color.FromArgb(140, 149, 157), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                else if (n.Count > 0)
                {
                    string t = n.Count > 999 ? "999+" : n.Count.ToString();
                    var tw = Math.Max(22, Theme.Measure(t, Theme.S11).Width + 14);
                    var tr = new Rectangle(r.Right - tw - 10, r.Y + 10, tw, 18);
                    Theme.FillRound(g, Theme.Accent, tr, 9);
                    TextRenderer.DrawText(g, t, Theme.S11, tr, Theme.OnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
            // separator above the bottom group
            var first = nav.First(x => x.Key == "settings").Bounds;
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, 14, first.Y - 8, SideW - 14, first.Y - 8);
            // footer: PS5 status
            var fr = new Rectangle(8, ClientSize.Height - 12 - 76, SideW - 16, 76);
            Theme.FillRound(g, Theme.Alpha(Color.White, 12), fr, 8);
            Color dc; string head, line3;
            StatusText(out head, out dc, out line3);
            Theme.Dot(g, dc, fr.X + 16, fr.Y + 18);
            Theme.Text1(g, head, Theme.F12, Theme.TextSoft, new Rectangle(fr.X + 26, fr.Y + 8, fr.Width - 30, 20));
            Theme.Text1(g, Settings.FtpHost + ":" + Settings.FtpPort, Theme.Mono11, Theme.Muted, new Rectangle(fr.X + 12, fr.Y + 30, fr.Width - 20, 18));
            Theme.Text1(g, line3, Theme.F115, Theme.Muted, new Rectangle(fr.X + 12, fr.Y + 50, fr.Width - 20, 18));
        }

        public void StatusText(out string head, out Color dot, out string detail)
        {
            switch (Status.Power)
            {
                case Ps5Power.FtpOnline:
                    head = "PS5 online"; dot = Theme.Ok;
                    detail = (Status.LatencyMs >= 0 ? "Latency " + Status.LatencyMs + " ms \u00B7 " : "") + "FTP up"; break;
                case Ps5Power.RestMode: head = "PS5 in rest mode"; dot = Theme.Blue; detail = Settings.Wake.IsConfigured() ? "Can wake from here" : "Wake not configured"; break;
                case Ps5Power.AwakeNoFtp: head = "PS5 awake, FTP down"; dot = Theme.Warn; detail = "Start the FTP payload"; break;
                case Ps5Power.Offline: head = "PS5 offline"; dot = Theme.Bad; detail = "No reply"; break;
                default: head = "PS5: controllo\u2026"; dot = Theme.Muted; detail = ""; break;
            }
        }

        NavItem NavAt(Point p) { LayoutNav(); return nav.FirstOrDefault(n => n.Bounds.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var n = NavAt(e.Location);
            if (n != hoverNav) { hoverNav = n; Cursor = n != null && n.Enabled ? Cursors.Hand : Cursors.Default; Invalidate(new Rectangle(0, 0, SideW, ClientSize.Height)); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverNav = null; Invalidate(new Rectangle(0, 0, SideW, ClientSize.Height)); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var n = NavAt(e.Location);
            if (n != null && n.Enabled) Navigate(n.Key);
            base.OnMouseClick(e);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (host == null) return;
            host.SetBounds(SideW, 6, ClientSize.Width - SideW, ClientSize.Height - 6);
            Invalidate();
        }

        public void SetNavCount(string key, int n)
        {
            var it = nav.FirstOrDefault(x => x.Key == key);
            if (it != null && it.Count != n) { it.Count = n; Invalidate(new Rectangle(0, 0, SideW, ClientSize.Height)); }
        }

        // ------------------------------------------------------------------ pages
        public Page CurrentPage { get { Page p; return current != null && pages.TryGetValue(current, out p) ? p : null; } }

        public void Navigate(string key)
        {
            if (!pages.ContainsKey(key)) key = "dashboard";
            current = key;
            foreach (var p in pages.Values) p.Visible = p.Key == key;
            host.LayoutChildren();
            host.Invalidate();
            Invalidate(new Rectangle(0, 0, SideW, ClientSize.Height));
            pages[key].Entered();
        }

        public void HeaderChanged() { host.Invalidate(new Rectangle(0, 0, host.Width, 86)); }

        class ContentHost : Panel
        {
            readonly MainWindow w;
            public ContentHost(MainWindow w)
            {
                this.w = w;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Content;
            }

            protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); LayoutChildren(); }

            public void LayoutChildren()
            {
                if (w.chipPc == null || w.chipPs5 == null || w.dryToggle == null || w.wakeBanner == null || w.wakeCancel == null) return;
                int right = Width - 28;
                w.dryToggle.Location = new Point(right - w.dryToggle.Width, 31);
                right = w.dryToggle.Left - 14;
                w.chipPs5.Location = new Point(right - w.chipPs5.Width, 30);
                right = w.chipPs5.Left - 10;
                w.chipPc.Location = new Point(right - w.chipPc.Width, 30);
                int bottom = Height - 18;
                if (w.wakeBanner.Visible)
                {
                    w.wakeBanner.SetBounds(28, Height - 18 - 44, Width - 56, 44);
                    w.wakeCancel.Location = new Point(Width - 28 - 12 - w.wakeCancel.Width, w.wakeBanner.Top + 8);
                    w.wakeCancel.BackColor = Theme.Mix(Theme.Content, w.wakeTint, 24 / 255f);
                    bottom = w.wakeBanner.Top - 12;
                }
                var p = w.CurrentPage;
                if (p != null) p.SetBounds(28, 88, Width - 56, Math.Max(100, bottom - 88));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; Theme.Hq(g);
                w.PaintBackdrop(g, Left, Top);
                var r = new RectangleF(0, 0, Width + 20, Height + 20);
                Theme.FillRound(g, Theme.Content, r, 10);
                Theme.DrawRound(g, Theme.Line, r, 10);
                var p = w.CurrentPage;
                if (p == null) return;
                int textRight = w.chipPc.Left - 16;
                Theme.Text1(g, p.Title, Theme.S24, Theme.Text, new Rectangle(28, 16, textRight - 28, 36));
                string sub = p.Subtitle;
                if (!string.IsNullOrEmpty(sub))
                    Theme.Text1(g, sub, sub.Contains(":\\") || sub.StartsWith("/") ? Theme.Mono11 : Theme.F125, Theme.Muted, new Rectangle(29, 54, textRight - 29, 20), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }

        // ------------------------------------------------------------------ state
        public void SetBusy(bool b)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetBusy(b))); return; }
            Busy = b;
            dryToggle.Enabled = !b;
            foreach (var p in pages.Values) p.BusyChanged(b);
        }

        public void SetDryRun(bool v)
        {
            if (DryRun == v) { dryToggle.SetSilently(v); return; }
            DryRun = v;
            dryToggle.SetSilently(v);
            foreach (var p in pages.Values) p.DryRunChanged();
        }

        public void SettingsSaved()
        {
            Lang.Apply(Settings);
            foreach (var p in pages.Values) p.SettingsChanged();
            pollTimer.Interval = Math.Max(10, Settings.Ui.StatusPollSeconds) * 1000;
            if (Settings.Ui.StatusPollSeconds > 0 && !Demo.On) pollTimer.Start(); else pollTimer.Stop();
            UpdateChips(); Invalidate(); HeaderChanged();
            RefreshStatus(true);
        }

        public void Ui(Action a) { if (IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { } } else a(); }

        public void Log(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
            Ui(() =>
            {
                LogLines.Add(line);
                if (LogLines.Count > 20000) LogLines.RemoveRange(0, 2000);
                if (LogAdded != null) LogAdded(line);
            });
        }

        public static string Root(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex.Message; }

        public void UpdateChips()
        {
            bool raOk = Demo.On || Directory.Exists(Settings.PcRetroArchRoot);
            chipPc.Set(raOk ? "PC ready" : "PC: RetroArch not found", raOk ? ChipKind.Ok : ChipKind.Bad);
            switch (Status.Power)
            {
                case Ps5Power.FtpOnline: chipPs5.Set("PS5 online", ChipKind.Ok); break;
                case Ps5Power.RestMode: chipPs5.Set("PS5 in rest mode", ChipKind.Info); break;
                case Ps5Power.AwakeNoFtp: chipPs5.Set("PS5 awake \u00B7 FTP down", ChipKind.Warn); break;
                case Ps5Power.Offline: chipPs5.Set("PS5 offline", ChipKind.Bad); break;
                default: chipPs5.Set("PS5 \u2026", ChipKind.Neutral); break;
            }
            host.LayoutChildren(); host.Invalidate(new Rectangle(0, 0, host.Width, 86));
        }

        // Background check of the PS5 (FTP port, then Remote Play discovery). Skipped while a sync runs.
        public void RefreshStatus(bool force)
        {
            if (Demo.On || polling || (Busy && !force)) return;
            polling = true;
            var s = Settings;
            Task.Factory.StartNew(() => Ps5Probe.Check(s, s.Wake.UseDiscovery)).ContinueWith(t =>
            {
                polling = false;
                if (t.IsFaulted) return;
                var old = Status.Power;
                Status = t.Result;
                if (old != Status.Power && old != Ps5Power.Unknown) Log("Stato PS5: " + PowerText(Status.Power));
                ApplyStatus();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void ApplyStatus()
        {
            UpdateChips();
            Invalidate(new Rectangle(0, 0, SideW, ClientSize.Height));
            foreach (var p in pages.Values) p.StatusChanged();
        }

        public static string PowerText(Ps5Power p)
        {
            switch (p)
            {
                case Ps5Power.FtpOnline: return "online (FTP up)";
                case Ps5Power.RestMode: return "in rest mode";
                case Ps5Power.AwakeNoFtp: return "awake but the FTP server is not answering";
                case Ps5Power.Offline: return "unreachable";
                default: return "unknown";
            }
        }

        // ------------------------------------------------------------------ orchestration
        public void StartRetroArch(SyncScope scope)
        {
            if (Busy) return;
            Navigate("retroarch");
            RetroArch.Analyze(scope);
        }

        public void OpenPs2(bool fresh)
        {
            Navigate("ps2");
            Ps2.Open(fresh);
        }

        public static string ScopeKind(SyncScope s) { return s == SyncScope.All ? "All" : s == SyncScope.Saves ? "Saves" : "RetroArch"; }

        public const string WakeChecklist =
            "On the PS5 you need:\n" +
            "\u2022 Settings \u203A System \u203A Remote Play \u203A Enable Remote Play\n" +
            "\u2022 Settings \u203A System \u203A Power Saving \u203A Features Available in Rest Mode \u203A Stay Connected to the Internet\n" +
            "\u2022 Settings \u203A System \u203A Power Saving \u203A Features Available in Rest Mode \u203A Enable Turning On PS5 from Network\n" +
            "\u2022 the PS5 must be in rest mode (fully off cannot be woken) and the FTP payload must start again on its own.";

        public void Wake(bool thenSync)
        {
            if (Busy) return;
            if (!Settings.Wake.IsConfigured())
            {
                if (DarkDialog.Ask(this, "Remote power-on not configured",
                    "To wake the PS5 from here you need a one-time Remote Play pairing (the \u201CPair PS5\u201D button) or a wake credential.\n\nOpen Settings \u203A Remote power-on?",
                    "Open Settings", "Cancel", Ico.Power) == DialogResult.OK)
                { Navigate("settings"); ((SettingsPage)pages["settings"]).ScrollToWake(); }
                return;
            }
            if (Demo.On) { DarkDialog.Info(this, "Demo", "Demo mode does not send anything."); return; }
            string after = thenSync ? (Settings.Wake.AfterWake ?? "all") : "none";
            wakeCancelRequested = false;
            wakeRunning = true;
            SetBusy(true);
            ShowWake(BannerKind.Busy, "Wake PS5", "sending the signal to " + Settings.FtpHost + "\u2026", 0);
            Log("Remote wake of the PS5 (" + Settings.FtpHost + ")" + (after != "none" ? ", then sync" : "") + "\u2026");
            var s = Settings;
            var start = DateTime.Now;
            string lastPhase = null;
            Task.Factory.StartNew(() => Ps5Probe.WakeAndWait(s, st =>
            {
                int el = (int)(DateTime.Now - start).TotalSeconds;
                string phase = st.Split('\u2026')[0];
                if (phase != lastPhase) { lastPhase = phase; Log("  " + st); }
                Ui(() => ShowWake(BannerKind.Busy, "Wake PS5", st, Math.Min(99, el * 100 / Math.Max(1, s.Wake.WaitSeconds))));
            }, () => wakeCancelRequested)).ContinueWith(t =>
            {
                wakeRunning = false;
                SetBusy(false);
                if (t.IsFaulted)
                {
                    Log("ERROR wake: " + Root(t.Exception));
                    ShowWake(BannerKind.Bad, "Wake failed", Root(t.Exception), -1);
                    return;
                }
                if (!t.Result)
                {
                    if (wakeCancelRequested) { HideWake(); Log("Wake cancelled."); return; }
                    Log("The PS5 did not reply within " + s.Wake.WaitSeconds + " s.");
                    ShowWake(BannerKind.Warn, "PS5 is not responding", "no FTP reply within " + s.Wake.WaitSeconds + " s \u2014 see Log", -1);
                    Log(WakeChecklist.Replace("\n", " "));
                    ActivityLog.Add(new ActivityEntry { Kind = "Wake", Title = "Wake PS5", Detail = "no reply within " + s.Wake.WaitSeconds + " s", Status = "error" });
                    RefreshStatus(true);
                    return;
                }
                Log("PS5 ready: FTP is up.");
                ActivityLog.Add(new ActivityEntry { Kind = "Wake", Title = "Wake PS5", Detail = "PS5 awake, FTP up after " + (int)(DateTime.Now - start).TotalSeconds + " s", Status = "ok" });
                Status = new Ps5State { Power = Ps5Power.FtpOnline }; ApplyStatus();
                ShowWake(BannerKind.Ok, "PS5 awake", "FTP up on " + s.FtpHost + ":" + s.FtpPort, -1);
                var hide = new Timer { Interval = 6000 }; hide.Tick += (o, e) => { hide.Stop(); hide.Dispose(); if (!Busy) HideWake(); }; hide.Start();
                RefreshStatus(true);
                if (after == "saves") StartRetroArch(SyncScope.Saves);
                else if (after == "config") StartRetroArch(SyncScope.Config);
                else if (after == "all") StartRetroArch(SyncScope.All);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void Rest()
        {
            if (Busy) return;
            if (Demo.On) { DarkDialog.Info(this, "Demo", "Demo mode does not send anything."); return; }
            if (DarkDialog.Ask(this, Lang.T("Put the PS5 into rest mode?", "Mettere il PS5 a riposo?"),
                "RetroSync sends the PS5 (" + Settings.FtpHost + ", ELF loader on port " + PairHelper.LoaderPort + ") a small payload that puts it into rest mode.\n\n" +
                "Save open games first: the current game is suspended or closed as with PS button \u203A Power \u203A Rest Mode. " +
                "jailbreak payloads (including FTP) may not come back after wake.",
                Lang.T("Rest mode", "Metti a riposo"), Lang.T("Cancel", "Annulla"), Ico.Power, Theme.Warn) != DialogResult.OK) return;
            SetBusy(true);
            ShowWake(BannerKind.Busy, "Rest mode", "sending the command to " + Settings.FtpHost + "\u2026", 0);
            Log("Rest mode for the PS5 (" + Settings.FtpHost + ")\u2026");
            var s = Settings;
            Task.Factory.StartNew(() => RestMode.Enter(s, x => { Log("  " + x); Ui(() => ShowWake(BannerKind.Busy, "Rest mode", x, 50)); })).ContinueWith(t =>
            {
                SetBusy(false);
                if (t.IsFaulted)
                {
                    Log("ERROR rest: " + Root(t.Exception));
                    ShowWake(BannerKind.Bad, "Rest mode failed", Root(t.Exception), -1);
                    ActivityLog.Add(new ActivityEntry { Kind = "Wake", Title = "Rest mode", Detail = Root(t.Exception), Status = "error" });
                    return;
                }
                if (t.Result)
                {
                    ShowWake(BannerKind.Ok, "PS5 in rest mode", "command completed on " + s.FtpHost, -1);
                    ActivityLog.Add(new ActivityEntry { Kind = "Wake", Title = "Rest mode", Detail = "PS5 put into rest mode", Status = "ok" });
                    var hide = new Timer { Interval = 6000 }; hide.Tick += (o, e) => { hide.Stop(); hide.Dispose(); if (!Busy) HideWake(); }; hide.Start();
                }
                else
                {
                    Log("Rest mode not confirmed within " + RestMode.ConfirmSeconds + " s.");
                    ShowWake(BannerKind.Warn, "Rest mode not confirmed", "the command was sent but the PS5 still answers \u2014 check the notifications on the PS5", -1);
                }
                RefreshStatus(true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowWake(BannerKind k, string title, string body, int progress)
        {
            wakeBanner.Set(k, title, body, progress);
            wakeBanner.RightReserve = 104;
            wakeTint = k == BannerKind.Warn ? Theme.Warn : k == BannerKind.Ok ? Theme.Ok : k == BannerKind.Bad ? Theme.Bad : Theme.Accent;
            wakeBanner.Visible = true;
            wakeCancel.Text = k == BannerKind.Busy ? "Cancel" : "Close";
            wakeCancel.Width = 84;
            wakeCancel.Visible = true; wakeCancel.Enabled = true;
            wakeCancel.BringToFront();
            host.LayoutChildren();
        }

        void HideWake() { wakeBanner.Visible = false; wakeCancel.Visible = false; host.LayoutChildren(); host.Invalidate(); }
    }
}
