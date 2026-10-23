using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("RetroSync")]
[assembly: System.Reflection.AssemblyProduct("RetroSync")]
[assembly: System.Reflection.AssemblyVersion("2.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.2.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("2.2.0")]

namespace RetroSync
{
    // "Start with Windows" (New in 2.2): HKCU Run entry pointing at this exe with --minimized.
    static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "RetroSync";

        public static string Command { get { return "\"" + Application.ExecutablePath + "\" --minimized"; } }

        public static bool IsEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) { var v = k == null ? null : k.GetValue(Name) as string; return !string.IsNullOrEmpty(v); } }
            catch { return false; }
        }

        public static string CurrentTarget()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? null : k.GetValue(Name) as string; }
            catch { return null; }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (on) k.SetValue(Name, Command);
                else if (k.GetValue(Name) != null) k.DeleteValue(Name, false);
            }
        }
    }

    static class Program
    {
        public const string Version = "2.2.0";

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            var a = args.Select(x => x.ToLowerInvariant()).ToList();
            if (a.Contains("--dry-run") || a.Contains("/dry-run") || a.Contains("--dryrun") || a.Contains("--baseline") || a.Contains("--backup-ps5") || a.Contains("--wake") || a.Contains("--help") || a.Contains("/?"))
            {
                AttachConsole(-1);
                return Cli.Run(args);
            }
            // --minimized (used by "Start with Windows"): start hidden with only the tray icon.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // --demo: sample data for screenshots, nothing is read from or written to the PS5/RetroArch
            Demo.On = a.Contains("--demo");
            MainWindow.StartHidden = (a.Contains("--minimized") || a.Contains("--tray")) && !Demo.On;
            int li = a.IndexOf("--lang");
            if (li >= 0 && li + 1 < a.Count) Lang.Forced = a[li + 1].Trim().ToLowerInvariant() == "it" ? "it" : "en";
            int pi = a.IndexOf("--page");
            string page = pi >= 0 && pi + 1 < a.Count ? a[pi + 1] : null;
            Application.Run(new MainWindow(page));
            return 0;
        }
    }

    // Headless preview: builds the plan against the real paths and prints it. Never writes to the PS5 or to RetroArch.
    static class Cli
    {
        static StreamWriter file;

        static readonly System.Collections.Generic.List<string> early = new System.Collections.Generic.List<string>();

        static void Out(string s)
        {
            try { Console.WriteLine(s); } catch { }
            if (file != null) { file.WriteLine(s); file.Flush(); }
            else early.Add(s);
        }

        static void OpenLog(string path)
        {
            try { file = new StreamWriter(path, false, new UTF8Encoding(true)); } catch { }
            if (file != null) { foreach (var l in early) file.WriteLine(l); file.Flush(); }
            early.Clear();
        }

        // New in 2.0: "--wake" powers the PS5 on from rest mode and waits for its FTP server before the
        // requested CLI action (e.g. a scheduled "--backup-ps5 --wake"). Returns false if it did not come up.
        static bool WakeFirst()
        {
            string err;
            var s = SettingsStore.Load(out err);
            if (err != null) Out(err);
            Out("RetroSync \u2014 ACCENSIONE PS5 " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
            int ms;
            if (Ps5Probe.FtpAnswers(s.FtpHost, s.FtpPort, 2000, out ms)) { Out("PS5 already awake: FTP up (" + ms + " ms)."); return true; }
            if (!s.Wake.IsConfigured()) { Out("ERROR: remote power-on not configured (Settings \u203A Remote power-on)."); return false; }
            try
            {
                string last = null;
                bool ok = Ps5Probe.WakeAndWait(s, x => { string ph = x.Split('\u2026')[0]; if (ph != last) { last = ph; Out("  " + x); } }, () => false);
                Out(ok ? "PS5 ready: FTP is up." : "ERROR: the PS5 did not respond within " + s.Wake.WaitSeconds + " s.");
                return ok;
            }
            catch (Exception e) { Out("ERROR wake: " + e.Message); return false; }
        }

        static int BackupPs5(string[] args, System.Collections.Generic.List<string> a)
        {
            string logPath = Path.Combine(SettingsStore.AppDir, "RetroSync-backup-ps5.log");
            int li = a.IndexOf("--log");
            if (li >= 0 && li + 1 < args.Length) logPath = args[li + 1];
            OpenLog(logPath);
            try
            {
                string err;
                var s = SettingsStore.Load(out err);
                if (err != null) Out(err);
                Out("RetroSync \u2014 BACKUP PS5 " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
                var m = new Ps5Mirror(s, x => Out(x), x => { try { Console.Write("\r  " + x.PadRight(78)); } catch { } });
                if (a.Contains("--move-deleted")) m.MoveDeleted = true;
                var r = m.Run();
                try { Console.WriteLine(); } catch { }
                Out("Log: " + logPath);
                return r.Failed > 0 ? 2 : 0;
            }
            catch (Exception e)
            {
                Out("ERROR: " + e.Message);
                return 1;
            }
            finally { if (file != null) file.Close(); }
        }

        public static int Run(string[] args)
        {
            var a = args.Select(x => x.ToLowerInvariant()).ToList();
            if (a.Contains("--help") || a.Contains("/?"))
            {
                Out("RetroSync.exe --dry-run [--saves | --config | --all] [--no-ps2] [--dolphin | --no-dolphin] [--log <file>]");
                Out("  Show what would be synced, without writing anything.");
                Out("RetroSync.exe --backup-ps5 [--move-deleted] [--log <file>]");
                Out("  One-way incremental backup PS5 -> PC (MirrorRemoteRoot -> MirrorLocalRoot), excluding MirrorExcludes. Does not write to the PS5.");
                Out("RetroSync.exe --baseline [--saves | --config | --all] [--log <file>]");
                Out("  Record the current PC and PS5 state as aligned in RetroSync.state.json; does not touch RetroArch.");
                Out("RetroSync.exe --wake [--backup-ps5 | --dry-run ...]");
                Out("  Wakes the PS5 from rest mode (Remote Play) and waits for FTP, then runs the rest.");
                Out("  --no-dolphin skips the Dolphin/Porpoise analysis (only run when Dolphin \u203A IncludeInSyncSave is on, or with --dolphin).");
                Out("RetroSync.exe --minimized");
                Out("  Start hidden in the notification area (used by \u201CStart with Windows\u201D).");
                Out("RetroSync.exe --demo [--page dashboard|retroarch|ps2|backup|settings|log]");
                Out("  Sample UI data for screenshots: nothing is read from or written anywhere.");
                return 0;
            }
            if (a.Contains("--wake"))
            {
                bool woke = WakeFirst();
                bool more = a.Contains("--backup-ps5") || a.Contains("--dry-run") || a.Contains("/dry-run") || a.Contains("--dryrun") || a.Contains("--baseline");
                if (!more)
                {
                    OpenLog(Path.Combine(SettingsStore.AppDir, "RetroSync-wake.log"));
                    if (file != null) file.Close();
                    return woke ? 0 : 3;
                }
                if (!woke)
                {
                    OpenLog(a.Contains("--backup-ps5") ? Path.Combine(SettingsStore.AppDir, "RetroSync-backup-ps5.log") : Path.Combine(SettingsStore.AppDir, "RetroSync-dryrun.log"));
                    if (file != null) file.Close();
                    return 3;
                }
                Out("");
            }
            if (a.Contains("--backup-ps5")) return BackupPs5(args, a);
            string logPath = Path.Combine(SettingsStore.AppDir, "RetroSync-dryrun.log");
            int li = a.IndexOf("--log");
            if (li >= 0 && li + 1 < args.Length) logPath = args[li + 1];
            OpenLog(logPath);

            SyncScope scope = a.Contains("--saves") ? SyncScope.Saves : a.Contains("--config") ? SyncScope.Config : SyncScope.All;
            bool baseline = a.Contains("--baseline");
            bool ps2 = !a.Contains("--no-ps2") && !baseline;
            int rc = 0;
            try
            {
                string err;
                var s = SettingsStore.Load(out err);
                if (err != null) Out("! " + err);
                LocalFs.ReadOnly = true;
                Out("RetroSync \u2014 " + (baseline ? "BASELINE (record current state as aligned)" : "PREVIEW (dry run)") + " " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
                Out("Scope: " + (scope == SyncScope.All ? "everything" : scope == SyncScope.Saves ? "saves" : "config"));
                Out("PC : " + s.PcRetroArchRoot);
                Out("PS5: ftp://" + s.FtpHost + ":" + s.FtpPort + s.Ps5RetroArchRoot);
                Out("Backup (solo in sync reale): " + s.BackupRoot + " (ultimi " + s.BackupKeep + ")");
                Out("");
                using (var eng = new SyncEngine(s, true, x => Out("  . " + x)))
                {
                    var plan = eng.BuildPlan(scope, null);
                    Out("");
                    Out(baseline ? "=== STATO PRIMA DELLA BASELINE ===" : "=== PIANO RetroArch ===");
                    Out(PlanPrinter.Format(plan, !baseline));
                    if (baseline)
                    {
                        int n = eng.Baseline(plan);
                        Out("Baseline registrata: " + n + " coppie PC/PS5 in " + SettingsStore.StatePath);
                        Out("Files present on only one side were not recorded and stay proposed.");
                    }
                }
                if (ps2 && (scope & SyncScope.Saves) != 0 && s.Ps2.IncludeInSyncSave)
                {
                    Out("");
                    Out("=== PS2 memory card (analysis only) ===");
                    try
                    {
                        var cfg = Config.FromApp(s);
                        var p = Ps2Engine.Analyze(cfg, x => Out("  . " + x), x => { },
                            (l, r) => { }, false);
                        Out("  Esito: " + p.Title + " \u2014 " + p.Reason);
                        if (!string.IsNullOrEmpty(p.Detail)) Out("  " + p.Detail.Replace("\n", "\n  "));
                        foreach (var it in p.Items.Where(i => i.Dir != 0 || i.Status.StartsWith("diverso")))
                            Out("    " + (it.Dir == 1 ? "PC \u2192 PS5" : it.Dir == 2 ? "PS5 \u2192 PC" : "\u2014") + "  " + it.Name + "  " + it.Title + "  (" + it.Status + ")");
                    }
                    catch (Exception e) { Out("  ERROR PS2: " + e.Message); rc = 1; }
                }
                bool dol = !baseline && (scope & SyncScope.Saves) != 0 && !a.Contains("--no-dolphin") && (a.Contains("--dolphin") || (s.Dolphin != null && s.Dolphin.IncludeInSyncSave));
                if (dol)
                {
                    Out("");
                    Out("=== Dolphin \u21C4 Porpoise (preview) ===");
                    Out("PC : " + s.Dolphin.PcUserDir);
                    Out("PS5: " + s.Dolphin.Ps5UserDir + (s.Dolphin.SyncWii ? "  (GC + Wii)" : "  (GC only)"));
                    try
                    {
                        using (var de = new DolphinEngine(s, true, x => Out("  . " + x)))
                        {
                            var dp = de.BuildPlan(null);
                            Out(DolphinEngine.Format(dp));
                        }
                    }
                    catch (Exception e) { var x = e; while (x.InnerException != null) x = x.InnerException; Out("  ERROR Dolphin: " + x.Message); rc = 1; }
                }
                Out("");
                Out((baseline ? "No RetroArch file modified (only RetroSync.state.json written)." : "No changes made (preview).") + " Log: " + logPath);
            }
            catch (Exception e)
            {
                var x = e; while (x.InnerException != null) x = x.InnerException;
                Out("ERROR: " + x.Message);
                rc = 2;
            }
            if (file != null) file.Close();
            return rc;
        }
    }
}
