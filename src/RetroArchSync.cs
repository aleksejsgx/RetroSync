using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroSync
{
    [Flags]
    enum SyncScope { Saves = 1, Config = 2, All = 3 }

    enum ItemKind { Raw, KeyValue, Playlist }

    // One files (or file pair) that the plan may touch.
    class SyncItem
    {
        public string Category, Key;
        public ItemKind Kind;
        public string PcFull, PcRel, Ps5Path, Ps5Rel;   // PcRel / Ps5Rel are relative to the RetroArch roots
        public bool PcExists, Ps5Exists;
        public long PcSize, Ps5Size;
        public DateTime? PcMtime, Ps5Mtime;              // UTC
        public RemoteEntry Ps5Entry;
        public string PcSha, Ps5Sha;
        public byte[] PcBytes, Ps5Bytes;
        public ItemState Prev;
        public KeyFilter Filter;
        public KvMergeResult MergePreview;

        public Dir Direction = Dir.None;
        public List<Dir> Options = new List<Dir>();
        public bool Conflict, Identical, Info, Selected = true;
        public string Reason = "";
        public List<string> Details = new List<string>();

        public string ExcludeList, ExcludePattern;   // "Always ignore": settings list + pattern to add

        public string Display { get { return PcExists || Ps5Rel == null ? PcRel : Ps5Rel; } }
        public bool Actionable { get { return Direction != Dir.None && Selected; } }
    }

    class SyncPlan
    {
        public SyncScope Scope;
        public List<SyncItem> Items = new List<SyncItem>();
        public List<string> Notes = new List<string>();
        public DateTime Created = DateTime.Now;

        public IEnumerable<SyncItem> Visible { get { return Items.Where(i => !i.Identical); } }
        public int CountTo(Dir d) { return Items.Count(i => i.Actionable && (i.Direction == d || (i.Direction == Dir.Merge && d != Dir.None))); }
    }

    static class DirText
    {
        public static string Of(Dir d)
        {
            switch (d)
            {
                case Dir.ToPs5: return "PC \u2192 PS5";
                case Dir.ToPc: return "PS5 \u2192 PC";
                case Dir.Merge: return "PC \u21C4 PS5 (unisci)";
                default: return "\u2014 salta";
            }
        }
    }

    // Shared helpers and caches for one planning/apply session.
    class RaContext
    {
        public AppSettings S;
        public FtpClient Ftp;
        public StateStore State;
        public Action<string> Log;
        public PathTranslator Tr;
        public string PcRoot, Ps5Root;
        readonly Dictionary<string, List<RemoteEntry>> dirCache = new Dictionary<string, List<RemoteEntry>>(StringComparer.Ordinal);
        KvFile pcCfg, psCfg;

        public RaContext(AppSettings s, FtpClient ftp, StateStore st, Action<string> log)
        {
            S = s; Ftp = ftp; State = st; Log = log; Tr = new PathTranslator(s);
            PcRoot = s.PcRetroArchRoot;
            Ps5Root = Util.RemoteJoin(s.Ps5RetroArchRoot, "").TrimEnd('/');
        }

        public string Ps5Abs(string rel) { return Util.RemoteJoin(Ps5Root, rel); }
        public string PcAbs(string rel) { return Util.LocalJoin(PcRoot, rel); }

        public static string Parent(string p) { int i = p.LastIndexOf('/'); return i <= 0 ? "/" : p.Substring(0, i); }
        static string Name(string p) { return p.Substring(p.LastIndexOf('/') + 1); }

        public List<RemoteEntry> ListDir(string ftpDir)
        {
            List<RemoteEntry> l;
            if (dirCache.TryGetValue(ftpDir, out l)) return l;
            l = Ftp.ListDir(ftpDir);
            dirCache[ftpDir] = l;
            return l;
        }

        public void Invalidate(string ftpDir) { dirCache.Remove(ftpDir); }

        public RemoteEntry Stat(string ftpPath)
        {
            var l = ListDir(Parent(ftpPath));
            if (l == null) return null;
            string n = Name(ftpPath);
            return l.FirstOrDefault(e => !e.IsDir && e.Name == n) ?? l.FirstOrDefault(e => !e.IsDir && string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
        }

        public Dictionary<string, RemoteEntry> RemoteWalk(string rel)
        {
            return Ftp.Walk(Ps5Abs(rel));
        }

        public static Dictionary<string, FileInfo> LocalWalk(string dir)
        {
            var res = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir)) return null;
            string root = dir.TrimEnd('\\') + "\\";
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                res[f.Substring(root.Length).Replace('\\', '/')] = new FileInfo(f);
            return res;
        }

        public KvFile PcCfg
        {
            get
            {
                if (pcCfg == null)
                {
                    string p = PcAbs(S.PcMainConfig);
                    pcCfg = File.Exists(p) ? KvFile.Parse(File.ReadAllBytes(p)) : new KvFile();
                }
                return pcCfg;
            }
        }

        public KvFile Ps5Cfg
        {
            get
            {
                if (psCfg == null)
                {
                    try { psCfg = KvFile.Parse(Ftp.Download(Ps5Abs(S.Ps5MainConfig))); }
                    catch (Exception e) { Log("PS5 retroarch.cfg unreadable: " + e.Message); psCfg = new KvFile(); }
                }
                return psCfg;
            }
        }

        // Existence of a content file on the PS5, given its RetroArch (/app0/...) path.
        public bool Ps5ContentExists(string appPath)
        {
            string f = Tr.AppToFtp(appPath);
            if (f == null) return false;
            try { return Stat(f) != null; } catch { return false; }
        }
    }

    // Finds the RetroArch core folder ("Mupen64Plus-Next", "Snes9x", ...) a save belongs to,
    // needed when one side sorts saves per core and the other does not.
    class CoreIndex
    {
        readonly Dictionary<string, string> byGame = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> howGame = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> bySub = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly Regex SaveExt = new Regex(@"^(srm|sav|mcr|mcd|bcr|bkr|smpc|eep|eeprom|fla|sra|mpk|rtc|ram|nv|nvram|dsv|sram|state\d*|auto|png|bsv|ldci|bak|lrtl|opt|cfg|slangp|glslp|cgp|rmp)$", RegexOptions.IgnoreCase);
        static readonly Regex Digits = new Regex(@"^\d+$");

        public static string GameBase(string fileName)
        {
            string n = fileName;
            while (true)
            {
                int i = n.LastIndexOf('.');
                if (i <= 0) break;
                string ext = n.Substring(i + 1);
                if (SaveExt.IsMatch(ext) || Digits.IsMatch(ext)) n = n.Substring(0, i);
                else break;
            }
            return n;
        }

        public void AddGame(string game, string core, string how)
        {
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(core) || byGame.ContainsKey(game)) return;
            byGame[game] = core; howGame[game] = how;
        }

        public void AddSub(string sub, string core)
        {
            if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(core) || bySub.ContainsKey(sub)) return;
            bySub[sub] = core;
        }

        // core-wide files inside a core sub tree (e.g. opera/shared/nvram.0.srm) are not games
        public static bool IsSharedName(string g)
        {
            return string.IsNullOrEmpty(g) || g.Equals("nvram", StringComparison.OrdinalIgnoreCase) || g.Equals("shared", StringComparison.OrdinalIgnoreCase);
        }

        // games found inside a mapped PC sub folder (flat PC layout, e.g. opera/per_game/<game>.0.srm)
        public void AddSubGames(IEnumerable<string> flatRels)
        {
            foreach (var rel in flatRels)
            {
                int slash = rel.IndexOf('/');
                if (slash <= 0) continue;
                string core;
                if (!bySub.TryGetValue(rel.Substring(0, slash), out core)) continue;
                string g = GameBase(rel.Substring(rel.LastIndexOf('/') + 1));
                if (!IsSharedName(g)) AddGame(g, core, "subfolder " + rel.Substring(0, slash) + " (" + core + ")");
            }
        }

        public static string CoreFromName(string coreName)
        {
            // "Nintendo - Nintendo 64 (Mupen64Plus-Next)" -> "Mupen64Plus-Next"
            if (string.IsNullOrEmpty(coreName)) return null;
            var m = Regex.Match(coreName, @"\(([^()]+)\)\s*$");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public string Resolve(string flatRel, out string how)
        {
            how = null;
            int slash = flatRel.IndexOf('/');
            if (slash > 0)
            {
                string core;
                if (bySub.TryGetValue(flatRel.Substring(0, slash), out core)) { how = "subfolder " + flatRel.Substring(0, slash); return core; }
                return null;
            }
            string g = GameBase(flatRel);
            string c;
            if (byGame.TryGetValue(g, out c)) { how = howGame[g]; return c; }
            if (byGame.TryGetValue(flatRel, out c)) { how = howGame[flatRel]; return c; }
            return null;
        }

        public int Count { get { return byGame.Count; } }
    }

    // UI-agnostic sync engine. GUI, the --dry-run CLI and (later) a background watcher all use it:
    // BuildPlan() only reads; Apply() writes the selected items with backups and updates the state.
    class SyncEngine : IDisposable
    {
        readonly AppSettings S;
        readonly bool dryRun;
        readonly Action<string> log;
        readonly RaContext c;
        readonly StateStore state;

        public SyncEngine(AppSettings s, bool dryRun, Action<string> log)
        {
            S = s; this.dryRun = dryRun; this.log = log ?? (x => { });
            LocalFs.ReadOnly = dryRun;
            state = new StateStore(SettingsStore.StatePath);
            var ftp = new FtpClient(s.FtpHost, s.FtpPort, s.FtpUser, s.FtpPassword, s.FtpTimeoutSeconds)
            {
                ReadOnly = dryRun, ListTimesUtc = s.Ps5ListTimesAreUtc, ClockOffsetMinutes = s.Ps5ClockOffsetMinutes, Trace = this.log
            };
            c = new RaContext(s, ftp, state, this.log);
        }

        public void Dispose() { try { c.Ftp.Dispose(); } catch { } }

        public static bool PcRetroArchRunning()
        {
            return Process.GetProcesses().Any(p => p.ProcessName.StartsWith("retroarch", StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ planning
        public SyncPlan BuildPlan(SyncScope scope, Action<int> progress)
        {
            if (progress == null) progress = x => { };
            var plan = new SyncPlan { Scope = scope };
            if (!Directory.Exists(S.PcRetroArchRoot)) throw new Exception("PC RetroArch folder not found: " + S.PcRetroArchRoot);
            log("Mi collego al PS5 (" + S.FtpHost + ":" + S.FtpPort + ")\u2026");
            c.Ftp.Connect();
            if (c.ListDir(c.Ps5Root) == null) throw new Exception("PS5 RetroArch folder not found: " + c.Ps5Root);
            progress(5);
            if ((scope & SyncScope.Saves) != 0)
            {
                BuildSaves(plan);
                progress((scope & SyncScope.Config) != 0 ? 50 : 100);
            }
            if ((scope & SyncScope.Config) != 0)
            {
                BuildConfig(plan);
                progress(100);
            }
            if (PcRetroArchRunning()) plan.Notes.Add("WARNING: RetroArch is open on the PC: close it before applying (it rewrites the config on exit).");
            return plan;
        }

        SyncItem NewItem(string cat, string key, ItemKind kind, string pcRel, FileInfo pcFi, string psRel, RemoteEntry re)
        {
            var it = new SyncItem { Category = cat, Key = cat + "|" + key, Kind = kind, PcRel = pcRel, Ps5Rel = psRel };
            it.PcFull = pcRel == null ? null : c.PcAbs(pcRel);
            it.Ps5Path = psRel == null ? null : c.Ps5Abs(psRel);
            if (pcFi != null && pcFi.Exists) { it.PcExists = true; it.PcSize = pcFi.Length; it.PcMtime = pcFi.LastWriteTimeUtc; }
            if (re != null) { it.Ps5Exists = true; it.Ps5Size = re.Size; it.Ps5Mtime = re.ModifiedUtc; it.Ps5Entry = re; }
            it.Prev = state.Get(it.Key);
            return it;
        }

        static Dictionary<string, string> PrevBase(SyncItem it, bool pc)
        {
            if (it.Prev == null) return null;
            return pc ? it.Prev.Base : (it.Prev.BasePs5 ?? it.Prev.Base);
        }

        static bool IsConfigCategory(string cat) { return cat != "Saves" && cat != "Stati"; }

        byte[] PcRead(SyncItem it) { if (it.PcBytes == null) it.PcBytes = File.ReadAllBytes(it.PcFull); return it.PcBytes; }
        byte[] PsRead(SyncItem it) { if (it.Ps5Bytes == null) it.Ps5Bytes = c.Ftp.Download(it.Ps5Path); return it.Ps5Bytes; }

        Dir Newer(SyncItem it)
        {
            if (!it.PcMtime.HasValue || !it.Ps5Mtime.HasValue) return it.PcMtime.HasValue ? Dir.ToPs5 : it.Ps5Mtime.HasValue ? Dir.ToPc : Dir.None;
            double d = (it.PcMtime.Value - it.Ps5Mtime.Value).TotalSeconds;
            if (Math.Abs(d) <= S.TimeToleranceSeconds) return Dir.None;
            return d > 0 ? Dir.ToPs5 : Dir.ToPc;
        }

        // Handles the "file exists on one side only" case. Returns true when handled.
        bool DecideOneSided(SyncItem it)
        {
            if (it.PcExists && it.Ps5Exists) return false;
            if (!it.PcExists && !it.Ps5Exists) { it.Identical = true; return true; }
            if (it.Ps5Rel == null || it.PcRel == null)
            {
                it.Info = true; it.Options = new List<Dir> { Dir.None };
                it.Reason = it.Ps5Rel == null ? "unknown core: do not know which PS5 folder to use (see GameCoreOverrides)" : "unknown core: do not know where to put it on the PC";
                return true;
            }
            Dir d = it.PcExists ? Dir.ToPs5 : Dir.ToPc;
            it.Options = new List<Dir> { d, Dir.None };
            if (it.Prev != null)
            {
                it.Direction = Dir.None;
                it.Reason = it.PcExists ? "deleted on the PS5 since last sync (will not re-copy)" : "deleted on the PC since last sync (will not re-copy)";
            }
            else
            {
                it.Direction = d;
                it.Reason = it.PcExists ? "PC only" : "PS5 only";
                // a config file present on one side only is often a leftover: proposed, but not ticked
                if (IsConfigCategory(it.Category)) { it.Selected = false; it.Reason += " (unchecked: your choice)"; }
            }
            return true;
        }

        void DecideByHistory(SyncItem it, bool equal)
        {
            it.Options = new List<Dir> { Dir.ToPs5, Dir.ToPc, Dir.None };
            if (equal) { it.Identical = true; it.Reason = "identico"; return; }
            bool pcCh = it.Prev == null || it.Prev.LocalSha != it.PcSha;
            bool psCh = it.Prev == null || it.Prev.RemoteSha != it.Ps5Sha;
            if (pcCh && !psCh) { it.Direction = Dir.ToPs5; it.Reason = "changed on the PC since last sync"; }
            else if (!pcCh && psCh) { it.Direction = Dir.ToPc; it.Reason = "changed on the PS5 since last sync"; }
            else if (!pcCh && !psCh) { it.Direction = Dir.None; it.Identical = true; it.Reason = "different, but neither changed since last sync / baseline"; }
            else
            {
                it.Conflict = true;
                Dir n = Newer(it);
                it.Direction = n;
                string pre = it.Prev == null ? "no history, " : "changed on both sides, ";
                it.Reason = pre + (n == Dir.ToPs5 ? "newer on PC" : n == Dir.ToPc ? "newer on PS5" : "stessa data: scegli tu");
            }
        }

        void EvaluateRaw(SyncItem it)
        {
            if (DecideOneSided(it)) return;
            it.PcSha = Util.Sha(PcRead(it));
            var p = it.Prev;
            if (p != null && p.RemoteSha != null && p.RemoteSize == it.Ps5Size && it.Ps5Mtime.HasValue && p.RemoteMtime == it.Ps5Mtime.Value.Ticks)
                it.Ps5Sha = p.RemoteSha;   // unchanged on the PS5 since the last sync: no download needed
            else
                it.Ps5Sha = Util.Sha(PsRead(it));
            DecideByHistory(it, it.PcSha == it.Ps5Sha);
        }

        void EvaluateKv(SyncItem it)
        {
            if (!it.PcExists || !it.Ps5Exists)
            {
                if (DecideOneSided(it)) { if (it.Direction != Dir.None || it.Options.Count > 1) AddFilteredPreview(it); return; }
            }
            var pcB = PcRead(it); var psB = PsRead(it);
            it.PcSha = Util.Sha(pcB); it.Ps5Sha = Util.Sha(psB);
            Dir n = Newer(it);
            bool pcNewer = n == Dir.ToPs5 || (n == Dir.None && it.PcMtime >= it.Ps5Mtime);
            var r = KvMerger.Merge(pcB, psB, PrevBase(it, true), PrevBase(it, false), it.Filter, pcNewer, Dir.Merge);
            it.MergePreview = r;
            it.Options = new List<Dir> { Dir.Merge, Dir.ToPs5, Dir.ToPc, Dir.None };
            if (r.NewPc == null && r.NewPs5 == null)
            {
                it.Identical = true;
                it.Reason = "shared keys identical (" + r.SharedCommon + " shared, " + r.Skipped + " platform keys ignored)";
                return;
            }
            it.Direction = r.NewPc != null && r.NewPs5 != null ? Dir.Merge : r.NewPs5 != null ? Dir.ToPs5 : Dir.ToPc;
            it.Conflict = r.Conflicts.Count > 0;
            it.Reason = r.ToPs5.Count + " keys \u2192 PS5, " + r.ToPc.Count + " \u2192 PC" + (it.Conflict ? ", " + r.Conflicts.Count + " in conflict (newer file wins: " + (pcNewer ? "PC" : "PS5") + ")" : "")
                + (it.Prev == null ? " [primo sync]" : "");
            it.Details.Add("Shared keys in common: " + r.SharedCommon + ", platform/path keys ignored: " + r.Skipped);
            foreach (var x in r.ToPs5) it.Details.Add("\u2192 PS5  " + x);
            foreach (var x in r.ToPc) it.Details.Add("\u2192 PC   " + x);
            foreach (var x in r.Conflicts) it.Details.Add("CONFLITTO  " + x);
        }

        void AddFilteredPreview(SyncItem it)
        {
            try
            {
                int kept, dropped; Dictionary<string, string> b;
                var src = it.PcExists ? PcRead(it) : PsRead(it);
                var copy = KvMerger.FilteredCopy(src, it.Filter, out kept, out dropped, out b);
                if (copy == null) { it.Direction = Dir.None; it.Options = new List<Dir> { Dir.None }; it.Info = true; it.Reason += " \u2014 no shareable keys, will not copy"; return; }
                it.Details.Add("Will copy " + kept + " shared keys, " + dropped + " platform keys excluded.");
            }
            catch (Exception e) { it.Details.Add("Read error: " + e.Message); }
        }

        void EvaluatePlaylist(SyncItem it)
        {
            if (DecideOneSided(it))
            {
                if (it.Direction != Dir.None)
                {
                    PreviewPlaylist(it, it.Direction);
                    try
                    {
                        if (PlaylistConverter.ItemCount(ConvertPlaylist(it, it.Direction, new List<string>())) == 0)
                        {
                            it.Direction = Dir.None; it.Info = true; it.Options = new List<Dir> { Dir.None };
                            it.Reason += " \u2014 no entries whose content exists on the other side: will not create it";
                        }
                    }
                    catch { }
                }
                return;
            }
            var pcB = PcRead(it); var psB = PsRead(it);
            it.PcSha = Util.Sha(pcB); it.Ps5Sha = Util.Sha(psB);
            bool equal;
            try
            {
                var notes = new List<string>();
                string conv = PlaylistConverter.Convert(Util.Utf8.GetString(pcB), true, c.Tr, null, notes, Util.Utf8.GetString(psB));
                equal = PlaylistConverter.Normalize(conv) == PlaylistConverter.Normalize(Util.Utf8.GetString(psB));
            }
            catch (Exception e) { it.Details.Add("Playlist unreadable: " + e.Message); equal = false; }
            DecideByHistory(it, equal);
            if (it.Direction != Dir.None) PreviewPlaylist(it, it.Direction);
        }

        string ConvertPlaylist(SyncItem it, Dir d, List<string> notes)
        {
            bool toPs5 = d == Dir.ToPs5;
            string src = Util.Utf8.GetString(toPs5 ? PcRead(it) : PsRead(it));
            Func<string, bool> exists = null;
            if (S.PlaylistDropMissingContent) exists = toPs5 ? (Func<string, bool>)(p => c.Ps5ContentExists(p)) : (p => File.Exists(p));
            string target = null;
            if (toPs5 ? it.Ps5Exists : it.PcExists) target = Util.Utf8.GetString(toPs5 ? PsRead(it) : PcRead(it));
            return PlaylistConverter.Convert(src, toPs5, c.Tr, exists, notes, target);
        }

        void PreviewPlaylist(SyncItem it, Dir d)
        {
            try
            {
                var notes = new List<string>();
                string conv = ConvertPlaylist(it, d, notes);
                var root = MiniJson.Parse(conv) as JObj;
                var items = root == null ? null : MiniJson.Get(root, "items") as List<object>;
                it.Details.Add("Conversion " + DirText.Of(d) + ": " + (items == null ? 0 : items.Count) + " voci");
                foreach (var n in notes) it.Details.Add("  " + n);
                if (items != null)
                    foreach (var o in items.OfType<JObj>().Take(6))
                        it.Details.Add("  path: " + MiniJson.Get(o, "path") + "   core: " + MiniJson.Get(o, "core_path"));
            }
            catch (Exception e) { it.Details.Add("Conversion failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- saves
        string ResolveDir(KvFile cfg, string key, string fallback, bool pc, SyncPlan plan, string label)
        {
            string v;
            if (cfg.Values.TryGetValue(key, out v) && !string.IsNullOrEmpty(v) && v != "default")
            {
                if (v.StartsWith(":\\") || v.StartsWith(":/")) return v.Substring(2).Replace('\\', '/').Trim('/');
                if (pc && v.StartsWith(S.PcRetroArchRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return v.Substring(S.PcRetroArchRoot.TrimEnd('\\').Length + 1).Replace('\\', '/');
                string app = S.Ps5AppRoot.TrimEnd('/') + "/";
                if (!pc && v.StartsWith(app)) return v.Substring(app.Length).Trim('/');
                plan.Notes.Add(label + ": " + key + " = \"" + v + "\" is not inside the RetroArch folder, using \"" + fallback + "\".");
            }
            return fallback;
        }

        static bool CfgTrue(KvFile cfg, string key) { string v; return cfg.Values.TryGetValue(key, out v) && v == "true"; }

        bool PerCore(string setting, KvFile cfg, string sortKey)
        {
            if (string.Equals(setting, "core", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(setting, "flat", StringComparison.OrdinalIgnoreCase)) return false;
            return CfgTrue(cfg, sortKey);
        }

        CoreIndex BuildCoreIndex(Dictionary<string, RemoteEntry> psSaves, Dictionary<string, RemoteEntry> psStates, SyncPlan plan)
        {
            var idx = new CoreIndex();
            foreach (var kv in S.GameCoreOverrides) idx.AddGame(kv.Key, kv.Value, "GameCoreOverrides");
            foreach (var kv in S.SubfolderCoreMap) idx.AddSub(kv.Key, kv.Value);
            // existing PS5 per-core trees
            foreach (var tree in new[] { psSaves, psStates })
            {
                if (tree == null) continue;
                foreach (var rel in tree.Keys)
                {
                    var parts = rel.Split('/');
                    if (parts.Length < 2) continue;
                    if (parts.Length == 2) idx.AddGame(CoreIndex.GameBase(parts[1]), parts[0], "save already on the PS5 in " + parts[0]);
                    else
                    {
                        idx.AddSub(parts[1], parts[0]);
                        // cores that keep their own sub tree (e.g. Opera/3DO: Opera/opera/per_game/<game>.0.srm):
                        // the game itself belongs to that core too, so its flat savestates map to the same folder
                        string g = CoreIndex.GameBase(parts[parts.Length - 1]);
                        if (!CoreIndex.IsSharedName(g)) idx.AddGame(g, parts[0], "save already on the PS5 in " + parts[0] + "/" + parts[1]);
                    }
                }
            }
            // runtime logs: playlists/logs/<core>/<game>.lrtl (both sides)
            try
            {
                var pcLogs = RaContext.LocalWalk(c.PcAbs(Util.JoinRel(S.PlaylistDir, "logs")));
                if (pcLogs != null) foreach (var rel in pcLogs.Keys) AddCoreRel(idx, rel, "log di gioco");
                var psLogs = c.RemoteWalk(Util.JoinRel(S.PlaylistDir, "logs"));
                if (psLogs != null) foreach (var rel in psLogs.Keys) AddCoreRel(idx, rel, "log di gioco");
            }
            catch (Exception e) { plan.Notes.Add("Game logs unreadable: " + e.Message); }
            // per-game config files: config/<core>/<game>.opt|cfg|slangp and remaps
            try
            {
                var pcCfgTree = RaContext.LocalWalk(c.PcAbs(S.PcConfigDir));
                if (pcCfgTree != null) foreach (var rel in pcCfgTree.Keys) AddCoreRel(idx, rel.StartsWith("remaps/", StringComparison.OrdinalIgnoreCase) ? rel.Substring(7) : rel, "config per-gioco");
                var psCfgTree = c.RemoteWalk(S.Ps5ConfigDir);
                if (psCfgTree != null) foreach (var rel in psCfgTree.Keys) AddCoreRel(idx, rel.StartsWith("remaps/", StringComparison.OrdinalIgnoreCase) ? rel.Substring(7) : rel, "config per-gioco");
            }
            catch (Exception e) { plan.Notes.Add("Per-game configs unreadable: " + e.Message); }
            // playlists: label / file name -> core_name "(Core)"
            try
            {
                var pcPl = RaContext.LocalWalk(c.PcAbs(S.PlaylistDir));
                if (pcPl != null) foreach (var kv in pcPl.Where(x => x.Key.EndsWith(".lpl", StringComparison.OrdinalIgnoreCase))) AddPlaylist(idx, File.ReadAllBytes(kv.Value.FullName));
                var psPl = c.RemoteWalk(S.PlaylistDir);
                if (psPl != null) foreach (var kv in psPl.Where(x => x.Key.EndsWith(".lpl", StringComparison.OrdinalIgnoreCase) && !x.Key.StartsWith("logs/")))
                        AddPlaylist(idx, c.Ftp.Download(c.Ps5Abs(Util.JoinRel(S.PlaylistDir, kv.Key))));
            }
            catch (Exception e) { plan.Notes.Add("Playlists unreadable for core index: " + e.Message); }
            return idx;
        }

        static void AddCoreRel(CoreIndex idx, string rel, string how)
        {
            var parts = rel.Split('/');
            if (parts.Length != 2) return;
            string game = CoreIndex.GameBase(parts[1]);
            if (string.Equals(game, parts[0], StringComparison.OrdinalIgnoreCase)) return;   // core-wide files (e.g. Snes9x.opt)
            idx.AddGame(game, parts[0], how + " (" + parts[0] + ")");
        }

        static void AddPlaylist(CoreIndex idx, byte[] data)
        {
            JObj root;
            try { root = MiniJson.Parse(Util.StripBom(Util.Utf8.GetString(data))) as JObj; } catch { return; }
            if (root == null) return;
            var items = MiniJson.Get(root, "items") as List<object>;
            if (items == null) return;
            foreach (var it in items.OfType<JObj>())
            {
                string core = CoreIndex.CoreFromName(MiniJson.Get(it, "core_name") as string);
                if (core == null) core = CoreIndex.CoreFromName(MiniJson.Get(root, "default_core_name") as string);
                if (core == null) continue;
                string label = MiniJson.Get(it, "label") as string;
                string path = MiniJson.Get(it, "path") as string;
                if (!string.IsNullOrEmpty(path))
                {
                    string p = path; int h = p.IndexOf('#'); if (h > 0) p = p.Substring(0, h);
                    string fn = p.Replace('\\', '/'); fn = fn.Substring(fn.LastIndexOf('/') + 1);
                    int dot = fn.LastIndexOf('.'); if (dot > 0) fn = fn.Substring(0, dot);
                    idx.AddGame(fn, core, "playlist (" + core + ")");
                }
                if (!string.IsNullOrEmpty(label)) idx.AddGame(label, core, "playlist (" + core + ")");
            }
        }

        void BuildSaves(SyncPlan plan)
        {
            log("Analyzing saves and states\u2026");
            var pcCfg = c.PcCfg; var psCfg = c.Ps5Cfg;
            var cats = new[]
            {
                new { Cat = "Saves", PcDef = S.PcSavesDir, PsDef = S.Ps5SavesDir, DirKey = "savefile_directory", Sort = "sort_savefiles_enable", ByContent = "sort_savefiles_by_content_enable", InContent = "savefiles_in_content_dir" },
                new { Cat = "Stati", PcDef = S.PcStatesDir, PsDef = S.Ps5StatesDir, DirKey = "savestate_directory", Sort = "sort_savestates_enable", ByContent = "sort_savestates_by_content_enable", InContent = "savestates_in_content_dir" }
            };
            var resolved = new List<Tuple<string, string, string, bool, bool, Dictionary<string, FileInfo>, Dictionary<string, RemoteEntry>>>();
            foreach (var k in cats)
            {
                string pcDir = ResolveDir(pcCfg, k.DirKey, k.PcDef, true, plan, "PC");
                string psDir = ResolveDir(psCfg, k.DirKey, k.PsDef, false, plan, "PS5");
                bool pcCore = PerCore(S.PcSaveLayout, pcCfg, k.Sort), psCore = PerCore(S.Ps5SaveLayout, psCfg, k.Sort);
                if (CfgTrue(pcCfg, k.ByContent) || CfgTrue(psCfg, k.ByContent) || CfgTrue(pcCfg, k.InContent) || CfgTrue(psCfg, k.InContent))
                {
                    plan.Notes.Add(k.Cat + ": sorting by content folder / saves next to games is not supported: category skipped.");
                    continue;
                }
                var pcFiles = RaContext.LocalWalk(c.PcAbs(pcDir)) ?? new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                var psFiles = c.RemoteWalk(psDir);
                if (psFiles == null) { plan.Notes.Add(k.Cat + ": PS5 folder " + psDir + " missing."); psFiles = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase); }
                plan.Notes.Add(k.Cat + ": PC \\" + pcDir.Replace('/', '\\') + " (" + (pcCore ? "one folder per core" : "flat") + ", " + pcFiles.Count + " file)  \u21C4  PS5 /" + psDir + " (" + (psCore ? "one folder per core" : "flat") + ", " + psFiles.Count + " file)");
                resolved.Add(Tuple.Create(k.Cat, pcDir, psDir, pcCore, psCore, pcFiles, psFiles));
            }
            CoreIndex idx = null;
            if (resolved.Any(r => r.Item4 != r.Item5))
            {
                var sv = resolved.FirstOrDefault(r => r.Item1 == "Saves");
                var st = resolved.FirstOrDefault(r => r.Item1 == "Stati");
                idx = BuildCoreIndex(sv == null ? null : sv.Item7, st == null ? null : st.Item7, plan);
                foreach (var r in resolved) if (!r.Item4) idx.AddSubGames(r.Item6.Keys);   // PC flat trees with core sub folders (opera/...)
                plan.Notes.Add("Indice giochi \u2192 core: " + idx.Count + " voci (da save PS5, log, config per-gioco, playlist).");
            }
            foreach (var r in resolved) BuildSaveCategory(plan, r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6, r.Item7, idx);
        }

        void BuildSaveCategory(SyncPlan plan, string cat, string pcDir, string psDir, bool pcCore, bool psCore,
            Dictionary<string, FileInfo> pcFiles, Dictionary<string, RemoteEntry> psFiles, CoreIndex idx)
        {
            bool same = pcCore == psCore;
            Func<string, bool, string> canon = (rel, core) =>
            {
                if (same || !core) return rel;
                int i = rel.IndexOf('/');
                return i < 0 ? rel : rel.Substring(i + 1);
            };
            var pcMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in pcFiles.Keys)
            {
                if (Glob.AnyPath(S.SaveExcludes, rel)) continue;
                pcMap[canon(rel, pcCore)] = rel;
            }
            var psMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in psFiles.Keys)
            {
                if (Glob.AnyPath(S.SaveExcludes, rel)) continue;
                string k = canon(rel, psCore);
                List<string> l;
                if (!psMap.TryGetValue(k, out l)) psMap[k] = l = new List<string>();
                l.Add(rel);
            }
            foreach (var key in pcMap.Keys.Union(psMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string pcRel; pcMap.TryGetValue(key, out pcRel);
                List<string> psl; psMap.TryGetValue(key, out psl);
                string psRel = null;
                if (psl != null)
                {
                    psRel = psl.OrderByDescending(x => psFiles[x].ModifiedUtc ?? DateTime.MinValue).First();
                    foreach (var other in psl.Where(x => x != psRel))
                    {
                        var dup = NewItem(cat, "dup|" + other, ItemKind.Raw, null, null, Util.JoinRel(psDir, other), psFiles[other]);
                        dup.Info = true; dup.Options = new List<Dir> { Dir.None };
                        dup.Reason = "same name as " + psRel + " (newer) in another core: ignored";
                        plan.Items.Add(dup);
                    }
                }
                string how = null;
                string targetPs = psRel, targetPc = pcRel;
                if (targetPs == null)
                {
                    if (same || !psCore) targetPs = key;
                    else
                    {
                        string core = idx == null ? null : idx.Resolve(key, out how);
                        if (core != null) targetPs = core + "/" + key;
                    }
                }
                if (targetPc == null)
                {
                    if (same || !pcCore) targetPc = key;
                    else
                    {
                        string core = idx == null ? null : idx.Resolve(key, out how);
                        if (core != null) targetPc = core + "/" + key;
                    }
                }
                FileInfo fi = pcRel != null ? pcFiles[pcRel] : null;
                RemoteEntry re = psRel != null ? psFiles[psRel] : null;
                var it = NewItem(cat, key, ItemKind.Raw,
                    targetPc == null ? null : Util.JoinRel(pcDir, targetPc), fi,
                    targetPs == null ? null : Util.JoinRel(psDir, targetPs), re);
                try { EvaluateRaw(it); }
                catch (Exception e) { it.Info = true; it.Direction = Dir.None; it.Options = new List<Dir> { Dir.None }; it.Reason = "error: " + e.Message; }
                if (how != null && !it.Identical) it.Details.Add("Core folder chosen from: " + how);
                it.ExcludeList = "SaveExcludes"; it.ExcludePattern = key;
                plan.Items.Add(it);
            }
        }

        // ---------------------------------------------------------------- configuration
        void BuildConfig(SyncPlan plan)
        {
            log("Analyzing config\u2026");
            var mf = S.MainConfigFilter;
            var mainFilter = new KeyFilter(new KeyFilterSettings
            {
                PlatformKeys = mf.PlatformKeys.Concat(S.MainConfigOnlyPlatformKeys).ToList(), AlwaysShareKeys = mf.AlwaysShareKeys,
                SkipPathLikeValues = mf.SkipPathLikeValues, TreatInputTextBindsAsPlatform = mf.TreatInputTextBindsAsPlatform, AddMissingKeys = mf.AddMissingKeys
            });
            var overrideFilter = new KeyFilter(mf);
            var optFilter = new KeyFilter(new KeyFilterSettings
            {
                PlatformKeys = S.CoreOptionPlatformKeys, AlwaysShareKeys = new List<string>(),
                SkipPathLikeValues = true, TreatInputTextBindsAsPlatform = false, AddMissingKeys = false
            });

            // main retroarch.cfg (different location on each side)
            {
                string pcRel = S.PcMainConfig.Replace('\\', '/');
                var fi = new FileInfo(c.PcAbs(pcRel));
                RemoteEntry re = c.Stat(c.Ps5Abs(S.Ps5MainConfig));
                var it = NewItem("retroarch.cfg", "retroarch.cfg", ItemKind.KeyValue, pcRel, fi, S.Ps5MainConfig, re);
                it.Filter = mainFilter;
                Eval(it);
                plan.Items.Add(it);
            }

            // config\ (per-core / per-game overrides, core options, shader presets, remaps)
            string ps5MainInCfg = RelInside(S.Ps5MainConfig, S.Ps5ConfigDir);
            string pcMainInCfg = RelInside(S.PcMainConfig, S.PcConfigDir);
            Func<string, bool> cfgWanted = rel =>
            {
                string ext = Path.GetExtension(rel);
                if (!S.ConfigExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) return false;
                if (Glob.AnyPath(S.ConfigExcludes, rel)) return false;
                if (ps5MainInCfg != null && string.Equals(rel, ps5MainInCfg, StringComparison.OrdinalIgnoreCase)) return false;
                if (pcMainInCfg != null && string.Equals(rel, pcMainInCfg, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            };
            PairTrees(plan, "Config", S.PcConfigDir, S.Ps5ConfigDir, cfgWanted, rel =>
            {
                string ext = Path.GetExtension(rel).ToLowerInvariant();
                if (S.MergeKeyValueFiles && ext == ".cfg") return overrideFilter;
                if (S.MergeKeyValueFiles && ext == ".opt") return optFilter;
                return null;
            }, ItemKind.Raw);

            // favourite shader folders
            foreach (var d in S.ShaderDirs)
                PairTrees(plan, "Shader", d, d, rel => !Glob.AnyPath(S.ShaderExcludes, rel), rel => null, ItemKind.Raw);

            // playlists with path conversion
            PairTrees(plan, "Playlist", S.PlaylistDir, S.PlaylistDir,
                rel => rel.EndsWith(".lpl", StringComparison.OrdinalIgnoreCase) && !Glob.AnyPath(S.PlaylistExcludes, rel), rel => null, ItemKind.Playlist);
        }

        static string RelInside(string rel, string dir)
        {
            rel = rel.Replace('\\', '/'); dir = dir.Replace('\\', '/').Trim('/') + "/";
            return rel.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? rel.Substring(dir.Length) : null;
        }

        void PairTrees(SyncPlan plan, string cat, string pcDir, string psDir, Func<string, bool> wanted, Func<string, KeyFilter> kvFilter, ItemKind kind)
        {
            var pc = RaContext.LocalWalk(c.PcAbs(pcDir)) ?? new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var ps = c.RemoteWalk(psDir);
            if (ps == null) { plan.Notes.Add(cat + ": PS5 folder /" + psDir + " missing (will be created if needed)."); ps = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase); }
            var keys = pc.Keys.Where(wanted).Union(ps.Keys.Where(wanted), StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            plan.Notes.Add(cat + ": " + pcDir.Replace('/', '\\') + " " + pc.Keys.Count(wanted) + " files on PC, " + ps.Keys.Count(wanted) + " on PS5.");
            foreach (var k in keys)
            {
                FileInfo fi; pc.TryGetValue(k, out fi);
                RemoteEntry re; ps.TryGetValue(k, out re);
                string pcName = fi != null ? pc.Keys.First(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)) : null;
                string psName = re != null ? ps.Keys.First(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)) : null;
                var f = kvFilter(k);
                var it = NewItem(cat, Util.JoinRel(pcDir, k), f != null ? ItemKind.KeyValue : kind,
                    Util.JoinRel(pcDir, pcName ?? psName), fi, Util.JoinRel(psDir, psName ?? pcName), re);
                it.Filter = f;
                it.ExcludeList = cat == "Config" ? "ConfigExcludes" : cat == "Shader" ? "ShaderExcludes" : "PlaylistExcludes";
                it.ExcludePattern = k;
                Eval(it);
                plan.Items.Add(it);
            }
        }

        void Eval(SyncItem it)
        {
            try
            {
                if (it.Kind == ItemKind.KeyValue) EvaluateKv(it);
                else if (it.Kind == ItemKind.Playlist) EvaluatePlaylist(it);
                else EvaluateRaw(it);
            }
            catch (Exception e)
            {
                it.Info = true; it.Direction = Dir.None; it.Options = new List<Dir> { Dir.None };
                it.Reason = "error: " + e.Message;
            }
        }

        // ------------------------------------------------------------------ apply
        public void Apply(SyncPlan plan, Action<int> progress)
        {
            if (dryRun) throw new InvalidOperationException("Preview mode: no changes applied.");
            if (progress == null) progress = x => { };
            var bk = new BackupManager(S.BackupRoot, S.BackupKeep, log);
            var todo = plan.Items.Where(i => i.Actionable).ToList();
            int done = 0, failed = 0;
            foreach (var it in todo)
            {
                try
                {
                    log(DirText.Of(it.Direction) + "  [" + it.Category + "] " + it.Display);
                    ApplyItem(it, bk);
                }
                catch (Exception e)
                {
                    failed++;
                    log("ERROR on " + it.Display + ": " + e.Message);
                }
                done++;
                progress(done * 100 / Math.Max(1, todo.Count));
            }
            // remember identical items so the next run can tell which side changed
            foreach (var it in plan.Items.Where(i => i.Identical && i.PcExists && i.Ps5Exists && i.PcSha != null && i.Ps5Sha != null))
                Record(it, it.PcSha, it.Ps5Sha, it.PcSize, it.Ps5Entry, it.MergePreview != null ? it.MergePreview.NewBasePc : null, it.MergePreview != null ? it.MergePreview.NewBasePs5 : null);
            state.Save();
            if (bk.Count > 0) { log("Backup: " + bk.Count + " files in " + bk.SessionDir); bk.Prune(); }
            log("Applied " + (todo.Count - failed) + " items" + (failed > 0 ? ", " + failed + " with errors" : "") + ".");
            if (failed > 0) throw new Exception(failed + " items were not synced: see the log.");
        }

        void Record(SyncItem it, string pcSha, string psSha, long pcSize, RemoteEntry psEntry, Dictionary<string, string> baseVals)
        {
            Record(it, pcSha, psSha, pcSize, psEntry, baseVals, baseVals);
        }

        void Record(SyncItem it, string pcSha, string psSha, long pcSize, RemoteEntry psEntry, Dictionary<string, string> basePc, Dictionary<string, string> basePs5)
        {
            var s = new ItemState
            {
                LocalSha = pcSha, RemoteSha = psSha, LocalSize = pcSize,
                LocalMtime = File.Exists(it.PcFull) ? File.GetLastWriteTimeUtc(it.PcFull).Ticks : 0,
                RemoteSize = psEntry != null ? psEntry.Size : 0,
                RemoteMtime = psEntry != null && psEntry.ModifiedUtc.HasValue ? psEntry.ModifiedUtc.Value.Ticks : 0,
                Synced = DateTime.UtcNow.Ticks, Base = basePc, BasePs5 = basePs5
            };
            state.Set(it.Key, s);
        }

        RemoteEntry WriteRemote(SyncItem it, byte[] data, BackupManager bk)
        {
            if (it.Ps5Exists) bk.SaveBytes("PS5", it.Ps5Rel, PsRead(it));
            string dir = RaContext.Parent(it.Ps5Path);
            c.Ftp.MakeDirs(dir);
            c.Ftp.Upload(it.Ps5Path, data);
            c.Invalidate(dir);
            var e = c.Stat(it.Ps5Path);
            if (e == null || e.Size != data.Length) throw new Exception("upload to PS5 not verified (size mismatch)");
            return e;
        }

        void WriteLocal(SyncItem it, byte[] data, DateTime? mtimeUtc, BackupManager bk)
        {
            if (File.Exists(it.PcFull)) bk.SaveLocal("PC", it.PcRel, it.PcFull);
            LocalFs.Write(it.PcFull, data, mtimeUtc);
        }

        void ApplyItem(SyncItem it, BackupManager bk)
        {
            if (it.Kind == ItemKind.Raw)
            {
                if (it.Direction == Dir.ToPs5)
                {
                    var data = File.ReadAllBytes(it.PcFull);
                    var e = WriteRemote(it, data, bk);
                    string sha = Util.Sha(data);
                    Record(it, sha, sha, data.Length, e, null);
                }
                else if (it.Direction == Dir.ToPc)
                {
                    var data = PsRead(it);
                    WriteLocal(it, data, it.Ps5Mtime, bk);
                    string sha = Util.Sha(data);
                    Record(it, sha, sha, data.Length, it.Ps5Entry, null);
                }
                return;
            }
            if (it.Kind == ItemKind.Playlist)
            {
                var notes = new List<string>();
                if (it.Direction == Dir.ToPs5)
                {
                    var data = Util.Utf8.GetBytes(ConvertPlaylist(it, Dir.ToPs5, notes));
                    var e = WriteRemote(it, data, bk);
                    var pcB = File.ReadAllBytes(it.PcFull);
                    Record(it, Util.Sha(pcB), Util.Sha(data), pcB.Length, e, null);
                }
                else if (it.Direction == Dir.ToPc)
                {
                    var data = Util.Utf8.GetBytes(ConvertPlaylist(it, Dir.ToPc, notes));
                    WriteLocal(it, data, null, bk);
                    Record(it, Util.Sha(data), Util.Sha(PsRead(it)), data.Length, it.Ps5Entry, null);
                }
                foreach (var n in notes) log("   " + n);
                return;
            }
            // key/value merge
            if (!it.PcExists || !it.Ps5Exists)
            {
                int kept, dropped; Dictionary<string, string> b;
                var src = it.PcExists ? File.ReadAllBytes(it.PcFull) : PsRead(it);
                var copy = KvMerger.FilteredCopy(src, it.Filter, out kept, out dropped, out b);
                if (copy == null) return;
                if (it.Direction == Dir.ToPs5)
                {
                    var e = WriteRemote(it, copy, bk);
                    Record(it, Util.Sha(src), Util.Sha(copy), src.Length, e, b);
                }
                else
                {
                    WriteLocal(it, copy, null, bk);
                    Record(it, Util.Sha(copy), Util.Sha(src), copy.Length, it.Ps5Entry, b);
                }
                return;
            }
            var pcData = File.ReadAllBytes(it.PcFull);
            var psData = PsRead(it);
            Dir nd = Newer(it);
            bool pcNewer = nd == Dir.ToPs5 || (nd == Dir.None && it.PcMtime >= it.Ps5Mtime);
            var r = KvMerger.Merge(pcData, psData, PrevBase(it, true), PrevBase(it, false), it.Filter, pcNewer, it.Direction);
            RemoteEntry pe = it.Ps5Entry;
            if (r.NewPs5 != null) { pe = WriteRemote(it, r.NewPs5, bk); log("   " + r.ToPs5.Count + " keys updated on the PS5"); }
            if (r.NewPc != null) { WriteLocal(it, r.NewPc, null, bk); log("   " + r.ToPc.Count + " keys updated on the PC"); }
            var finalPc = r.NewPc ?? pcData; var finalPs = r.NewPs5 ?? psData;
            Record(it, Util.Sha(finalPc), Util.Sha(finalPs), finalPc.Length, pe, r.NewBasePc, r.NewBasePs5);
        }

        // "Mark current state as aligned": records the current SHA (and, for .cfg/.opt, every shared key value
        // of each side) of all pairs present on both sides, so the next run only proposes changes made after now.
        // Files present on one side only are NOT recorded: they keep being proposed (config ones unticked).
        // Writes only RetroSync.state.json; allowed in preview mode too.
        public int Baseline(SyncPlan plan)
        {
            int n = 0;
            foreach (var it in plan.Items)
            {
                if (!it.PcExists || !it.Ps5Exists || it.PcFull == null || it.Ps5Path == null) continue;
                if (it.Info && it.Reason.StartsWith("errore")) continue;
                try
                {
                    var pcB = PcRead(it);
                    string pcSha = Util.Sha(pcB);
                    string psSha = it.Ps5Sha ?? Util.Sha(PsRead(it));
                    Dictionary<string, string> bPc = null, bPs = null;
                    if (it.Kind == ItemKind.KeyValue)
                    {
                        var pc = KvFile.Parse(pcB); var ps = KvFile.Parse(PsRead(it));
                        bPc = new Dictionary<string, string>(); bPs = new Dictionary<string, string>();
                        foreach (var kv in pc.Values)
                        {
                            string b;
                            if (!ps.Values.TryGetValue(kv.Key, out b) || !it.Filter.Shared(kv.Key, kv.Value, b)) continue;
                            bPc[kv.Key] = kv.Value; bPs[kv.Key] = b;
                        }
                    }
                    Record(it, pcSha, psSha, pcB.Length, it.Ps5Entry, bPc, bPs);
                    n++;
                }
                catch (Exception e) { log("Baseline not recorded for " + it.Display + ": " + e.Message); }
            }
            state.Save();
            return n;
        }

        // "Always ignore": adds the item to the matching exclude list in the settings file.
        public string IgnoreForever(SyncItem it)
        {
            if (it.ExcludeList == null || string.IsNullOrEmpty(it.ExcludePattern)) return null;
            List<string> l = it.ExcludeList == "ConfigExcludes" ? S.ConfigExcludes : it.ExcludeList == "ShaderExcludes" ? S.ShaderExcludes
                : it.ExcludeList == "PlaylistExcludes" ? S.PlaylistExcludes : S.SaveExcludes;
            string pat = it.ExcludePattern;
            if (!l.Contains(pat)) l.Add(pat);
            // saves: the PS5 side has an extra "<core>/" level, so also exclude "*/<path>"
            if (it.ExcludeList == "SaveExcludes" && pat.Contains("/") && !l.Contains("*/" + pat)) l.Add("*/" + pat);
            SettingsStore.Save(S);
            return it.ExcludeList + " += \"" + pat + "\"";
        }

        // Recomputes the preview details after the user changed the direction of a key/value item.
        public void RefreshKvPreview(SyncItem it)
        {
            if (it.Kind != ItemKind.KeyValue || !it.PcExists || !it.Ps5Exists || it.Direction == Dir.None) return;
            Dir n = Newer(it);
            bool pcNewer = n == Dir.ToPs5 || (n == Dir.None && it.PcMtime >= it.Ps5Mtime);
            var r = KvMerger.Merge(PcRead(it), PsRead(it), PrevBase(it, true), PrevBase(it, false), it.Filter, pcNewer, it.Direction);
            it.Details.Clear();
            it.Details.Add("Shared keys in common: " + r.SharedCommon + ", platform/path keys ignored: " + r.Skipped);
            foreach (var x in r.ToPs5) it.Details.Add("\u2192 PS5  " + x);
            foreach (var x in r.ToPc) it.Details.Add("\u2192 PC   " + x);
            foreach (var x in r.Conflicts) it.Details.Add("CONFLITTO  " + x);
        }
    }

    // Text rendering of a plan (console / log / dry-run report).
    static class PlanPrinter
    {
        public static string Format(SyncPlan p, bool withDetails)
        {
            var sb = new StringBuilder();
            foreach (var n in p.Notes) sb.AppendLine("  * " + n);
            var vis = p.Visible.ToList();
            int ident = p.Items.Count(i => i.Identical);
            sb.AppendLine();
            sb.AppendLine("  Items: " + p.Items.Count + " total, " + ident + " already aligned, " + vis.Count + " to review.");
            sb.AppendLine("  To the PS5: " + p.Items.Count(i => i.Actionable && (i.Direction == Dir.ToPs5 || i.Direction == Dir.Merge))
                + "   To the PC: " + p.Items.Count(i => i.Actionable && (i.Direction == Dir.ToPc || i.Direction == Dir.Merge))
                + "   Conflicts: " + p.Items.Count(i => i.Conflict) + "   Unchecked: " + vis.Count(i => i.Direction != Dir.None && !i.Selected) + "   Skipped/info: " + vis.Count(i => i.Direction == Dir.None));
            foreach (var g in vis.OrderByDescending(i => i.Conflict).ThenBy(i => i.Category).ThenBy(i => i.Display).GroupBy(i => i.Category))
            {
                sb.AppendLine();
                sb.AppendLine("  [" + g.Key + "]");
                foreach (var it in g)
                {
                    sb.AppendLine("    " + (it.Conflict ? "! " : "  ") + (it.Direction != Dir.None && !it.Selected ? "[ ] " : it.Direction != Dir.None ? "[x] " : "    ") + DirText.Of(it.Direction).PadRight(22) + it.Display);
                    sb.AppendLine("        " + it.Reason + "   (PC " + (it.PcExists ? Util.FmtUtc(it.PcMtime) + " " + Util.Size(it.PcSize) : "missing")
                        + " | PS5 " + (it.Ps5Exists ? Util.FmtUtc(it.Ps5Mtime) + " " + Util.Size(it.Ps5Size) : "missing") + ")");
                    if (it.Ps5Rel != null && it.PcRel != null && !string.Equals(it.PcRel, it.Ps5Rel, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("        PC \\" + it.PcRel.Replace('/', '\\') + "  \u21C4  PS5 /" + it.Ps5Rel);
                    if (withDetails)
                    {
                        foreach (var d in it.Details.Take(60)) sb.AppendLine("        " + d);
                        if (it.Details.Count > 60) sb.AppendLine("        \u2026 (+" + (it.Details.Count - 60) + " righe)");
                    }
                }
            }
            return sb.ToString();
        }
    }
}
