using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

// Backup: v1 PS5 mirror (unchanged engine) + per-emulator folder backups + scheduling helper + history.
namespace RetroSync
{
    class BackupPage : Page
    {
        readonly CardPanel mirror, sched, emu, history;
        readonly DarkButton runMirror, openMirror, copyCmd, makeTask, openBackup;
        readonly Toggle schedWake;
        readonly Segmented schedFreq;
        readonly DarkTextField schedTime;
        readonly DarkProgress mirrorBar = new DarkProgress();
        readonly Dictionary<string, DarkButton> emuButtons = new Dictionary<string, DarkButton>();
        readonly Dictionary<string, string> emuInfo = new Dictionary<string, string>();
        bool mirrorRunning;
        string mirrorStatus = "";
        string runningEmu;

        public BackupPage(MainWindow w) : base(w, "backup", "Backup")
        {
            mirror = new CardPanel { PaintExtra = PaintMirror };
            sched = new CardPanel(Lang.T("Scheduling", "Pianificazione")) { PaintExtra = PaintSched };
            emu = new CardPanel(Lang.T("Per-emulator backup", "Backup per emulatore")) { Hint = Lang.T("Dated copies of individual folders, faster than a full mirror", "Copie datate di singole cartelle, pi\u00F9 veloci del mirror completo"), PaintExtra = PaintEmu };
            history = new CardPanel(Lang.T("History", "Cronologia")) { PaintExtra = PaintHistory };
            foreach (var c in new[] { mirror, sched, emu, history }) Controls.Add(c);

            runMirror = new DarkButton(Lang.T("Start PS5 backup", "Avvia backup PS5"), BtnKind.Primary, Ico.Play) { BackColor = Theme.Card };
            runMirror.Click += (s, e) => StartMirror();
            openMirror = new DarkButton(Lang.T("Open folder", "Apri cartella"), BtnKind.Normal, Ico.Folder) { BackColor = Theme.Card };
            openMirror.Click += (s, e) => Open(S.MirrorLocalRoot);
            mirror.Controls.Add(runMirror); mirror.Controls.Add(openMirror);
            mirrorBar.BackColor = Theme.Card2; mirrorBar.Visible = false;
            mirror.Controls.Add(mirrorBar);

            schedFreq = new Segmented();
            schedFreq.Add("Every day", Ico.Clock); schedFreq.Add("Every Sunday", Ico.Clock);
            schedFreq.SelectedIndex = 1;
            schedFreq.ItemClicked += i => { schedFreq.SelectedIndex = i; sched.Invalidate(); };
            schedTime = new DarkTextField("03:00") { Width = 72, BackColor = Theme.Card };
            schedTime.Box.TextChanged += (s, e) => sched.Invalidate();
            schedWake = new Toggle("Wake the PS5 first (--wake)", false) { BackColor = Theme.Card };
            schedWake.CheckedChanged += (s, e) => sched.Invalidate();
            copyCmd = new DarkButton(Lang.T("Copy command", "Copia comando"), BtnKind.Normal, null, true) { BackColor = Theme.Card };
            copyCmd.Click += (s, e) => { try { Clipboard.SetText(Command()); W.Log("Command copied: " + Command()); } catch { } };
            makeTask = new DarkButton(Lang.T("Create Windows task\u2026", "Crea attivit\u00E0 in Windows\u2026"), BtnKind.Normal, Ico.Clock, true) { BackColor = Theme.Card };
            makeTask.Click += (s, e) => CreateTask();
            foreach (Control c in new Control[] { schedFreq, schedTime, schedWake, copyCmd, makeTask }) sched.Controls.Add(c);

            foreach (var m in FolderBackup.Modules)
            {
                var mod = m;
                var b = new DarkButton(m.Available ? Lang.T("Run now", "Esegui ora") : Lang.T("Unavailable", "Non disponibile"), BtnKind.Normal, null, true) { Width = 120, BackColor = Theme.Card, Enabled = m.Available };
                b.Click += (s, e) => RunEmu(mod);
                emuButtons[m.Id] = b; emu.Controls.Add(b);
            }
            openBackup = new DarkButton(Lang.T("Open backup folder", "Apri cartella backup"), BtnKind.Normal, Ico.Folder, true) { BackColor = Theme.Card };
            openBackup.Click += (s, e) => Open(S.BackupRoot);
            history.Controls.Add(openBackup);
            ActivityLog.Changed += () => W.Ui(() => { mirror.Invalidate(); history.Invalidate(); });
        }

