using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

// RetroArch page: Sync saves / Sync RetroArch / Sync all.
// Same flow as v1 (BuildPlan -> preview -> confirm -> Apply -> PS2 card), shown inline instead of a dialog.
namespace RetroSync
{
    class RetroArchPage : Page
    {
        readonly Segmented seg;
        readonly SearchField search;
        readonly DarkButton hideBtn, moreBtn;
        readonly Banner banner;
        readonly CardPanel gridCard;
        readonly DarkGrid grid;
        readonly DetailPane detail;
        readonly BottomBar bar;
        readonly Toggle dry2;
        readonly DarkButton cancel, apply;
        readonly ContextMenuStrip more;

        SyncEngine eng;
        SyncPlan plan;
        bool planDry;
        SyncScope scope = SyncScope.All;
        readonly Dictionary<SyncItem, Dir> proposed = new Dictionary<SyncItem, Dir>();
        public List<SyncItem> LastIssues = new List<SyncItem>();

        public RetroArchPage(MainWindow w) : base(w, "retroarch", "RetroArch")
        {
            seg = new Segmented();
            seg.Add("Sync saves", Ico.Save); seg.Add("Sync RetroArch", Ico.Gamepad); seg.Add("Sync all", Ico.Sync);
            seg.ItemClicked += i => Analyze(i == 0 ? SyncScope.Saves : i == 1 ? SyncScope.Config : SyncScope.All);
            Controls.Add(seg);
            search = new SearchField { Width = 220, Height = 34 };
            search.BackColor = Theme.Content;
            search.Box.TextChanged += (s, e) => Fill(true);
            Controls.Add(search);
            hideBtn = new DarkButton(Lang.T("Hide identical", "Nascondi identici"), BtnKind.Normal, Ico.Filter) { Height = 34 };
            hideBtn.Selected = S.Ui.HideIdentical;
            hideBtn.Click += (s, e) => { S.Ui.HideIdentical = !S.Ui.HideIdentical; hideBtn.Selected = S.Ui.HideIdentical; Fill(true); };
            Controls.Add(hideBtn);
            moreBtn = new DarkButton("\u22EF", BtnKind.Normal) { Width = 40, Height = 34 };
            more = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.F13 };
            more.Items.Add("Mark current state as aligned\u2026", null, (s, e) => StartBaseline());
            more.Items.Add("Deseleziona i file presenti su un solo lato", null, (s, e) => DeselectOneSided());
            more.Items.Add("Always ignore selected file\u2026", null, (s, e) => IgnoreSelected());
            more.Items.Add(new ToolStripSeparator());
            more.Items.Add("Copy plan to clipboard", null, (s, e) => { if (plan != null) { try { Clipboard.SetText(PlanPrinter.Format(plan, true)); } catch { } } });
            moreBtn.Click += (s, e) => more.Show(moreBtn, new Point(moreBtn.Width - more.Width, moreBtn.Height + 2));
            Controls.Add(moreBtn);

            banner = new Banner();
            Controls.Add(banner);

            gridCard = new CardPanel();
            grid = new DarkGrid { CheckColumn = true, RowHeight = 42, EmptyText = "No analysis yet.\nChoose Sync saves, Sync RetroArch or Sync all to see a preview before any change." };
            grid.Columns.Add(new GridColumn("File \u00B7 Category", 200, true));
            grid.Columns.Add(new GridColumn("Action", 120));
            grid.Columns.Add(new GridColumn("Reason", 168));
            grid.Columns.Add(new GridColumn("PC date", 92));
            grid.Columns.Add(new GridColumn("PS5 date", 92));
            grid.CheckState = o => { var it = (SyncItem)o; return it.Direction == Dir.None ? -1 : it.Selected ? 1 : 0; };
            grid.CheckClicked += o => { var it = (SyncItem)o; it.Selected = !it.Selected; Changed(); };
            grid.HeaderCheckClicked += (s, e) => ToggleAll();
            grid.LookOf = o => { var it = (SyncItem)o; return it.Conflict ? RowLook.Conflict : it.Info || it.Direction == Dir.None ? RowLook.Muted : RowLook.Normal; };
            grid.PaintCell = PaintCell;
            grid.SelectionChanged += (s, e) => detail.ShowItem(grid.SelectedRow as SyncItem);
            grid.RowActivated += o => Cycle((SyncItem)o);
            var ctx = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.F13 };
            ctx.Items.Add("Cambia direzione", null, (s, e) => { var it = grid.SelectedRow as SyncItem; if (it != null) Cycle(it); });
            ctx.Items.Add("Always ignore this file (add to exclusions)", null, (s, e) => IgnoreSelected());
            grid.ContextMenuStrip = ctx;
            gridCard.Controls.Add(grid);
            Controls.Add(gridCard);

