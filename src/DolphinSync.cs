using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Dolphin (PC) <-> Porpoise (Dolphin port for PS5) save sync. New in 2.2.
// GameCube: per-game GCI files (Dolphin "GCI folder" mode) in User/GC/<EUR|USA|JAP>/Card A/.
//   Files are matched by the GCI header (game code + maker + internal file name), not by file name,
//   because different Dolphin builds name the same save differently.
// Wii (optional): User/Wii/title/<type>/<id>/data/** matched by relative path.
// Savestates are never synced (different Dolphin builds are not state-compatible).
namespace RetroSync
{
    public class DolphinSettings
    {
        public bool IncludeInSyncSave = false;                 // also analysed by the CLI preview (--dry-run)
        public string PcUserDir = "";                          // e.g. <dolphin>\User (portable) or %APPDATA%\Dolphin Emulator
        public string Ps5UserDir = "/data/porpoise/saves/User";
        public bool SyncGc = true;
        public bool SyncWii = false;
        public List<string> Regions = new List<string> { "EUR", "USA", "JAP" };
        public string CardFolder = "Card A";
        public bool MatchByHeader = true;
        public List<string> WiiTitleTypes = new List<string> { "00010000", "00010004" };   // disc games / disc games with channel
        public List<string> Excludes = new List<string> { "*.tmp", "*.retrosync.tmp", "desktop.ini", "Thumbs.db" };
    }

    class DolphinItem
    {
        public string Key, Display, Kind;
        public string PcPath, Ps5Path;        // targets (absolute): PC path, PS5 FTP path
        public bool PcExists, Ps5Exists;
        public DateTime? PcMtime, Ps5Mtime;
        public long PcSize, Ps5Size;
        public byte[] Ps5Data;                // downloaded during analysis (GCI headers / hashing)
        public string PcSha, Ps5Sha;
        public Dir Direction = Dir.None;
        public bool Conflict, Identical, Selected = true;
        public string Reason = "";
        public ItemState Prev;
    }

    class DolphinPlan
    {
        public List<DolphinItem> Items = new List<DolphinItem>();
        public List<string> Notes = new List<string>();
        public bool DryRun;
        public int ToPs5 { get { return Items.Count(i => i.Selected && i.Direction == Dir.ToPs5); } }
        public int ToPc { get { return Items.Count(i => i.Selected && i.Direction == Dir.ToPc); } }
    }

    static class Gci
    {
        // GCI = 0x40-byte DEntry + blocks. 0-3 game code, 4-5 maker, 8..39 file name (NUL padded).
        public static string HeaderKey(byte[] d)
        {
            if (d == null || d.Length < 0x40) return null;
            for (int i = 0; i < 6; i++) if (d[i] < 0x20 || d[i] > 0x7E) return null;
            string game = Encoding.ASCII.GetString(d, 0, 4), maker = Encoding.ASCII.GetString(d, 4, 2);
            int n = 0; while (n < 32 && d[8 + n] != 0) n++;
            string fn = Encoding.GetEncoding(28591).GetString(d, 8, n).Trim();
            return game + maker + "/" + fn;
        }

        public static string Title(byte[] d)
        {
            var k = HeaderKey(d);
            return k == null ? null : k.Substring(0, 6) + " \u00B7 " + k.Substring(7);
        }
    }

    class DolphinEngine : IDisposable
    {
        readonly AppSettings S;
        readonly DolphinSettings D;
        readonly bool dry;
        readonly Action<string> log;
        FtpClient ftp;
        readonly StateStore state;
        public static string StatePath { get { return Path.Combine(SettingsStore.DataDir, "RetroSync.dolphin-state.json"); } }

        public DolphinEngine(AppSettings s, bool dryRun, Action<string> log)
        {
            S = s; D = s.Dolphin ?? new DolphinSettings(); dry = dryRun; this.log = log ?? (x => { });
            state = new StateStore(StatePath);
        }