        public override string Subtitle { get { return Lang.T("Backups of the PS5 and emulators on the PC \u2014 nothing is ever written to the PS5", "Copie di sicurezza della PS5 e degli emulatori sul PC \u2014 sul PS5 non viene mai scritto nulla"); } }

        public override void Entered() { RefreshEmuInfo(); Invalidate(true); }
        public override void SettingsChanged() { RefreshEmuInfo(); Invalidate(true); }
        public override void BusyChanged(bool busy)
        {
            runMirror.Enabled = !busy;
            foreach (var m in FolderBackup.Modules) emuButtons[m.Id].Enabled = !busy && m.Available;
        }

        void RefreshEmuInfo()
        {
            foreach (var m in FolderBackup.Modules)
            {
                if (!m.Available) { emuInfo[m.Id] = "soon \u00B7 v2.1"; continue; }
                if (Demo.On) { emuInfo[m.Id] = m.Id == "retroarch" ? "Last: today, 05:02 \u00B7 7 versions" : "Last: yesterday, 21:40 \u00B7 4 versions"; continue; }
                try
                {
                    var v = FolderBackup.Versions(S, m);
                    emuInfo[m.Id] = v.Count == 0 ? "Never run" : "Last: " + ActivityLog.Ago(v[0].CreationTime) + " \u00B7 " + v.Count + (v.Count == 1 ? " version" : " versions");
                }
                catch { emuInfo[m.Id] = ""; }
            }
        }

        protected override void DoLayout()
        {
            int w = Width, gap = 14;
            int lw = (int)(w * 0.6);
            mirror.SetBounds(0, 0, lw, 248);
            sched.SetBounds(lw + gap, 0, w - lw - gap, 248);
            runMirror.Location = new Point(16, mirror.Height - 16 - runMirror.Height);
            openMirror.Location = new Point(runMirror.Right + 10, runMirror.Top);
            mirrorBar.SetBounds(28, 176, mirror.Width - 56, 6);
            schedFreq.Location = new Point(16, 46);
            schedTime.SetBounds(52, 88, 72, 32);
            schedWake.Location = new Point(140, 92);
            copyCmd.Location = new Point(16, sched.Height - 16 - copyCmd.Height);
            makeTask.Location = new Point(copyCmd.Right + 8, copyCmd.Top);
            int n = FolderBackup.Modules.Length;
            emu.SetBounds(0, 262, w, 52 + n * 50);
            int y = 46;
            foreach (var m in FolderBackup.Modules) { emuButtons[m.Id].Location = new Point(w - 16 - 120, y + 10); y += 50; }
            int hy = emu.Bottom + gap;
            history.SetBounds(0, hy, w, Math.Max(90, Height - hy));
            openBackup.Location = new Point(history.Width - 16 - openBackup.Width, 10);
        }