            detail = new DetailPane(this);
            Controls.Add(detail);

            bar = new BottomBar(this);
            Controls.Add(bar);
            dry2 = new Toggle(Lang.T("Dry-run (preview only)", "Dry-run (solo simulazione)"), false);
            dry2.CheckedChanged += (s, e) => W.SetDryRun(dry2.Checked);
            cancel = new DarkButton(Lang.T("Cancel", "Annulla")) { Width = 96 };
            cancel.Click += (s, e) => CancelPlan();
            apply = new DarkButton(Lang.T("Apply", "Applica"), BtnKind.Primary, Ico.Check);
            apply.Click += (s, e) => Apply();
            Controls.Add(dry2); Controls.Add(cancel); Controls.Add(apply);
            bar.SendToBack();
            SetIdle();
            UpdateButtons();
        }

        public override string Subtitle { get { return S.PcRetroArchRoot + "  \u21C4  " + S.Ps5RetroArchRoot; } }

        protected override void DoLayout()
        {
            int w = Width, h = Height;
            seg.Location = new Point(0, 0);
            moreBtn.Location = new Point(w - moreBtn.Width, 1);
            hideBtn.Location = new Point(moreBtn.Left - 8 - hideBtn.Width, 1);
            search.SetBounds(hideBtn.Left - 10 - 220, 1, 220, 34);
            search.Visible = search.Left > seg.Right + 10;
            banner.SetBounds(0, 48, w, 42);
            int top = 102, bottom = h - 46;
            int dw = 264;
            gridCard.SetBounds(0, top, w - dw - 14, bottom - top);
            grid.SetBounds(1, 1, gridCard.Width - 2, gridCard.Height - 2);
            detail.SetBounds(w - dw, top, dw, bottom - top);
            bar.SetBounds(0, h - 36, w, 36);
            apply.Width = Math.Max(150, apply.PreferredWidth);
            apply.Location = new Point(w - apply.Width, h - 34);
            cancel.Location = new Point(apply.Left - 10 - cancel.Width, h - 34);
            dry2.Location = new Point(cancel.Left - 22 - dry2.Width, h - 30);
            bar.ReservedRight = w - dry2.Left + 10;
        }

        public override void DryRunChanged()
        {
            dry2.SetSilently(W.DryRun);
            if (plan != null && !W.Busy && planDry != W.DryRun)
            {
                W.Log(W.DryRun ? "Dry-run on: re-analysing in preview only." : "Dry-run off: re-analysing so changes can be applied.");
                Analyze(scope);
            }
            UpdateButtons();
        }

        public override void BusyChanged(bool busy) { seg.Enabled = !busy; dry2.Enabled = !busy; moreBtn.Enabled = !busy; UpdateButtons(); }
        public override void SettingsChanged() { W.HeaderChanged(); }

        // ------------------------------------------------------------------ analysis (v1: MainForm.StartSync)
        public void Analyze(SyncScope sc)
        {
            if (W.Busy) return;
            if (Demo.On) { Demo.FillRetroArch(this, sc); return; }
            DisposeEngine();
            scope = sc;
            seg.SelectedIndex = sc == SyncScope.Saves ? 0 : sc == SyncScope.Config ? 1 : 2;
            bool dry = W.DryRun;
            W.SetBusy(true);
            string what = sc == SyncScope.All ? "everything" : sc == SyncScope.Saves ? "saves" : "RetroArch config";
            banner.Set(BannerKind.Busy, "Analisi in corso\u2026", "confronto " + what + " tra PC e PS5" + (dry ? " (preview)" : ""), 0);
            grid.EmptyText = "Analisi in corso\u2026";
            grid.SetRows(new object[0], false);
            SyncEngine e = null;
            Task.Factory.StartNew(() =>
            {
                e = new SyncEngine(S, dry, W.Log);
                return e.BuildPlan(sc, p => W.Ui(() => banner.Progress = p));
            }).ContinueWith(t =>
            {
                W.SetBusy(false);
                if (t.IsFaulted)
                {
                    if (e != null) e.Dispose();
                    W.Log("ERROR: " + MainWindow.Root(t.Exception));
                    banner.Set(BannerKind.Bad, "Something went wrong", MainWindow.Root(t.Exception));
                    grid.EmptyText = "Analysis failed: " + MainWindow.Root(t.Exception);
                    grid.Invalidate();
                    return;
                }
                var p2 = t.Result;
                foreach (var n in p2.Notes) W.Log(n);
                ShowPlan(e, p2, dry);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // v1: MainForm.ReviewPlan (first half)
        public void ShowPlan(SyncEngine e, SyncPlan p, bool dry)
        {
            int pending = p.Items.Count(i => i.Direction != Dir.None || i.Conflict);
            W.Log("Analysis: " + p.Items.Count + " items, " + p.Items.Count(i => i.Identical) + " already aligned, " + pending + " to sync.");
            scope = p.Scope;
            seg.SelectedIndex = scope == SyncScope.Saves ? 0 : scope == SyncScope.Config ? 1 : 2;
            LastIssues = p.Visible.Where(i => i.Conflict || i.Direction != Dir.None).OrderByDescending(i => i.Conflict).ThenBy(i => i.Direction == Dir.ToPs5).Take(6).ToList();
            if (!p.Visible.Any())
            {
                if (e != null) e.Dispose();
                eng = null; plan = null;
                banner.Set(BannerKind.Ok, "RetroArch already aligned", "no differences between PC and PS5.");
                grid.EmptyText = "No differences between PC and PS5.";
                grid.SetRows(new object[0], false); detail.ShowItem(null);
                ActivityLog.Add(new ActivityEntry { Kind = MainWindow.ScopeKind(p.Scope), Title = dry ? "Preview" : "Analysis", Detail = "already aligned", Status = "ok" });
                W.SetNavCount("retroarch", 0);
                ChainPs2(p);
                UpdateButtons();
                return;
            }
            eng = e; plan = p; planDry = dry;
            proposed.Clear();
            foreach (var it in p.Items) proposed[it] = it.Direction;
            if (dry && !Demo.On) ActivityLog.Add(new ActivityEntry { Kind = MainWindow.ScopeKind(p.Scope), Title = "Preview", Detail = "Sync " + ScopeName(p.Scope) + " \u00B7 dry-run, " + p.Visible.Count() + " differenze", Status = "dry" });
            Fill(false);
            grid.Focus();
            Changed();
        }

        static string ScopeName(SyncScope s) { return s == SyncScope.All ? "everything" : s == SyncScope.Saves ? "Saves" : "RetroArch"; }

        void Fill(bool keepSelection)
        {
            if (plan == null) return;
            string f = search.Text.Trim();
            var rows = (S.Ui.HideIdentical ? plan.Visible : plan.Items)
                .Where(i => f.Length == 0 || (i.Display ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || (i.Category ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(i => i.Conflict).ThenBy(i => i.Info).ThenBy(i => i.Direction == Dir.None).ThenBy(i => i.Category).ThenBy(i => i.Display)
                .Cast<object>().ToList();
            grid.EmptyText = f.Length > 0 ? "No file matches \u201C" + f + "\u201D." : "Nothing to show.";
            grid.SetRows(rows, true);
            detail.ShowItem(grid.SelectedRow as SyncItem);
        }

        void SetIdle()
        {
            banner.Set(BannerKind.Neutral, "Choose what to sync", "Sync saves, Sync RetroArch or Sync all: you always get a preview before any change.");
        }

        // counts exactly like v1 (merge counts on both sides)
        public int ToPs5 { get { return plan == null ? 0 : plan.Items.Count(i => i.Actionable && (i.Direction == Dir.ToPs5 || i.Direction == Dir.Merge)); } }
        public int ToPc { get { return plan == null ? 0 : plan.Items.Count(i => i.Actionable && (i.Direction == Dir.ToPc || i.Direction == Dir.Merge)); } }
        public int Conflicts { get { return plan == null ? 0 : plan.Items.Count(i => i.Conflict); } }
        public int Selected { get { return plan == null ? 0 : plan.Items.Count(i => i.Actionable); } }
        public bool HasPlan { get { return plan != null; } }
        public bool PlanDry { get { return planDry; } }
        public long TotalBytes
        {
            get
            {
                if (plan == null) return 0;
                return plan.Items.Where(i => i.Actionable).Sum(i => i.Direction == Dir.ToPs5 ? i.PcSize : i.Direction == Dir.ToPc ? i.Ps5Size : Math.Max(i.PcSize, i.Ps5Size));
            }
        }

        void Changed()
        {
            if (plan == null) return;
            int n = plan.Visible.Count();
            int sel = Selected, cf = Conflicts;
            if (planDry)
                banner.Set(BannerKind.Warn, "Preview (dry-run on)", n + " items to review \u00B7 " + sel + " selected" + (cf > 0 ? " \u00B7 " + cf + (cf == 1 ? " conflict" : " conflicts") + " to check" : "") + ". No files will be written.");
            else if (cf > 0)
                banner.Set(BannerKind.Warn, cf + (cf == 1 ? " conflict" : " conflicts"), "changed on both sides or no history: newer side preselected, check highlighted rows. " + sel + " selected.");
            else
                banner.Set(BannerKind.Info, "Preview ready", n + " items to review \u00B7 " + sel + " selected. Nothing is written until you press Apply; every overwritten file is backed up first.");
            W.SetNavCount("retroarch", sel);
            grid.HeaderCheck = HeaderState();
            grid.Invalidate(); bar.Invalidate(); detail.Refresh2();
            UpdateButtons();
        }

        int HeaderState()
        {
            var c = plan.Items.Where(i => i.Direction != Dir.None && !i.Identical).ToList();
            if (c.Count == 0) return 0;
            int s = c.Count(i => i.Selected);
            return s == 0 ? 0 : s == c.Count ? 1 : 2;
        }

        void ToggleAll()
        {
            if (plan == null || W.Busy) return;
            var c = plan.Items.Where(i => i.Direction != Dir.None && !i.Identical).ToList();
            bool all = c.All(i => i.Selected);
            foreach (var i in c) i.Selected = !all;
            Changed();
        }

        void UpdateButtons()
        {
            bool has = plan != null;
            cancel.Enabled = has && !W.Busy;
            cancel.Text = has && planDry ? "Close" : "Cancel";
            int n = Selected;
            if (has && planDry) { apply.Text = "Preview only"; apply.Icon = Ico.Eye; apply.Enabled = false; }
            else { apply.Text = n > 0 ? Lang.T("Apply", "Applica") + " " + n + (n == 1 ? Lang.T(" change", " modifica") : Lang.T(" changes", " modifiche")) : Lang.T("Apply", "Applica"); apply.Icon = Ico.Check; apply.Enabled = has && !W.Busy && ToPs5 + ToPc > 0; }
            hideBtn.Enabled = !W.Busy;
            if (Ready && Width > 0) DoLayout();
        }

        public void Cycle(SyncItem it)
        {
            if (it == null || W.Busy || it.Options.Count < 2) return;
            int i = it.Options.IndexOf(it.Direction);
            SetDirection(it, it.Options[(i + 1) % it.Options.Count]);
        }

        public void SetDirection(SyncItem it, Dir d)
        {
            it.Direction = d;
            it.Selected = it.Direction != Dir.None;
            if (eng != null) { try { eng.RefreshKvPreview(it); } catch { } }
            Changed();
        }

        public Dir ProposedOf(SyncItem it) { Dir d; return proposed.TryGetValue(it, out d) ? d : it.Direction; }

        void DeselectOneSided()
        {
            if (plan == null) return;
            foreach (var it in plan.Items.Where(i => i.PcExists != i.Ps5Exists)) it.Selected = false;
            Changed();
        }

        public void IgnoreSelected()
        {
            var it = grid.SelectedRow as SyncItem;
            if (it == null || eng == null) return;
            if (DarkDialog.Ask(FindForm(), "Always ignore", "Stop syncing \"" + it.Display + "\"?\nIt will be added to the exclusions in RetroSync.settings.json.", "Always ignore", "Cancel", Ico.Filter) != DialogResult.OK) return;
            try
            {
                string r = eng.IgnoreForever(it);
                if (r == null) { DarkDialog.Info(FindForm(), "Always ignore", "There is no automatic exclusion for this item: edit RetroSync.settings.json."); return; }
                it.Selected = false; it.Direction = Dir.None; it.Options = new List<Dir> { Dir.None }; it.Info = true; it.Reason = "ignorato (" + r + ")";
                W.Log("Exclusion added: " + r);
                Changed();
            }
            catch (Exception ex) { DarkDialog.Info(FindForm(), "Could not save", ex.Message, Ico.Warning, Theme.Bad); }
        }

        void DisposeEngine()
        {
            if (eng != null) { try { eng.Dispose(); } catch { } }
            eng = null; plan = null; proposed.Clear();
            W.SetNavCount("retroarch", 0);
        }

        void ChainPs2(SyncPlan p)
        {
            bool doPs2 = (p.Scope & SyncScope.Saves) != 0 && S.Ps2.IncludeInSyncSave;
            if (!doPs2) return;
            W.Log("Opening PS2 memory card sync" + (W.DryRun ? " (preview)" : "") + "\u2026");
            W.OpenPs2(true);
        }

        // v1: "Cancel"/"Close" in the preview dialog
        void CancelPlan()
        {
            if (plan == null || W.Busy) return;
            var p = plan; bool dry = planDry;
            DisposeEngine();
            banner.Set(dry ? BannerKind.Info : BannerKind.Neutral, dry ? "Preview finished" : "Cancelled", dry ? "No changes made." : "Nothing was changed.");
            grid.EmptyText = dry ? "Preview closed." : "Cancelled: no changes.";
            grid.SetRows(new object[0], false); detail.ShowItem(null);
            UpdateButtons();
            ChainPs2(p);
        }

        // v1: MainForm.ReviewPlan (second half)
        void Apply()
        {
            if (plan == null || planDry || W.Busy) return;
            if (Demo.On) { DarkDialog.Info(FindForm(), "Demo", "Demo mode does not write anything."); return; }
            int toPs5 = ToPs5, toPc = ToPc;
            if (SyncEngine.PcRetroArchRunning())
            {
                DarkDialog.Info(FindForm(), "RetroArch is running", "RetroArch is open on the PC: close it before syncing, or it will rewrite the config on exit.", Ico.Warning, Theme.Warn);
                DisposeEngine();
                SetIdle(); grid.SetRows(new object[0], false); detail.ShowItem(null); UpdateButtons();
                return;
            }
            if (DarkDialog.Ask(FindForm(), "Confirm sync",
                "About to write " + toPs5 + " items to the PS5 and " + toPc + " to the PC.\n\n" +
                "Every overwritten file is first copied to:\n" + S.BackupRoot + "\n\n" +
                "Make sure RetroArch is also CLOSED on the PS5 (config_save_on_exit would rewrite retroarch.cfg).\n\nContinue?",
                "Continue", "Cancel", Ico.Sync) != DialogResult.OK)
            {
                DisposeEngine();
                banner.Set(BannerKind.Neutral, "Cancelled", "Nothing was changed.");
                grid.SetRows(new object[0], false); detail.ShowItem(null); UpdateButtons();
                return;
            }
            var p = plan; var e = eng;
            var moved = p.Items.Where(i => i.Actionable).ToList();
            long bytes = TotalBytes;
            W.SetBusy(true);
            banner.Set(BannerKind.Busy, "Syncing\u2026", toPs5 + " to the PS5, " + toPc + " to the PC", 0);
            Task.Factory.StartNew(() => e.Apply(p, x => W.Ui(() => banner.Progress = x))).ContinueWith(t =>
            {
                e.Dispose();
                eng = null; plan = null; proposed.Clear();
                W.SetBusy(false);
                W.SetNavCount("retroarch", 0);
                var entry = new ActivityEntry { Kind = MainWindow.ScopeKind(p.Scope), Title = "Sync " + ScopeName(p.Scope), ToPs5 = toPs5, ToPc = toPc, Files = moved.Count, Bytes = bytes };
                string names = moved.Count == 0 ? "" : moved[0].Display.Split('/').Last() + (moved.Count > 1 ? " and " + (moved.Count - 1) + " more" : "");
                if (t.IsFaulted)
                {
                    W.Log("ERROR: " + MainWindow.Root(t.Exception));
                    banner.Set(BannerKind.Bad, "Completato with errors", MainWindow.Root(t.Exception));
                    entry.Status = "error"; entry.Detail = MainWindow.Root(t.Exception);
                }
                else
                {
                    banner.Set(BannerKind.Ok, "Done \u2714", "Sync completed, backup in " + S.BackupRoot);
                    entry.Status = "ok"; entry.Detail = moved.Count + " file \u00B7 " + names;
                    LastIssues.Clear();
                }
                ActivityLog.Add(entry);
                grid.EmptyText = t.IsFaulted ? "Sync finished with errors: see Log." : "Sync completed. Run a new analysis to re-check.";
                grid.SetRows(new object[0], false); detail.ShowItem(null);
                UpdateButtons();
                ChainPs2(p);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // v1: MainForm.StartBaseline
        void StartBaseline()
        {
            if (W.Busy) return;
            if (Demo.On) { DarkDialog.Info(FindForm(), "Demo", "Demo mode does not write anything."); return; }
            if (DarkDialog.Ask(FindForm(), "Mark current state as aligned", "I will record the current PC and PS5 state as aligned.\n\nFrom now on only changes after this moment are proposed; " +
                "current differences (e.g. a different xmb_theme) stay as they are.\nFiles present on only one side are not recorded and stay proposed.\n\nNo RetroArch file is modified. Continue?",
                "Continue", "Cancel", Ico.Check) != DialogResult.OK) return;
            DisposeEngine();
            grid.SetRows(new object[0], false); detail.ShowItem(null);
            W.SetBusy(true);
            banner.Set(BannerKind.Busy, "Registro lo stato attuale\u2026", "leggo PC e PS5", 0);
            Task.Factory.StartNew(() =>
            {
                using (var e = new SyncEngine(S, true, W.Log))
                {
                    var p = e.BuildPlan(SyncScope.All, x => W.Ui(() => banner.Progress = x));
                    return e.Baseline(p);
                }
            }).ContinueWith(t =>
            {
                W.SetBusy(false);
                UpdateButtons();
                if (t.IsFaulted) { W.Log("ERROR: " + MainWindow.Root(t.Exception)); banner.Set(BannerKind.Bad, "Baseline failed", MainWindow.Root(t.Exception)); return; }
                W.Log("Baseline registrata: " + t.Result + " coppie PC/PS5.");
                banner.Set(BannerKind.Ok, "Current state recorded as aligned", t.Result + " PC/PS5 pairs recorded in RetroSync.state.json");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ------------------------------------------------------------------ painting
        // compact local date for the table ("18/10 23:41"; other years "18/10/25 23:41"); full dates are in the details box
        public static string ShortDate(DateTime? utc)
        {
            if (!utc.HasValue || utc.Value.Year <= 1980) return "\u2014";
            var l = utc.Value.ToLocalTime();
            return l.ToString(l.Year == DateTime.Now.Year ? "dd/MM HH:mm" : "dd/MM/yy HH:mm");
        }

        public static Theme.BadgeKind KindOf(SyncItem it)
        {
            if (it.Conflict) return Theme.BadgeKind.Conflict;
            switch (it.Direction)
            {
                case Dir.ToPs5: return Theme.BadgeKind.Up;
                case Dir.ToPc: return Theme.BadgeKind.Down;
                case Dir.Merge: return Theme.BadgeKind.Merge;
                default: return Theme.BadgeKind.Neutral;
            }
        }

        public static string ActionText(SyncItem it)
        {
            switch (it.Direction)
            {
                case Dir.ToPs5: return (it.Conflict ? "\u26A0 " : "") + "PC \u2192 PS5";
                case Dir.ToPc: return (it.Conflict ? "\u26A0 " : "") + "PS5 \u2192 PC";
                case Dir.Merge: return (it.Conflict ? "\u26A0 " : "") + "\u21C4 Merge";
                default: return it.Conflict ? "Conflict" : "None";
            }
        }

        void PaintCell(Graphics g, Rectangle r, object o, int col)
        {
            var it = (SyncItem)o;
            bool muted = it.Info || it.Direction == Dir.None;
            switch (col)
            {
                case 0:
                    string full = it.Display ?? "";
                    int sl = full.LastIndexOf('/');
                    string name = sl >= 0 ? full.Substring(sl + 1) : full, folder = sl >= 0 ? full.Substring(0, sl) : "";
                    Theme.Text1(g, name, Theme.F13, muted ? Theme.Muted : Theme.Text, new Rectangle(r.X, r.Y + 4, r.Width, r.Height / 2), TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    Theme.Text1(g, it.Category + (folder.Length > 0 ? " \u00B7 " + folder : ""), Theme.F115, muted ? Theme.Dim : Theme.Muted, new Rectangle(r.X, r.Y + r.Height / 2 + 1, r.Width, r.Height / 2 - 5), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    break;
                case 1:
                    Theme.Badge(g, ActionText(it), KindOf(it), r.X, r.Y + r.Height / 2);
                    break;
                case 2:
                    Theme.Text1(g, it.Reason, Theme.F125, it.Conflict ? Color.FromArgb(255, 207, 115) : muted ? Theme.Dim : Theme.TextSoft, r);
                    break;
                case 3:
                    Theme.Text1(g, it.PcExists ? ShortDate(it.PcMtime) : "\u2014", Theme.F12, muted ? Theme.Dim : Theme.Muted, r);
                    break;
                case 4:
                    Theme.Text1(g, it.Ps5Exists ? ShortDate(it.Ps5Mtime) : "\u2014", Theme.F12, muted ? Theme.Dim : Theme.Muted, r);
                    break;
            }
        }

        // ------------------------------------------------------------------ bottom bar
        class BottomBar : Control
        {
            readonly RetroArchPage p;
            public int ReservedRight;
            public BottomBar(RetroArchPage p)
            {
                this.p = p;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Content;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
                if (!p.HasPlan) { Theme.Text1(g, "No plan loaded", Theme.F125, Theme.Dim, new Rectangle(0, 0, Width - ReservedRight, Height)); return; }
                int x = 0, cy = Height / 2;
                x += Theme.Badge(g, "PC \u2192 PS5 \u00B7 " + p.ToPs5, Theme.BadgeKind.Up, x, cy) + 8;
                x += Theme.Badge(g, "PS5 \u2192 PC \u00B7 " + p.ToPc, Theme.BadgeKind.Down, x, cy) + 8;
                x += Theme.Badge(g, "Conflicts \u00B7 " + p.Conflicts, Theme.BadgeKind.Conflict, x, cy) + 12;
                Theme.Text1(g, "Total " + Util.Size(p.TotalBytes), Theme.F125, Theme.Muted, new Rectangle(x, 0, Math.Max(10, Width - ReservedRight - x), Height));
            }
        }

        // ------------------------------------------------------------------ detail pane
        class DetailPane : CardPanel
        {
            readonly RetroArchPage p;
            SyncItem it;
            readonly TextBox details;
            readonly List<DarkButton> opts = new List<DarkButton>();
            readonly DarkButton ignore;
            int textTop;

            public DetailPane(RetroArchPage p)
            {
                this.p = p;
                details = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, WordWrap = true,
                    BackColor = Theme.Field, ForeColor = Theme.TextSoft, Font = Theme.Mono11, Visible = false
                };
                details.HandleCreated += (s, e) => Theme.DarkScrollbars(details);
                Controls.Add(details);
                ignore = new DarkButton("Always ignore this file", BtnKind.Ghost, Ico.Filter, true) { BackColor = Theme.Card, Visible = false };
                ignore.Click += (s, e) => p.IgnoreSelected();
                Controls.Add(ignore);
                PaintExtra = PaintDetail;
            }

            public void ShowItem(SyncItem x)
            {
                it = x;
                foreach (var b in opts) { Controls.Remove(b); b.Dispose(); }
                opts.Clear();
                details.Visible = ignore.Visible = it != null;
                if (it != null)
                {
                    foreach (var d in it.Options)
                    {
                        var dir = d;
                        string t = d == Dir.ToPs5 ? "Keep PC \u2192 PS5" : d == Dir.ToPc ? "Keep PS5 \u2192 PC" : d == Dir.Merge ? "Merge (key by key)" : "Skip this file";
                        if (it.Options.Count > 1 && d == p.ProposedOf(it) && d != Dir.None) t += Lang.T(" (recommended)", " (consigliato)");
                        var b = new DarkButton(t, BtnKind.Normal, d == Dir.ToPs5 ? Ico.Monitor : d == Dir.ToPc ? Ico.Console : d == Dir.Merge ? Ico.Swap : Ico.Close, true) { BackColor = Theme.Card, LeftAlign = true, Height = 30 };
                        b.Click += (s, e) => { if (!p.W.Busy) p.SetDirection(it, dir); };
                        opts.Add(b); Controls.Add(b);
                    }
                    var lines = new List<string>();
                    lines.Add("PC : " + (it.PcFull ?? "(unknown core folder)") + (it.PcExists ? "   " + Util.FmtUtc(it.PcMtime) : "   (missing)"));
                    lines.Add("PS5: " + (it.Ps5Path ?? "(unknown core folder)") + (it.Ps5Exists ? "   " + Util.FmtUtc(it.Ps5Mtime) : "   (missing)"));
                    lines.AddRange(it.Details);
                    details.Text = string.Join("\r\n", lines);
                    ignore.Visible = it.ExcludeList != null;
                }
                Refresh2();
                OnLayout(new LayoutEventArgs(this, ""));
            }

            public void Refresh2()
            {
                if (it != null) for (int i = 0; i < opts.Count && i < it.Options.Count; i++) opts[i].Selected = it.Options[i] == it.Direction;
                Invalidate();
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (details == null || ignore == null) return;
                int w = Width - 32;
                int y = Height - 14 - 30;   // note line
                if (ignore.Visible) { ignore.SetBounds(16, y - 30, w, 28); y -= 36; }
                for (int i = opts.Count - 1; i >= 0; i--) { opts[i].SetBounds(16, y - 30, w, 30); y -= 36; }
                textTop = ComputeTextTop();
                details.SetBounds(17, textTop + 1, w - 2, Math.Max(20, y - 10 - textTop - 2));
            }

            int ComputeTextTop()
            {
                if (it == null) return 60;
                var nameH = TextRenderer.MeasureText(ShortName(it), Theme.S135, new Size(Width - 32, 200), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                nameH = Math.Min(nameH, 40);
                return 34 + nameH + 4 + 18 + 30 + 4 * 20 + 10;
            }

            void PaintDetail(Graphics g)
            {
                Theme.Text1(g, Lang.T("DETAIL", "DETTAGLIO"), Theme.F11, Theme.Muted, new Rectangle(16, 12, Width - 32, 18));
                if (it == null)
                {
                    TextRenderer.DrawText(g, "Seleziona una riga per vedere i dettagli e scegliere la direzione.", Theme.F125, new Rectangle(16, 36, Width - 32, 60), Theme.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    return;
                }
                int w = Width - 32, y = 34;
                var nameH = Math.Min(40, TextRenderer.MeasureText(g, ShortName(it), Theme.S135, new Size(w, 200), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
                TextRenderer.DrawText(g, ShortName(it), Theme.S135, new Rectangle(16, y, w, nameH), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                y += nameH + 4;
                Theme.Text1(g, it.Category + (it.Kind == ItemKind.KeyValue ? " \u00B7 unione chiave per chiave" : it.Kind == ItemKind.Playlist ? " \u00B7 playlist (percorsi convertiti)" : ""), Theme.F12, Theme.Muted, new Rectangle(16, y, w, 18));
                y += 22;
                string state = it.Conflict ? "Conflict \u00B7 " + it.Reason : it.Reason;
                var bw = Theme.Badge(g, state, KindOf(it), 16, y + 10, true);
                if (bw > w)
                {
                    Theme.FillRound(g, Theme.Alpha(Theme.Warn, it.Conflict ? 44 : 0), new Rectangle(16, y, w, 20), 4);
                    Color bg, fg; Theme.BadgeColors(KindOf(it), out bg, out fg);
                    Theme.FillRound(g, bg, new Rectangle(16, y, w, 20), 4);
                    Theme.Text1(g, state, Theme.S11, fg, new Rectangle(24, y, w - 16, 20));
                }
                else Theme.Badge(g, state, KindOf(it), 16, y + 10);
                y += 30;
                int c1 = 16 + 84, c2 = c1 + (w - 84) / 2;
                Theme.Text1(g, "PC", Theme.F12, Theme.Muted, new Rectangle(c1, y, c2 - c1, 18));
                Theme.Text1(g, "PS5", Theme.F12, Theme.Muted, new Rectangle(c2, y, Width - 16 - c2, 18));
                y += 20;
                bool pcNewer = it.PcExists && it.Ps5Exists && it.PcMtime > it.Ps5Mtime, psNewer = it.PcExists && it.Ps5Exists && it.Ps5Mtime > it.PcMtime;
                Row(g, Lang.T("Modified", "Modificato"), it.PcExists ? ShortDate(it.PcMtime) : "missing", it.Ps5Exists ? ShortDate(it.Ps5Mtime) : "missing", c1, c2, y, pcNewer, psNewer); y += 20;
                Row(g, Lang.T("Size", "Dimensione"), it.PcExists ? Util.Size(it.PcSize) : "\u2014", it.Ps5Exists ? Util.Size(it.Ps5Size) : "\u2014", c1, c2, y, false, false); y += 20;
                Row(g, "SHA-256", ShortSha(it.PcSha), ShortSha(it.Ps5Sha), c1, c2, y, false, false); y += 20;
                var fr = new RectangleF(16, textTop, w, details.Height + 2);
                Theme.FillRound(g, Theme.Field, fr, 5);
                Theme.DrawRound(g, Theme.Line, fr, 5);
                Theme.Text1(g, "A backup copy is saved before overwriting.", Theme.F115, Theme.Dim, new Rectangle(16, Height - 40, w, 30), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            static string ShortName(SyncItem it) { var d = it.Display ?? ""; int i = d.LastIndexOf('/'); return i >= 0 ? d.Substring(i + 1) : d; }

            static string ShortSha(string s) { return string.IsNullOrEmpty(s) ? "\u2014" : s.Length > 12 ? s.Substring(0, 6).ToLowerInvariant() + "\u2026" + s.Substring(s.Length - 4).ToLowerInvariant() : s; }

            void Row(Graphics g, string k, string a, string b, int c1, int c2, int y, bool hiA, bool hiB)
            {
                Theme.Text1(g, k, Theme.F12, Theme.Muted, new Rectangle(16, y, c1 - 16, 18));
                Theme.Text1(g, a, Theme.F12, hiA ? Color.FromArgb(111, 227, 214) : Theme.TextSoft, new Rectangle(c1, y, c2 - c1 - 4, 18));
                Theme.Text1(g, b, Theme.F12, hiB ? Color.FromArgb(156, 202, 255) : Theme.TextSoft, new Rectangle(c2, y, Width - 16 - c2, 18));
            }
        }
    }
}
