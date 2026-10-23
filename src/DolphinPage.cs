using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

// Dolphin (PC) <-> Porpoise (PS5) page. New in 2.2. Logic in DolphinSync.cs.
namespace RetroSync
{
    class DolphinPage : Page
    {
        readonly Banner banner;
        readonly DarkButton analyze, apply, openPc;
        readonly ListView lv;
        DolphinEngine eng;
        DolphinPlan plan;
        bool planDry;

        public DolphinPage(MainWindow w) : base(w, "dolphin", "GameCube / Wii \u00B7 Dolphin")
        {
            analyze = new DarkButton(Lang.T("Analyze", "Analizza"), BtnKind.Normal, Ico.Search) { Height = 34 };
            analyze.Click += (s, e) => Analyze();
            apply = new DarkButton(Lang.T("Apply", "Applica"), BtnKind.Primary, Ico.Check) { Height = 34 };
            apply.Click += (s, e) => Apply();
            openPc = new DarkButton(Lang.T("Open Dolphin folder", "Apri cartella Dolphin"), BtnKind.Normal, Ico.Folder, true);
            openPc.Click += (s, e) => { try { if (Directory.Exists(S.Dolphin.PcUserDir)) Process.Start("explorer.exe", "\"" + S.Dolphin.PcUserDir + "\""); } catch { } };
            banner = new Banner();
            banner.Set(BannerKind.Neutral, "Dolphin \u21C4 Porpoise", "Press Analyze to compare GameCube GCI saves" + (S.Dolphin.SyncWii ? " and Wii title data" : "") + ". Nothing is written until you press Apply.");
            lv = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.F125, HideSelection = false };
            lv.Columns.Add("Save", 430); lv.Columns.Add("Action", 110); lv.Columns.Add("Reason", 300); lv.Columns.Add("PC", 140); lv.Columns.Add("PS5", 140);
            lv.ItemChecked += (s, e) => { var it = e.Item.Tag as DolphinItem; if (it != null) { it.Selected = e.Item.Checked && it.Direction != Dir.None; } UpdateButtons(); };
            lv.HandleCreated += (s, e) => Theme.DarkScrollbars(lv);
            Controls.Add(analyze); Controls.Add(apply); Controls.Add(openPc); Controls.Add(banner); Controls.Add(lv);
            UpdateButtons();
        }

        public override string Subtitle
        {
            get
            {
                return (W.DryRun ? "PREVIEW (dry-run): analysis only \u00B7 " : "") + "Dolphin (PC) \u21C4 Porpoise (PS5) \u00B7 GCI matched by game code \u00B7 savestates not synced";
            }
        }

        protected override void DoLayout()
        {
            analyze.Location = new Point(0, 0);
            apply.Location = new Point(analyze.Right + 8, 0);
            openPc.Location = new Point(Width - openPc.Width, 4);
            banner.SetBounds(0, 44, Width, 58);
            lv.SetBounds(0, 112, Width, Height - 112);
            int w = lv.ClientSize.Width;
            if (w > 600) { lv.Columns[0].Width = w - 110 - 300 - 140 - 140 - 4; }
        }

        void UpdateButtons()
        {
            analyze.Enabled = !W.Busy;
            apply.Enabled = !W.Busy && plan != null && !planDry && (plan.ToPs5 + plan.ToPc) > 0;
        }

        public override void BusyChanged(bool busy) { UpdateButtons(); }
        public override void DryRunChanged() { plan = null; lv.Items.Clear(); UpdateButtons(); W.HeaderChanged(); }
        public override void SettingsChanged() { plan = null; if (lv != null) lv.Items.Clear(); }