        public void Dispose() { if (ftp != null) { try { ftp.Dispose(); } catch { } ftp = null; } }

        FtpClient Ftp
        {
            get
            {
                if (ftp == null)
                {
                    ftp = new FtpClient(S.FtpHost, S.FtpPort, S.FtpUser, S.FtpPassword, S.FtpTimeoutSeconds)
                    { ReadOnly = dry, ListTimesUtc = S.Ps5ListTimesAreUtc, ClockOffsetMinutes = S.Ps5ClockOffsetMinutes };
                    ftp.Connect();
                }
                return ftp;
            }
        }

        string PsRoot { get { return "/" + (D.Ps5UserDir ?? "").Replace('\\', '/').Trim('/'); } }

        public DolphinPlan BuildPlan(Action<int> progress)
        {
            var plan = new DolphinPlan { DryRun = dry };
            if (string.IsNullOrEmpty(D.PcUserDir) || !Directory.Exists(D.PcUserDir))
                throw new InvalidOperationException("Dolphin User folder on the PC not found: \"" + D.PcUserDir + "\" (Settings \u203A Dolphin).");
            log("Connecting to the PS5 (" + S.FtpHost + ":" + S.FtpPort + ")\u2026");
            var top = Ftp.ListDir(PsRoot);
            if (top == null) throw new InvalidOperationException("Porpoise User folder not found on the PS5: " + PsRoot);
            if (D.SyncGc) { log("Analysing GameCube GCI saves\u2026"); BuildGc(plan); }
            if (progress != null) progress(50);
            if (D.SyncWii) { log("Analysing Wii title data\u2026"); BuildWii(plan); }
            if (progress != null) progress(100);
            return plan;
        }

