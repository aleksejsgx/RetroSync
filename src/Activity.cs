using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Activity history (Dashboard "Recent activity", Backup "History") and the per-emulator folder backup
// framework. New in 2.0; it never touches the RetroArch / PS2 sync logic.
namespace RetroSync
{
    public class ActivityEntry
    {
        public long When;            // local time ticks
        public string Kind = "";     // Saves | RetroArch | All | PS2 | Backup | Wake
        public string Title = "";
        public string Detail = "";
        public string Status = "";   // ok | dry | error | cancel | info
        public int ToPs5, ToPc, Files;
        public long Bytes;

        [System.Web.Script.Serialization.ScriptIgnore]
        public DateTime Time { get { return new DateTime(When); } }
    }

    public class ActivityFile
    {
        public int Version = 1;
        public List<ActivityEntry> Items = new List<ActivityEntry>();
    }

    static class ActivityLog
    {
        public static readonly string FilePath = Path.Combine(SettingsStore.AppDir, "RetroSync.activity.json");
        static ActivityFile data;
        static readonly object gate = new object();
        public static event Action Changed;

        static ActivityFile Data
        {
            get
            {
                if (data == null) { data = SettingsStore.LoadJson<ActivityFile>(FilePath); if (data.Items == null) data.Items = new List<ActivityEntry>(); }
                return data;
            }
        }

        public static void Add(ActivityEntry e)
        {
            lock (gate)
            {
                if (e.When == 0) e.When = DateTime.Now.Ticks;
                Data.Items.Insert(0, e);
                if (Data.Items.Count > 200) Data.Items.RemoveRange(200, Data.Items.Count - 200);
                try { if (!Demo.On) SettingsStore.SaveJson(FilePath, Data); } catch { }
            }
            if (Changed != null) Changed();
        }

        public static List<ActivityEntry> Recent(int n, Func<ActivityEntry, bool> filter = null)
        {
            lock (gate) return Data.Items.Where(x => filter == null || filter(x)).Take(n).ToList();
        }

        public static ActivityEntry Last(Func<ActivityEntry, bool> filter)
        {
            lock (gate) return Data.Items.FirstOrDefault(filter);
        }

        public static void ReplaceForDemo(List<ActivityEntry> items) { lock (gate) data = new ActivityFile { Items = items }; }