        public void Analyze()
        {
            if (W.Busy) return;
            if (Demo.On) { banner.Set(BannerKind.Info, "Demo", "Demo mode does not read anything."); return; }
            if (eng != null) { eng.Dispose(); eng = null; }
            plan = null; lv.Items.Clear();
            bool dry = W.DryRun; planDry = dry;
            W.SetBusy(true);
            banner.Set(BannerKind.Busy, "Analysing\u2026", "reading Dolphin on the PC and Porpoise on the PS5", 0);
            var e = new DolphinEngine(S, dry, x => W.Log("[Dolphin] " + x));
            eng = e;
            Task.Factory.StartNew(() => e.BuildPlan(x => W.Ui(() => banner.Progress = x))).ContinueWith(t =>
            {
                W.SetBusy(false);
                if (t.IsFaulted)
                {
                    string m = MainWindow.Root(t.Exception);
                    W.Log("[Dolphin] ERROR: " + m);
                    banner.Set(BannerKind.Bad, "Analysis failed", m);
                    UpdateButtons(); return;
                }
                plan = t.Result;
                foreach (var n in plan.Notes) W.Log("[Dolphin] " + n);
                Fill();
                int act = plan.ToPs5 + plan.ToPc;
                banner.Set(act > 0 ? BannerKind.Info : BannerKind.Ok,
                    act > 0 ? (dry ? "Preview: " : "") + act + " save(s) to sync" : "Everything aligned",
                    plan.ToPs5 + " to the PS5, " + plan.ToPc + " to the PC, " + plan.Items.Count(i => i.Identical) + " identical" + (dry ? " \u00B7 dry-run: nothing will be written" : ""));
                W.SetNavCount("dolphin", act);
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Fill()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var it in plan.Items.OrderBy(i => i.Identical).ThenBy(i => i.Display))
            {
                if (it.Identical && S.Ui.HideIdentical) continue;
                var li = new ListViewItem(it.Display) { Tag = it };
                li.SubItems.Add(it.Direction == Dir.ToPs5 ? "PC \u2192 PS5" : it.Direction == Dir.ToPc ? "PS5 \u2192 PC" : it.Identical ? "identical" : "\u2014");
                li.SubItems.Add(it.Reason);
                li.SubItems.Add(it.PcExists ? (it.PcMtime.HasValue ? it.PcMtime.Value.ToLocalTime().ToString("dd/MM/yy HH:mm") : "") + " \u00B7 " + Util.Size(it.PcSize) : "\u2014");
                li.SubItems.Add(it.Ps5Exists ? (it.Ps5Mtime.HasValue ? it.Ps5Mtime.Value.ToLocalTime().ToString("dd/MM/yy HH:mm") : "") + " \u00B7 " + Util.Size(it.Ps5Size) : "\u2014");
                li.ForeColor = it.Conflict ? Theme.Warn : it.Identical ? Theme.Muted : Theme.Text;
                li.BackColor = Theme.Card;
                li.Checked = it.Selected && it.Direction != Dir.None;
                lv.Items.Add(li);
            }
            lv.EndUpdate();
        }

        void Apply()
        {
            if (plan == null || planDry || W.Busy || eng == null) return;
            int a = plan.ToPs5, b = plan.ToPc;
            if (DarkDialog.Ask(FindForm(), "Confirm Dolphin sync",
                "About to write " + a + " save(s) to the PS5 and " + b + " to the PC.\n\nEvery overwritten file is first copied to:\n" + Path.Combine(S.BackupRoot, "Dolphin") +
                "\n\nClose Dolphin on the PC and Porpoise on the PS5 first (they keep the GCI folder open).\n\nContinue?", "Continue", "Cancel", Ico.Sync) != DialogResult.OK) return;
            var p = plan; var e = eng;
            W.SetBusy(true);
            banner.Set(BannerKind.Busy, "Syncing\u2026", a + " to the PS5, " + b + " to the PC", 0);
            Task.Factory.StartNew(() => e.Apply(p, x => W.Ui(() => banner.Progress = x))).ContinueWith(t =>
            {
                W.SetBusy(false);
                e.Dispose(); eng = null; plan = null;
                lv.Items.Clear();
                W.SetNavCount("dolphin", 0);
                var entry = new ActivityEntry { Kind = "Dolphin", Title = "Dolphin saves", ToPs5 = a, ToPc = b, Files = a + b };
                if (t.IsFaulted)
                {
                    string m = MainWindow.Root(t.Exception);
                    W.Log("[Dolphin] ERROR: " + m);
                    banner.Set(BannerKind.Bad, "Completed with errors", m);
                    entry.Status = "error"; entry.Detail = m;
                }
                else
                {
                    banner.Set(BannerKind.Ok, "Done \u2714", t.Result + " save(s) synced. Run Analyze again to re-check.");
                    entry.Status = "ok"; entry.Detail = t.Result + " file";
                }
                ActivityLog.Add(entry);
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