        // ------------------------------------------------------------ GameCube
        void BuildGc(DolphinPlan plan)
        {
            foreach (var region in D.Regions)
            {
                string pcDir = Path.Combine(D.PcUserDir, "GC", region, D.CardFolder);
                string psDir = PsRoot + "/GC/" + region + "/" + D.CardFolder;
                var pc = new Dictionary<string, Tuple<FileInfo, byte[]>>(StringComparer.OrdinalIgnoreCase);
                var ps = new Dictionary<string, Tuple<RemoteEntry, byte[]>>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(pcDir))
                    foreach (var f in new DirectoryInfo(pcDir).GetFiles("*.gci"))
                    {
                        if (Glob.AnyPath(D.Excludes, f.Name)) continue;
                        var b = File.ReadAllBytes(f.FullName);
                        string k = KeyFor(b, f.Name);
                        if (pc.ContainsKey(k)) { plan.Notes.Add("PC " + region + ": " + f.Name + " has the same GCI header as " + pc[k].Item1.Name + ": ignored."); continue; }
                        pc[k] = Tuple.Create(f, b);
                    }
                var list = Ftp.ListDir(psDir);
                if (list != null)
                    foreach (var e in list.Where(x => !x.IsDir && x.Name.EndsWith(".gci", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (Glob.AnyPath(D.Excludes, e.Name)) continue;
                        var b = Ftp.Download(psDir + "/" + e.Name);
                        string k = KeyFor(b, e.Name);
                        if (ps.ContainsKey(k)) { plan.Notes.Add("PS5 " + region + ": " + e.Name + " has the same GCI header as " + ps[k].Item1.Name + ": ignored."); continue; }
                        ps[k] = Tuple.Create(e, b);
                    }
                plan.Notes.Add("GC " + region + ": PC " + pc.Count + " GCI \u21C4 PS5 " + ps.Count + " GCI" + (list == null ? " (PS5 folder missing, will be created)" : ""));
                var psNames = new HashSet<string>(ps.Values.Select(v => v.Item1.Name), StringComparer.OrdinalIgnoreCase);
                var pcNames = new HashSet<string>(pc.Values.Select(v => v.Item1.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var k in pc.Keys.Union(ps.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    Tuple<FileInfo, byte[]> a; Tuple<RemoteEntry, byte[]> r;
                    pc.TryGetValue(k, out a); ps.TryGetValue(k, out r);
                    var it = new DolphinItem { Kind = "GC " + region, Key = "gc|" + region + "|" + k };
                    byte[] any = a != null ? a.Item2 : r.Item2;
                    it.Display = region + "  " + (Gci.Title(any) ?? (a != null ? a.Item1.Name : r.Item1.Name));
                    string pcName = a != null ? a.Item1.Name : r.Item1.Name;
                    string psName = r != null ? r.Item1.Name : a.Item1.Name;
                    it.PcPath = Path.Combine(pcDir, pcName);
                    it.Ps5Path = psDir + "/" + psName;
                    if (a != null) { it.PcExists = true; it.PcMtime = a.Item1.LastWriteTimeUtc; it.PcSize = a.Item1.Length; it.PcSha = Util.Sha(a.Item2); }
                    if (r != null) { it.Ps5Exists = true; it.Ps5Mtime = r.Item1.ModifiedUtc; it.Ps5Size = r.Item2.Length; it.Ps5Data = r.Item2; it.Ps5Sha = Util.Sha(r.Item2); }
                    // name clash: target file name already used by another save on the other side
                    if (a == null && pcNames.Contains(pcName) || r == null && psNames.Contains(psName))
                    {
                        it.Reason = "a different save already uses the file name " + (a == null ? pcName : psName) + ": skipped";
                        it.Selected = false; plan.Items.Add(it); continue;
                    }
                    if (a != null && r != null && !string.Equals(a.Item1.Name, r.Item1.Name, StringComparison.OrdinalIgnoreCase))
                        it.Display += "   (PC: " + a.Item1.Name + " \u2194 PS5: " + r.Item1.Name + ")";
                    Decide(it);
                    plan.Items.Add(it);
                }
            }
        }

        string KeyFor(byte[] data, string name)
        {
            string h = D.MatchByHeader ? Gci.HeaderKey(data) : null;
            return h ?? "file:" + name;
        }

        // ------------------------------------------------------------ Wii
        void BuildWii(DolphinPlan plan)
        {
            foreach (var type in D.WiiTitleTypes)
            {
                string pcType = Path.Combine(D.PcUserDir, "Wii", "title", type);
                string psType = PsRoot + "/Wii/title/" + type;
                var pc = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(pcType))
                    foreach (var f in Directory.GetFiles(pcType, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(pcType.Length).TrimStart('\\').Replace('\\', '/');
                        var parts = rel.Split('/');
                        if (parts.Length < 3 || !parts[1].Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
                        if (Glob.AnyPath(D.Excludes, parts[parts.Length - 1])) continue;
                        pc[rel] = new FileInfo(f);
                    }
                var ps = Ftp.Walk(psType, 8) ?? new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
                var psData = ps.Where(kv => { var p = kv.Key.Split('/'); return p.Length >= 3 && p[1].Equals("data", StringComparison.OrdinalIgnoreCase) && !Glob.AnyPath(D.Excludes, p[p.Length - 1]); })
                               .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                plan.Notes.Add("Wii " + type + ": PC " + pc.Count + " files \u21C4 PS5 " + psData.Count + " files");
                foreach (var rel in pc.Keys.Union(psData.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    FileInfo f; RemoteEntry e;
                    pc.TryGetValue(rel, out f); psData.TryGetValue(rel, out e);
                    var it = new DolphinItem { Kind = "Wii", Key = "wii|" + type + "/" + rel, Display = "Wii " + WiiId(rel) + "  " + rel.Substring(rel.IndexOf('/') + 1) };
                    it.PcPath = Path.Combine(pcType, rel.Replace('/', '\\'));
                    it.Ps5Path = psType + "/" + rel;
                    if (f != null) { it.PcExists = true; it.PcMtime = f.LastWriteTimeUtc; it.PcSize = f.Length; it.PcSha = Util.Sha(File.ReadAllBytes(f.FullName)); }
                    if (e != null)
                    {
                        it.Ps5Exists = true; it.Ps5Mtime = e.ModifiedUtc; it.Ps5Size = e.Size;
                        var prev = state.Get(it.Key);
                        if (prev != null && prev.RemoteSha != null && prev.RemoteSize == e.Size && e.ModifiedUtc.HasValue && prev.RemoteMtime == e.ModifiedUtc.Value.Ticks)
                            it.Ps5Sha = prev.RemoteSha;   // unchanged since last sync: no download
                        else { it.Ps5Data = Ftp.Download(it.Ps5Path); it.Ps5Sha = Util.Sha(it.Ps5Data); }
                    }
                    Decide(it);
                    plan.Items.Add(it);
                }
            }
        }

        static string WiiId(string rel)
        {
            // title id low word is usually the 4-char game id in hex (e.g. 524d4750 = RMGP)
            string hex = rel.Split('/')[0];
            try
            {
                if (hex.Length == 8)
                {
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                    if (b.All(x => x >= 0x20 && x < 0x7F)) return Encoding.ASCII.GetString(b);
                }
            }
            catch { }
            return hex;
        }

        // ------------------------------------------------------------ decision (same rules as RetroArch saves)
        void Decide(DolphinItem it)
        {
            it.Prev = state.Get(it.Key);
            if (!it.PcExists || !it.Ps5Exists)
            {
                Dir d = it.PcExists ? Dir.ToPs5 : Dir.ToPc;
                if (it.Prev != null) { it.Direction = Dir.None; it.Reason = it.PcExists ? "deleted on the PS5 since last sync (will not re-copy)" : "deleted on the PC since last sync (will not re-copy)"; }
                else { it.Direction = d; it.Reason = it.PcExists ? "PC only" : "PS5 only"; }
                return;
            }
            if (it.PcSha == it.Ps5Sha) { it.Identical = true; it.Reason = "identical"; return; }
            bool pcCh = it.Prev == null || it.Prev.LocalSha != it.PcSha;
            bool psCh = it.Prev == null || it.Prev.RemoteSha != it.Ps5Sha;
            if (pcCh && !psCh) { it.Direction = Dir.ToPs5; it.Reason = "changed on the PC since last sync"; }
            else if (!pcCh && psCh) { it.Direction = Dir.ToPc; it.Reason = "changed on the PS5 since last sync"; }
            else if (!pcCh && !psCh) { it.Identical = true; it.Reason = "different, but neither changed since last sync"; }
            else
            {
                it.Conflict = true;
                Dir n = Newer(it);
                it.Direction = n;
                it.Reason = (it.Prev == null ? "no history, " : "changed on both sides, ") + (n == Dir.ToPs5 ? "newer on PC" : n == Dir.ToPc ? "newer on PS5" : "same time: choose yourself");
                if (n == Dir.None) it.Selected = false;
            }
        }

        Dir Newer(DolphinItem it)
        {
            if (!it.PcMtime.HasValue || !it.Ps5Mtime.HasValue) return Dir.None;
            double d = (it.PcMtime.Value - it.Ps5Mtime.Value).TotalSeconds;
            if (Math.Abs(d) <= S.TimeToleranceSeconds) return Dir.None;
            return d > 0 ? Dir.ToPs5 : Dir.ToPc;
        }

        // ------------------------------------------------------------ apply
        public int Apply(DolphinPlan plan, Action<int> progress)
        {
            if (dry) throw new InvalidOperationException("Preview mode: no changes applied.");
            var bk = new BackupManager(Path.Combine(S.BackupRoot, "Dolphin"), S.BackupKeep, log);
            var todo = plan.Items.Where(i => i.Selected && i.Direction != Dir.None).ToList();
            int done = 0, n = 0;
            foreach (var it in todo)
            {
                n++;
                string rel = it.Key.Replace('|', '/').Replace(':', '_');
                if (it.Direction == Dir.ToPs5)
                {
                    var data = File.ReadAllBytes(it.PcPath);
                    if (it.Ps5Exists) bk.SaveBytes("PS5", rel + "/" + Path.GetFileName(it.Ps5Path.Replace('/', '\\')), it.Ps5Data ?? Ftp.Download(it.Ps5Path));
                    int slash = it.Ps5Path.LastIndexOf('/');
                    Ftp.MakeDirs(it.Ps5Path.Substring(0, slash));
                    Ftp.Upload(it.Ps5Path, data);
                    log("\u2192 PS5  " + it.Ps5Path);
                    Record(it, Util.Sha(data), data.Length, File.GetLastWriteTimeUtc(it.PcPath), true);
                }
                else
                {
                    var data = it.Ps5Data ?? Ftp.Download(it.Ps5Path);
                    if (it.PcExists) bk.SaveLocal("PC", rel + "/" + Path.GetFileName(it.PcPath), it.PcPath);
                    LocalFs.Write(it.PcPath, data, it.Ps5Mtime);
                    log("\u2192 PC   " + it.PcPath);
                    Record(it, Util.Sha(data), data.Length, it.Ps5Mtime ?? DateTime.UtcNow, false);
                }
                done++;
                if (progress != null) progress(n * 100 / Math.Max(1, todo.Count));
            }
            // identical pairs become the new baseline
            foreach (var it in plan.Items.Where(i => i.Identical && i.PcExists && i.Ps5Exists && i.PcSha == i.Ps5Sha))
                state.Set(it.Key, new ItemState { LocalSha = it.PcSha, RemoteSha = it.Ps5Sha, LocalSize = it.PcSize, RemoteSize = it.Ps5Size,
                    LocalMtime = it.PcMtime.HasValue ? it.PcMtime.Value.Ticks : 0, RemoteMtime = it.Ps5Mtime.HasValue ? it.Ps5Mtime.Value.Ticks : 0, Synced = DateTime.UtcNow.Ticks });
            state.Save();
            bk.Prune();
            if (bk.Count > 0) log("Backup of " + bk.Count + " overwritten file(s): " + bk.SessionDir);
            return done;
        }

        void Record(DolphinItem it, string sha, long size, DateTime mtimeUtc, bool toPs5)
        {
            // re-read the PS5 entry time after an upload so the next analysis does not see a change
            long remoteTicks = mtimeUtc.Ticks;
            if (toPs5)
            {
                try
                {
                    int slash = it.Ps5Path.LastIndexOf('/');
                    var e = Ftp.ListDir(it.Ps5Path.Substring(0, slash)).FirstOrDefault(x => x.Name == it.Ps5Path.Substring(slash + 1));
                    if (e != null && e.ModifiedUtc.HasValue) remoteTicks = e.ModifiedUtc.Value.Ticks;
                }
                catch { }
            }
            state.Set(it.Key, new ItemState { LocalSha = sha, RemoteSha = sha, LocalSize = size, RemoteSize = size, LocalMtime = mtimeUtc.Ticks, RemoteMtime = remoteTicks, Synced = DateTime.UtcNow.Ticks });
        }

        public static string Format(DolphinPlan p)
        {
            var sb = new StringBuilder();
            foreach (var n in p.Notes) sb.AppendLine("  * " + n);
            sb.AppendLine();
            int same = p.Items.Count(i => i.Identical);
            sb.AppendLine("  Items: " + p.Items.Count + " total, " + same + " already aligned. To PS5: " + p.ToPs5 + "   To PC: " + p.ToPc + "   Conflicts: " + p.Items.Count(i => i.Conflict));
            foreach (var i in p.Items.Where(x => !x.Identical))
                sb.AppendLine("    " + (i.Direction == Dir.ToPs5 ? "PC \u2192 PS5" : i.Direction == Dir.ToPc ? "PS5 \u2192 PC" : "   \u2014   ") + (i.Selected ? "  " : " [ ] ") + i.Display + "  (" + i.Reason + ")");
            return sb.ToString();
        }
    }
}