        public static string When(DateTime t)
        {
            var now = DateTime.Now;
            if (t.Date == now.Date) return t.ToString("HH:mm");
            if (t.Date == now.Date.AddDays(-1)) return "yesterday";
            return t.ToString("dd/MM");
        }

        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " min ago";
            if (t.Date == DateTime.Now.Date) return "today, " + t.ToString("HH:mm");
            if (t.Date == DateTime.Now.Date.AddDays(-1)) return "yesterday, " + t.ToString("HH:mm");
            if (d.TotalDays < 14) return (int)Math.Round(d.TotalDays) + " days ago";
            return t.ToString("dd/MM/yyyy");
        }
    }

    // ------------------------------------------------------------------ per-emulator folder backups
    public class BackupSource
    {
        public string Label;          // sub folder name inside the backup version
        public string LocalPath;      // PC file or folder (null = none)
        public string RemotePath;     // PS5 file or folder via FTP, read only (null = none)
        public bool RemoteIsFile;
        public List<string> Excludes = new List<string>();
    }

    // A backup module copies a few folders of one emulator (PC and/or PS5) into
    // <BackupRoot>\Emulatori\<Title>\<yyyyMMdd-HHmmss>\{PC|PS5}\<Label>\... keeping the last N versions.
    interface IBackupModule
    {
        string Id { get; }
        string Title { get; }
        Ico Icon { get; }
        bool Available { get; }
        string Note { get; }                       // short description / "soon"
        List<BackupSource> Sources(AppSettings s);
    }

    class RetroArchBackup : IBackupModule
    {
        public string Id { get { return "retroarch"; } }
        public string Title { get { return "RetroArch"; } }
        public Ico Icon { get { return Ico.Gamepad; } }
        public bool Available { get { return true; } }
        public string Note { get { return "saves, states, config and retroarch.cfg \u2014 PC and PS5"; } }
        public List<BackupSource> Sources(AppSettings s)
        {
            var ex = new List<string> { "steam_autocloud.vdf", "*.retrosync.tmp", "*.tmp" };
            return new List<BackupSource>
            {
                new BackupSource { Label = "saves", LocalPath = Util.LocalJoin(s.PcRetroArchRoot, s.PcSavesDir), RemotePath = Util.RemoteJoin(s.Ps5RetroArchRoot, s.Ps5SavesDir), Excludes = ex },
                new BackupSource { Label = "states", LocalPath = Util.LocalJoin(s.PcRetroArchRoot, s.PcStatesDir), RemotePath = Util.RemoteJoin(s.Ps5RetroArchRoot, s.Ps5StatesDir), Excludes = ex },
                new BackupSource { Label = "config", LocalPath = Util.LocalJoin(s.PcRetroArchRoot, s.PcConfigDir), RemotePath = Util.RemoteJoin(s.Ps5RetroArchRoot, s.Ps5ConfigDir), Excludes = ex },
                new BackupSource { Label = "retroarch.cfg", LocalPath = Util.LocalJoin(s.PcRetroArchRoot, s.PcMainConfig), RemotePath = null }
            };
        }
    }

    class Ps2CardBackup : IBackupModule
    {
        public string Id { get { return "ps2"; } }
        public string Title { get { return "PS2 \u00B7 memory card"; } }
        public Ico Icon { get { return Ico.Card; } }
        public bool Available { get { return true; } }
        public string Note { get { return "copy of PCSX2 (PC) and PS5SX2 (PS5) cards"; } }
        public List<BackupSource> Sources(AppSettings s)
        {
            return new List<BackupSource>
            {
                new BackupSource { Label = "memcards", LocalPath = s.Ps2.LocalDir, Excludes = new List<string> { "*.partial", "*.tmp" } },
                new BackupSource { Label = "memcards", RemotePath = s.Ps2.RemoteDir.TrimEnd('/') + "/" + s.Ps2.CardName, RemoteIsFile = true }
            };
        }
    }

    class DolphinBackup : IBackupModule
    {
        public string Id { get { return "dolphin"; } }
        public string Title { get { return "GameCube / Wii (Dolphin)"; } }
        public Ico Icon { get { return Ico.Dolphin; } }
        public bool Available { get { return false; } }
        public string Note { get { return "GC/, Wii/title/ \u2014 support planned"; } }
        public List<BackupSource> Sources(AppSettings s) { return new List<BackupSource>(); }
    }

    class FolderBackupResult
    {
        public int Files, Failed; public long Bytes; public string Dir; public List<string> Notes = new List<string>();
    }

    static class FolderBackup
    {
        public static readonly IBackupModule[] Modules = { new RetroArchBackup(), new Ps2CardBackup(), new DolphinBackup() };

        public static string ModuleRoot(AppSettings s, IBackupModule m)
        {
            return Path.Combine(s.BackupRoot, "Emulatori", Safe(m.Title));
        }

        static string Safe(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '-'); return n.Replace("\u00B7", "-").Trim(); }

        static readonly Regex VersionRx = new Regex(@"^\d{8}-\d{6}$");

        public static List<DirectoryInfo> Versions(AppSettings s, IBackupModule m)
        {
            var root = ModuleRoot(s, m);
            if (!Directory.Exists(root)) return new List<DirectoryInfo>();
            return new DirectoryInfo(root).GetDirectories().Where(d => VersionRx.IsMatch(d.Name)).OrderByDescending(d => d.Name).ToList();
        }

        public static FolderBackupResult Run(AppSettings s, IBackupModule m, Action<string> log, Action<int> progress)
        {
            if (!m.Available) throw new InvalidOperationException(m.Title + ": not available yet.");
            var res = new FolderBackupResult();
            string dest = Path.Combine(ModuleRoot(s, m), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            res.Dir = dest;
            var sources = m.Sources(s);
            int step = 0;
            FtpClient ftp = null;
            try
            {
                foreach (var src in sources)
                {
                    step++;
                    if (!string.IsNullOrEmpty(src.LocalPath)) CopyLocal(src, Path.Combine(dest, "PC", src.Label), res, log);
                    if (!string.IsNullOrEmpty(src.RemotePath))
                    {
                        if (ftp == null)
                        {
                            int lat;
                            if (!Ps5Probe.FtpAnswers(s.FtpHost, s.FtpPort, 2000, out lat)) { res.Notes.Add("PS5 unreachable: only PC files were copied."); log("PS5 unreachable (" + s.FtpHost + ":" + s.FtpPort + "): skipping the PS5 part."); }
                            else { ftp = new FtpClient(s.FtpHost, s.FtpPort, s.FtpUser, s.FtpPassword, s.FtpTimeoutSeconds) { ReadOnly = true }; ftp.Connect(); }
                        }
                        if (ftp != null) CopyRemote(ftp, src, Path.Combine(dest, "PS5", src.Label), res, log);
                    }
                    if (progress != null) progress(step * 100 / Math.Max(1, sources.Count));
                }
            }
            finally { if (ftp != null) ftp.Dispose(); }
            Prune(s, m, log);
            return res;
        }

        static void CopyLocal(BackupSource src, string dest, FolderBackupResult res, Action<string> log)
        {
            if (File.Exists(src.LocalPath))
            {
                Directory.CreateDirectory(dest);
                var to = Path.Combine(dest, Path.GetFileName(src.LocalPath));
                File.Copy(src.LocalPath, to, true);
                res.Files++; res.Bytes += new FileInfo(to).Length;
                return;
            }
            if (!Directory.Exists(src.LocalPath)) { log("PC: " + src.LocalPath + " not found, skipping."); return; }
            foreach (var f in Directory.GetFiles(src.LocalPath, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.LocalPath.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
                if (Glob.AnyPath(src.Excludes, rel)) continue;
                try
                {
                    string to = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(f, to, true);
                    res.Files++; res.Bytes += new FileInfo(to).Length;
                }
                catch (Exception e) { res.Failed++; log("  ! " + rel + ": " + e.Message); }
            }
            log("PC " + src.Label + ": copied.");
        }

        static void CopyRemote(FtpClient ftp, BackupSource src, string dest, FolderBackupResult res, Action<string> log)
        {
            if (src.RemoteIsFile)
            {
                try
                {
                    var data = ftp.Download(src.RemotePath);
                    Directory.CreateDirectory(dest);
                    File.WriteAllBytes(Path.Combine(dest, src.RemotePath.Substring(src.RemotePath.LastIndexOf('/') + 1)), data);
                    res.Files++; res.Bytes += data.Length;
                    log("PS5 " + src.RemotePath + ": copied.");
                }
                catch (Exception e) { res.Failed++; log("  ! PS5 " + src.RemotePath + ": " + e.Message); }
                return;
            }
            var files = ftp.Walk(src.RemotePath);
            if (files == null) { log("PS5: " + src.RemotePath + " not found, skipping."); return; }
            foreach (var kv in files)
            {
                if (Glob.AnyPath(src.Excludes, kv.Key)) continue;
                try
                {
                    var data = ftp.Download(src.RemotePath.TrimEnd('/') + "/" + kv.Key);
                    string to = Path.Combine(dest, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.WriteAllBytes(to, data);
                    if (kv.Value.ModifiedUtc.HasValue) { try { File.SetLastWriteTimeUtc(to, kv.Value.ModifiedUtc.Value); } catch { } }
                    res.Files++; res.Bytes += data.Length;
                }
                catch (Exception e) { res.Failed++; log("  ! PS5 " + kv.Key + ": " + e.Message); }
            }
            log("PS5 " + src.Label + ": " + files.Count + " file.");
        }

        static void Prune(AppSettings s, IBackupModule m, Action<string> log)
        {
            int keep = Math.Max(1, s.Ui.EmulatorBackupKeep);
            foreach (var d in Versions(s, m).Skip(keep))
            {
                try { d.Delete(true); log("Old version removed: " + d.Name); } catch { }
            }
        }
    }
}