        static void Open(string dir)
        {
            try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        // ------------------------------------------------------------------ PS5 mirror (v1: MainForm.StartMirror)
        public void StartMirror()
        {
            if (W.Busy) return;
            if (Demo.On) { DarkDialog.Info(FindForm(), "Demo", "Demo mode does not copy anything."); return; }
            var s = S;
            if (DarkDialog.Ask(FindForm(), "PS5 backup", "Copy the PS5 RetroArch folder to the PC (new or changed files only):\n\n" +
                "ftp://" + s.FtpHost + ":" + s.FtpPort + s.MirrorRemoteRoot + "\n\u2192 " + s.MirrorLocalRoot +
                "\n\nExcluded: " + string.Join(", ", s.MirrorExcludes) + "\nNothing is written to the PS5. Continue?", "Continue", "Cancel", Ico.Database) != DialogResult.OK) return;
            W.SetBusy(true);
            mirrorRunning = true; mirrorStatus = "Listing files on the PS5"; mirrorBar.Value = 0; mirrorBar.Visible = true; mirror.Invalidate();
            W.Log("PS5 backup: " + s.MirrorRemoteRoot + " \u2192 " + s.MirrorLocalRoot);
            Task.Factory.StartNew(() => new Ps5Mirror(s, W.Log, x => W.Ui(() =>
            {
                mirrorStatus = x;
                var m = Regex.Match(x, @"(\d+)%");
                if (m.Success) mirrorBar.Value = int.Parse(m.Groups[1].Value);
                mirror.Invalidate();
            })).Run()).ContinueWith(t =>
            {
                W.SetBusy(false);
                mirrorRunning = false; mirrorBar.Visible = false;
                var e = new ActivityEntry { Kind = "Backup", Title = "Full PS5 mirror" };
                if (t.IsFaulted)
                {
                    W.Log("ERROR: " + MainWindow.Root(t.Exception));
                    e.Status = "error"; e.Detail = MainWindow.Root(t.Exception);
                    DarkDialog.Info(FindForm(), "PS5 backup failed", MainWindow.Root(t.Exception), Ico.Warning, Theme.Bad);
                }
                else
                {
                    var r = t.Result;
                    string sum = "Copied " + r.Copied + " files (" + Util.Size(r.CopiedBytes) + "), unchanged " + r.Unchanged + (r.DeletedOnPs5 > 0 ? ", " + r.DeletedOnPs5 + " no longer on the PS5" : "") + (r.Failed > 0 ? ", errors " + r.Failed : "");
                    W.Log((r.Failed > 0 ? "PS5 backup completato with errors: " : "PS5 backup completato: ") + sum);
                    e.Status = r.Failed > 0 ? "error" : "ok"; e.Files = r.Copied; e.Bytes = r.CopiedBytes;
                    e.Detail = Util.Size(r.RemoteBytes) + " \u00B7 " + r.RemoteFiles + " files, " + r.Copied + " copied";
                }
                ActivityLog.Add(e);
                mirror.Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ------------------------------------------------------------------ per-emulator folder backup
        void RunEmu(IBackupModule m)
        {
            if (W.Busy || !m.Available) return;
            if (Demo.On) { DarkDialog.Info(FindForm(), "Demo", "Demo mode does not copy anything."); return; }
            if (DarkDialog.Ask(FindForm(), "Backup " + m.Title, "Copy " + m.Note + " into a new dated folder:\n" + FolderBackup.ModuleRoot(S, m) +
                "\n\nNothing is written to the PS5. Keeping the last " + S.Ui.EmulatorBackupKeep + " versions. Continue?", "Continue", "Cancel", m.Icon) != DialogResult.OK) return;
            W.SetBusy(true);
            runningEmu = m.Id; emu.Invalidate();
            W.Log("Backup " + m.Title + "\u2026");
            var s = S;
            Task.Factory.StartNew(() => FolderBackup.Run(s, m, W.Log, null)).ContinueWith(t =>
            {
                W.SetBusy(false);
                runningEmu = null;
                var e = new ActivityEntry { Kind = "Backup", Title = "Backup " + m.Title };
                if (t.IsFaulted) { W.Log("ERROR: " + MainWindow.Root(t.Exception)); e.Status = "error"; e.Detail = MainWindow.Root(t.Exception); }
                else
                {
                    var r = t.Result;
                    foreach (var n in r.Notes) W.Log(n);
                    W.Log("Backup " + m.Title + ": " + r.Files + " files (" + Util.Size(r.Bytes) + ")" + (r.Failed > 0 ? ", errors " + r.Failed : "") + " \u2192 " + r.Dir);
                    e.Status = r.Failed > 0 ? "error" : "ok"; e.Files = r.Files; e.Bytes = r.Bytes; e.Detail = Util.Size(r.Bytes) + " \u00B7 " + r.Files + " file" + (r.Notes.Count > 0 ? " (PC only)" : "");
                }
                ActivityLog.Add(e);
                RefreshEmuInfo(); emu.Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ------------------------------------------------------------------ scheduling (Windows Task Scheduler)
        string Command()
        {
            return "\"" + (Demo.On ? @"C:\Users\User\Documents\RetroSync\RetroSync.exe" : Application.ExecutablePath) + "\" --backup-ps5" + (schedWake.Checked ? " --wake" : "");
        }

        bool TimeOk(out string t)
        {
            t = schedTime.Text.Trim();
            return Regex.IsMatch(t, @"^([01]\d|2[0-3]):[0-5]\d$");
        }

        void CreateTask()
        {
            string t;
            if (!TimeOk(out t)) { DarkDialog.Info(FindForm(), "Scheduling", "Scrivi l'ora nel formato HH:MM (es. 03:00).", Ico.Warning, Theme.Warn); return; }
            string args = "/Create /F /TN \"RetroSync backup PS5\" /SC " + (schedFreq.SelectedIndex == 0 ? "DAILY" : "WEEKLY /D SUN") + " /ST " + t +
                " /TR \"" + Command().Replace("\"", "\\\"") + "\"";
            if (DarkDialog.Ask(FindForm(), "Create scheduled task",
                "I will create (or replace) the \u201CRetroSync backup PS5\u201D task in Windows Task Scheduler:\n\n" +
                (schedFreq.SelectedIndex == 0 ? "every day" : "every Sunday") + " at " + t + "\n" + Command() +
                "\n\nThe PC must be on at that time. You can remove it from Task Scheduler anytime.", "Create", "Cancel", Ico.Clock) != DialogResult.OK) return;
            if (Demo.On) return;
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    W.Log("schtasks: " + o.Trim());
                    if (p.ExitCode == 0) DarkDialog.Info(FindForm(), "Scheduling", "Task created.", Ico.Check, Theme.Ok);
                    else DarkDialog.Info(FindForm(), "Scheduling failed", o.Trim(), Ico.Warning, Theme.Bad);
                }
            }
            catch (Exception ex) { DarkDialog.Info(FindForm(), "Scheduling failed", ex.Message, Ico.Warning, Theme.Bad); }
        }

        // ------------------------------------------------------------------ painting
        void PaintMirror(Graphics g)
        {
            var last = ActivityLog.Last(a => a.Kind == "Backup" && a.Title.StartsWith("Mirror"));
            string pill = mirrorRunning ? "Running" : last == null ? "Never run" : last.Status == "ok" ? "Last OK" : "Errors";
            var pk = mirrorRunning ? Theme.BadgeKind.Info : last == null ? Theme.BadgeKind.Neutral : last.Status == "ok" ? Theme.BadgeKind.Ok : Theme.BadgeKind.Bad;
            CardPaint.Header(g, mirror, Ico.Database, Theme.Accent, "Full PS5 mirror", "Full copy of " + S.MirrorRemoteRoot, pill, pk);
            int y = 60, cw = mirror.Width - 32;
            CardPaint.Kv(g, 16, y, 110, cw, "Destination", S.MirrorLocalRoot, true); y += 20;
            CardPaint.Kv(g, 16, y, 110, cw, "Last backup", last == null ? "never" : ActivityLog.Ago(last.Time) + (last.Detail.Length > 0 ? " \u00B7 " + last.Detail : "")); y += 20;
            CardPaint.Kv(g, 16, y, 110, cw, "Excluded", string.Join(", ", S.MirrorExcludes)); y += 20;
            var box = new Rectangle(16, 150, mirror.Width - 32, 40);
            Theme.FillRound(g, Theme.Card2, box, 6);
            if (mirrorRunning)
            {
                Theme.Text1(g, "Backup in corso \u00B7 incrementale", Theme.S12, Theme.Text, new Rectangle(28, 153, 220, 18));
                Theme.Text1(g, mirrorStatus, Theme.F115, Theme.Muted, new Rectangle(250, 153, box.Width - 246, 18), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                Icons.Draw(g, Ico.Info, new RectangleF(28, 162, 15, 15), Theme.Muted);
                TextRenderer.DrawText(g, "Copies only new or changed files; when a file changes on the PS5 the previous version is kept in _previous. FTP is used read-only.",
                    Theme.F12, new Rectangle(52, 155, box.Width - 48, 32), Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        void PaintSched(Graphics g)
        {
            string t; bool ok = TimeOk(out t);
            Theme.Text1(g, "Time", Theme.F125, Theme.Muted, new Rectangle(16, 88, 34, 32));
            var r = new Rectangle(16, 130, sched.Width - 32, 44);
            Theme.FillRound(g, Theme.Field, r, 5);
            Theme.DrawRound(g, Theme.Line, r, 5);
            string exe = Demo.On ? @"C:\Users\User\Documents\RetroSync\RetroSync.exe" : Application.ExecutablePath;
            string args = " --backup-ps5" + (schedWake.Checked ? " --wake" : "");
            string shown = "\"" + CardPaint.FitPath(exe, Theme.Mono11, r.Width - 24 - Theme.Measure("\"\"" + args, Theme.Mono11).Width) + "\"" + args;
            Theme.Text1(g, shown, Theme.Mono11, Theme.TextSoft, new Rectangle(r.X + 10, r.Y, r.Width - 16, r.Height));
            Theme.Text1(g, ok ? "Windows Task Scheduler \u00B7 " + (schedFreq.SelectedIndex == 0 ? "every day" : "every Sunday") + " at " + t : "Invalid time (HH:MM)",
                Theme.F12, ok ? Theme.Muted : Theme.Bad, new Rectangle(16, 178, sched.Width - 32, 18));
        }

        void PaintEmu(Graphics g)
        {
            int y = 46, w = emu.Width;
            using (var p = new Pen(Theme.Line))
                foreach (var m in FolderBackup.Modules)
                {
                    g.DrawLine(p, 16, y, w - 16, y);
                    Color tint = m.Id == "retroarch" ? Theme.Blue : m.Id == "ps2" ? Theme.Violet : Theme.Muted;
                    Icons.Tile(g, m.Icon, new Rectangle(16, y + 9, 32, 32), m.Available ? tint : Theme.Dim);
                    Theme.Text1(g, m.Title, Theme.S13, m.Available ? Theme.Text : Theme.Dim, new Rectangle(60, y + 7, 360, 20));
                    Theme.Text1(g, m.Note, Theme.F12, m.Available ? Theme.Muted : Theme.Dim, new Rectangle(60, y + 26, 420, 18));
                    string info; emuInfo.TryGetValue(m.Id, out info);
                    if (runningEmu == m.Id) info = "Backup in corso\u2026";
                    if (!m.Available)
                    {
                        int bw = Theme.Badge(g, info ?? "soon", Theme.BadgeKind.Neutral, 0, 0, true);
                        Theme.Badge(g, info ?? "soon", Theme.BadgeKind.Neutral, w - 16 - 120 - 16 - bw, y + 25);
                    }
                    else Theme.Text1(g, info ?? "", Theme.F125, Theme.TextSoft, new Rectangle(w - 16 - 120 - 16 - 300, y, 300, 50), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    y += 50;
                }
        }

        void PaintHistory(Graphics g)
        {
            var items = ActivityLog.Recent(Math.Max(1, (history.Height - 54) / 26), a => a.Kind == "Backup");
            int y = 48, w = history.Width;
            if (items.Count == 0) { Theme.Text1(g, "No backups recorded yet.", Theme.F125, Theme.Muted, new Rectangle(16, y + 2, w - 32, 22)); return; }
            using (var p = new Pen(Theme.Line))
                foreach (var a in items)
                {
                    g.DrawLine(p, 16, y, w - 16, y);
                    Theme.Text1(g, a.Time.ToString("dd/MM/yyyy HH:mm"), Theme.F125, Theme.Muted, new Rectangle(16, y, 130, 26));
                    Theme.Text1(g, a.Title, Theme.F125, Theme.Text, new Rectangle(150, y, 260, 26));
                    int bw = Theme.Badge(g, a.Status == "ok" ? "OK" : CardPaint.StatusText(a.Status), CardPaint.StatusKind(a.Status), 0, 0, true);
                    Theme.Text1(g, a.Detail, Theme.F125, Theme.TextSoft, new Rectangle(420, y, w - 420 - 24 - bw, 26));
                    Theme.Badge(g, a.Status == "ok" ? "OK" : CardPaint.StatusText(a.Status), CardPaint.StatusKind(a.Status), w - 16 - bw, y + 13);
                    y += 26;
                }
        }
    }
}
