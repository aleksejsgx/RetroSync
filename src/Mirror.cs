using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RetroSync
{
    public class MirrorResult
    {
        public int RemoteFiles, Copied, New, Changed, Unchanged, Failed, Skipped, DeletedOnPs5, MovedDeleted, KeptPrevious;
        public long RemoteBytes, CopiedBytes, ExcludedFiles, ExcludedBytes;
        public TimeSpan Elapsed;
        public List<string> Errors = new List<string>(), SkippedList = new List<string>(), DeletedList = new List<string>();
        public Dictionary<string, long[]> ExcludedByDir = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase); // dir -> {files, bytes}
    }

    // One-way incremental backup of the whole PS5 RetroArch folder to a local mirror. Never writes to the PS5
    // (the FTP client runs in ReadOnly mode). Change detection: remote size + MLSD UTC mtime vs a manifest kept
    // in the mirror folder. Files removed on the PS5 are reported, never deleted locally (optionally moved to
    // _deleted\<timestamp>). A file that changed on the PS5 keeps its previous copy in _previous\<timestamp>.
    public class Ps5Mirror
    {
        public const string ManifestName = "_retrosync-manifest.tsv";
        readonly AppSettings S;
        readonly Action<string> log;
        readonly Action<string> status;   // single-line progress (may be null)
        public bool MoveDeleted;
        public int Retries = 4;

        class Entry { public long Size; public long MtimeTicks; public string Sha; }

        public Ps5Mirror(AppSettings s, Action<string> log, Action<string> status)
        {
            S = s; this.log = log ?? (x => { }); this.status = status; MoveDeleted = s.MirrorMoveDeleted;
        }

        string ManifestPath { get { return Path.Combine(S.MirrorLocalRoot, ManifestName); } }

        Dictionary<string, Entry> LoadManifest()
        {
            var m = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (!File.Exists(ManifestPath)) return m;
            foreach (var line in File.ReadAllLines(ManifestPath, Encoding.UTF8))
            {
                var p = line.Split('\t');
                if (p.Length < 3 || p[0].StartsWith("#")) continue;
                var e = new Entry();
                long.TryParse(p[1], out e.Size); long.TryParse(p[2], out e.MtimeTicks);
                e.Sha = p.Length > 3 ? p[3] : "";
                m[p[0]] = e;
            }
            return m;
        }

        void SaveManifest(Dictionary<string, Entry> m)
        {
            string tmp = ManifestPath + ".tmp";
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.WriteLine("# RetroSync PS5 mirror manifest: path<TAB>size<TAB>mtime UTC ticks (MLSD)<TAB>sha256");
                foreach (var kv in m.OrderBy(k => k.Key, StringComparer.Ordinal))
                    w.WriteLine(kv.Key + "\t" + kv.Value.Size + "\t" + kv.Value.MtimeTicks + "\t" + kv.Value.Sha);
            }
            if (File.Exists(ManifestPath)) File.Replace(tmp, ManifestPath, null); else File.Move(tmp, ManifestPath);
        }

        bool DirExcluded(string rel)
        {
            foreach (var p in S.MirrorExcludes)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (p.EndsWith("/**") && Glob.ToRegex(p.Substring(0, p.Length - 3)).IsMatch(rel)) return true;
                if (!p.Contains("/") && Glob.MatchPath(p, rel)) return true;
            }
            return false;
        }

        T Retry<T>(FtpClient ftp, string what, Func<T> f)
        {
            for (int i = 1; ; i++)
            {
                try { return f(); }
                catch (Exception e)
                {
                    if (i >= Retries || e is InvalidOperationException) throw;
                    log("  ! " + what + ": " + e.Message + " \u2014 riprovo (" + i + "/" + (Retries - 1) + ")");
                    System.Threading.Thread.Sleep(1000 * i);
                    try { ftp.Connect(); } catch { }
                }
            }
        }

        void WalkRemote(FtpClient ftp, string abs, string rel, Dictionary<string, RemoteEntry> files, MirrorResult res, string excludedTop)
        {
            var list = Retry(ftp, "elenco " + abs, () => ftp.ListDir(abs));
            if (list == null) { res.Errors.Add("folder not readable: " + abs); return; }
            foreach (var e in list)
            {
                string r = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
                string a = abs.TrimEnd('/') + "/" + e.Name;
                if (e.IsDir)
                {
                    string ex = excludedTop;
                    if (ex == null && DirExcluded(r)) { ex = r; res.ExcludedByDir[r] = new long[2]; }
                    if (ex != null && !S.MirrorMeasureExcluded) continue;
                    WalkRemote(ftp, a, r, files, res, ex);
                }
                else if (excludedTop != null || Glob.AnyPath(S.MirrorExcludes, r))
                {
                    string key = excludedTop ?? "(excluded files)";
                    long[] c; if (!res.ExcludedByDir.TryGetValue(key, out c)) res.ExcludedByDir[key] = c = new long[2];
                    c[0]++; c[1] += e.Size; res.ExcludedFiles++; res.ExcludedBytes += e.Size;
                }
                else files[r] = e;
            }
        }

        static string LocalPath(string root, string rel)
        {
            return Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        public MirrorResult Run()
        {
            var res = new MirrorResult();
            var sw = Stopwatch.StartNew();
            string root = S.MirrorLocalRoot, remoteRoot = S.MirrorRemoteRoot.TrimEnd('/');
            Directory.CreateDirectory(root);
            var man = LoadManifest();
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            log("PS5 backup: ftp://" + S.FtpHost + ":" + S.FtpPort + remoteRoot + "  \u2192  " + root);
            log("Excluded: " + string.Join(", ", S.MirrorExcludes) + "   Manifest: " + man.Count + " files already copied");

            using (var ftp = new FtpClient(S.FtpHost, S.FtpPort, S.FtpUser, S.FtpPassword, S.FtpTimeoutSeconds) { ReadOnly = true, ClockOffsetMinutes = S.Ps5ClockOffsetMinutes, Trace = log })
            {
                ftp.Connect();
                log("Listing files on the PS5\u2026");
                var files = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
                WalkRemote(ftp, remoteRoot, "", files, res, null);
                res.RemoteFiles = files.Count; res.RemoteBytes = files.Values.Sum(e => e.Size);
                log("On the PS5: " + res.RemoteFiles + " file, " + Util.Size(res.RemoteBytes) + " (excluded: " + res.ExcludedFiles + " file, " + Util.Size(res.ExcludedBytes) + ")");

                var todo = new List<KeyValuePair<string, RemoteEntry>>();
                foreach (var kv in files.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    Entry m; string lp = LocalPath(root, kv.Key);
                    long mt = kv.Value.ModifiedUtc.HasValue ? kv.Value.ModifiedUtc.Value.Ticks : 0;
                    bool same = man.TryGetValue(kv.Key, out m) && m.Size == kv.Value.Size && m.MtimeTicks == mt;
                    if (same)
                    {
                        FileInfo fi = null;
                        try { fi = new FileInfo(lp); } catch { }
                        if (fi != null && fi.Exists && fi.Length == m.Size) { res.Unchanged++; continue; }
                    }
                    todo.Add(kv);
                }
                long todoBytes = todo.Sum(k => k.Value.Size), doneBytes = 0;
                log("To copy: " + todo.Count + " file, " + Util.Size(todoBytes) + "  (unchanged: " + res.Unchanged + ")");

                long lastSave = sw.ElapsedMilliseconds, lastStatus = 0;
                int idx = 0;
                foreach (var kv in todo)
                {
                    idx++;
                    string rel = kv.Key, lp = LocalPath(root, rel), part = lp + ".retrosync.part";
                    var re = kv.Value;
                    long baseDone = doneBytes;
                    Action<long> onBytes = n =>
                    {
                        if (status == null) return;
                        long now = sw.ElapsedMilliseconds;
                        if (now - lastStatus < 1000) return;
                        lastStatus = now;
                        long d = baseDone + n;
                        double rate = d / Math.Max(1.0, now / 1000.0);
                        status(idx + "/" + todo.Count + " file  " + Util.Size(d) + " / " + Util.Size(todoBytes) + "  " + Util.Size((long)rate) + "/s  " + (todoBytes == 0 ? 100 : (int)(d * 100 / todoBytes)) + "%");
                    };
                    try
                    {
                        string dir = Path.GetDirectoryName(lp);
                        Directory.CreateDirectory(dir);
                        string sha = null;
                        Retry(ftp, rel, () =>
                        {
                            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                            if (have > re.Size) { File.Delete(part); have = 0; }
                            using (var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                            {
                                try { ftp.DownloadTo(remoteRoot + "/" + rel, fs, have, onBytes); }
                                catch (FtpException fe)
                                {
                                    if (!(have > 0 && fe.Message.StartsWith("REST"))) throw;
                                    fs.SetLength(0); ftp.DownloadTo(remoteRoot + "/" + rel, fs, 0, onBytes);   // no resume: restart
                                }
                            }
                            long got = new FileInfo(part).Length;
                            if (got != re.Size) throw new IOException("received size " + got + " instead of " + re.Size);
                            return true;
                        });
                        sha = FileSha(part);
                        bool existed = File.Exists(lp);
                        if (existed)
                        {
                            if (S.MirrorKeepPrevious)
                            {
                                string prev = LocalPath(Path.Combine(root, "_previous", stamp), rel);
                                Directory.CreateDirectory(Path.GetDirectoryName(prev));
                                File.Move(lp, prev);
                                res.KeptPrevious++;
                            }
                            else File.Delete(lp);
                        }
                        File.Move(part, lp);
                        if (re.ModifiedUtc.HasValue) File.SetLastWriteTimeUtc(lp, re.ModifiedUtc.Value);
                        man[rel] = new Entry { Size = re.Size, MtimeTicks = re.ModifiedUtc.HasValue ? re.ModifiedUtc.Value.Ticks : 0, Sha = sha };
                        res.Copied++; res.CopiedBytes += re.Size;
                        if (existed) res.Changed++; else res.New++;
                    }
                    catch (PathTooLongException) { res.Skipped++; res.SkippedList.Add(rel + " (path too long for Windows)"); }
                    catch (ArgumentException) { res.Skipped++; res.SkippedList.Add(rel + " (invalid name on Windows)"); }
                    catch (NotSupportedException) { res.Skipped++; res.SkippedList.Add(rel + " (invalid name on Windows)"); }
                    catch (Exception e)
                    {
                        res.Failed++; res.Errors.Add(rel + ": " + e.Message);
                        log("  ERROR " + rel + ": " + e.Message);
                        if (e is InvalidOperationException) throw;
                    }
                    doneBytes += re.Size;
                    if (sw.ElapsedMilliseconds - lastSave > 15000) { SaveManifest(man); lastSave = sw.ElapsedMilliseconds; }   // resume point
                }
                SaveManifest(man);

                // files gone from the PS5: report (and optionally move aside), never delete
                foreach (var rel in man.Keys.ToList())
                {
                    if (files.ContainsKey(rel)) continue;
                    if (Glob.AnyPath(S.MirrorExcludes, rel)) continue;
                    res.DeletedOnPs5++; res.DeletedList.Add(rel);
                    if (!MoveDeleted) continue;
                    string lp = LocalPath(root, rel);
                    if (File.Exists(lp))
                    {
                        string dst = LocalPath(Path.Combine(root, "_deleted", stamp), rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Move(lp, dst);
                        res.MovedDeleted++;
                    }
                    man.Remove(rel);
                }
                if (MoveDeleted && res.MovedDeleted > 0) SaveManifest(man);
            }
            res.Elapsed = sw.Elapsed;
            WriteReport(res);
            return res;
        }

        public static string FileSha(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        void WriteReport(MirrorResult r)
        {
            log("");
            log("=== PS5 backup completato in " + r.Elapsed.ToString(@"hh\:mm\:ss") + " ===");
            log("Files on the PS5 (excluding exclusions): " + r.RemoteFiles + "  (" + Util.Size(r.RemoteBytes) + ")");
            log("Copied: " + r.Copied + " (" + Util.Size(r.CopiedBytes) + ")  new " + r.New + ", changed " + r.Changed + (r.KeptPrevious > 0 ? " (previous version in _previous)" : "") + "   Unchanged: " + r.Unchanged);
            if (r.Elapsed.TotalSeconds > 0 && r.CopiedBytes > 0) log("Average speed: " + Util.Size((long)(r.CopiedBytes / r.Elapsed.TotalSeconds)) + "/s");
            foreach (var kv in r.ExcludedByDir) log("Excluded " + kv.Key + ": " + kv.Value[0] + " file, " + Util.Size(kv.Value[1]) + (S.MirrorMeasureExcluded ? "" : " (not measured)"));
            if (r.Failed > 0) { log("ERRORI: " + r.Failed); foreach (var e in r.Errors) log("  " + e); }
            if (r.Skipped > 0) { log("Skipped: " + r.Skipped); foreach (var e in r.SkippedList) log("  " + e); }
            if (r.DeletedOnPs5 > 0)
            {
                log("No longer on the PS5 (" + r.DeletedOnPs5 + ")" + (MoveDeleted ? ", moved to _deleted" : ", left in the mirror") + ":");
                foreach (var d in r.DeletedList.Take(200)) log("  " + d);
            }
        }
    }
}
