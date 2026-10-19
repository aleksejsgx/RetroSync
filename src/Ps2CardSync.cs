using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

// PS2 memory card sync, taken from PS2CardSync.cs (Project Synch) with its logic unchanged.
// Changes for RetroSync: settings come from RetroSync.settings.json (section "Ps2"), the analysis
// moved into Ps2Engine (no UI) so the --dry-run CLI can use it, and the window has a read-only mode.
namespace RetroSync
{

    // ---------------------------------------------------------------- config
    class Config
    {
        public string Host = "";
        public int Port = 2121;
        public string User = "anonymous";
        public string Pass = "";
        public string LocalDir = "";
        public string RemoteDir = "/data/PCSX2";
        public string CardName = "my memory card.ps2";
        public int SkewMin = 2;
        public string MymcPath = "";

        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        public static string StatePath = Path.Combine(AppDir, "ps2-sync-state.ini");
        public static string BackupDir = Path.Combine(AppDir, "backup");
        AppSettings app;

        public string LocalPath { get { return Path.Combine(LocalDir, CardName); } }

        public static Dictionary<string, string> ReadIni(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            foreach (var l in File.ReadAllLines(path, Encoding.UTF8))
            {
                int i = l.IndexOf('=');
                if (i > 0) d[l.Substring(0, i).Trim()] = l.Substring(i + 1);
            }
            return d;
        }

        public static void WriteIni(string path, Dictionary<string, string> d)
        {
            File.WriteAllLines(path, d.Select(kv => kv.Key + "=" + kv.Value).ToArray(), Encoding.UTF8);
        }

        // Builds the PS2 config from the RetroSync settings (FTP host shared with RetroArch sync).
        public static Config FromApp(AppSettings a)
        {
            var c = new Config();
            c.app = a;
            c.Host = a.FtpHost; c.Port = a.FtpPort; c.User = a.FtpUser; c.Pass = a.FtpPassword ?? "";
            c.LocalDir = a.Ps2.LocalDir; c.RemoteDir = a.Ps2.RemoteDir; c.CardName = a.Ps2.CardName;
            c.SkewMin = a.Ps2.SkewMin;
            if (!string.IsNullOrEmpty(a.Ps2.MymcPath)) c.MymcPath = a.Ps2.MymcPath;
            BackupDir = Path.Combine(a.BackupRoot, "PS2 card");
            return c;
        }

        public void Save()
        {
            if (app == null) return;
            app.FtpHost = Host; app.FtpPort = Port;
            app.Ps2.LocalDir = LocalDir; app.Ps2.RemoteDir = RemoteDir; app.Ps2.CardName = CardName;
            app.Ps2.SkewMin = SkewMin; app.Ps2.MymcPath = MymcPath;
            SettingsStore.Save(app);
        }
    }

    class CardInfo
    {
        public bool Exists;
        public DateTime? When;
        public long Size;
        public string Hash;
        public int SaveCount;
        public DateTime? LastSave;
        public string LastSaveName;
    }

    class SyncState
    {
        public string Sha = "";
        public DateTime? RemoteWhen;
        public long RemoteSize;

        public static SyncState Load()
        {
            var d = Config.ReadIni(Config.StatePath);
            if (!d.ContainsKey("sha") || d["sha"].Length == 0) return null;
            var s = new SyncState { Sha = d["sha"] };
            string v;
            long ticks;
            if (d.TryGetValue("remotewhen", out v) && long.TryParse(v, out ticks) && ticks > 0) s.RemoteWhen = new DateTime(ticks);
            if (d.TryGetValue("remotesize", out v)) long.TryParse(v, out s.RemoteSize);
            return s;
        }

        public static void Save(string sha, CardInfo remote)
        {
            var d = new Dictionary<string, string>();
            d["sha"] = sha;
            d["remotewhen"] = remote != null && remote.When.HasValue ? remote.When.Value.Ticks.ToString() : "0";
            d["remotesize"] = remote != null ? remote.Size.ToString() : "0";
            Config.WriteIni(Config.StatePath, d);
        }
    }

    enum Act { None, Upload, Download, Ask, Merge, Error }

    class Plan
    {
        public Act Action;
        public string Title = "";
        public string Reason = "";
        public string Detail = "";
        public List<SaveItem> Items = new List<SaveItem>();
        public CardInfo Local;
        public CardInfo Remote;
    }



    // ------------------------------------------------------------ mymc (CLI)
    class McSave
    {
        public string Name;
        public string Title;
        public int Kb;
    }

    class SaveItem
    {
        public string Name, Title = "", Status = "";
        public int Kb;
        public DateTime? PcMod, PsMod;
        public string PcHash, PsHash;
        public int Dir; // 0 = non copiare, 1 = PC -> PS5, 2 = PS5 -> PC
    }

    class Mymc
    {
        readonly string exe;
        public Mymc(string exe) { this.exe = exe; }

        static string Q(string s) { return "\"" + s + "\""; }

        public string Run(string args, out int code)
        {
            var enc = Encoding.GetEncoding(28591);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = enc, StandardErrorEncoding = enc,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(180000))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("mymc is not responding");
                }
                p.WaitForExit();
                code = p.ExitCode;
            }
            return sb.ToString();
        }

        string Must(string args)
        {
            int code;
            string o = Run(args, out code);
            if (code != 0) throw new Exception("mymc ha fallito: " + o.Trim());
            return o;
        }

        public List<McSave> Dir(string card)
        {
            string o = Must(Q(card) + " dir");
            var lines = o.Replace("\r", "").Split(new char[] { (char)10 });
            var res = new List<McSave>();
            string[] prot = { "Copy & Delete Protected", "Delete Protected", "Not Protected", "Copy Protected", "PlayStation", "PocketStation" };
            int i = 0;
            while (i < lines.Length)
            {
                string l1 = lines[i];
                if (l1.Trim().Length == 0) { i++; continue; }
                if (Regex.IsMatch(l1, @"KB Free\s*$")) break;
                if (i + 1 < lines.Length)
                {
                    var m = Regex.Match(lines[i + 1], @"^\s*(\d+)KB\s+(.*)$");
                    if (m.Success)
                    {
                        string name = (l1.Length >= 32 ? l1.Substring(0, 32) : l1).Trim();
                        string t0 = l1.Length > 33 ? l1.Substring(33).Trim() : "";
                        string rest = m.Groups[2].Value;
                        foreach (var pr in prot) if (rest.StartsWith(pr)) { rest = rest.Substring(pr.Length); break; }
                        res.Add(new McSave { Name = name, Title = (t0 + " " + rest.Trim()).Trim(), Kb = int.Parse(m.Groups[1].Value) });
                        i += 2; continue;
                    }
                }
                i++;
            }
            return res;
        }

        public void Export(string card, string dir, string psu) { Must(Q(card) + " export -f -o " + Q(psu) + " " + Q(dir)); }
        public void Import(string card, string psu) { Must(Q(card) + " import " + Q(psu)); }
        public void Delete(string card, string dir) { Must(Q(card) + " delete " + Q(dir)); }
        public bool Check(string card) { int code; Run(Q(card) + " check", out code); return code == 0; }
    }

    // ------------------------------------------------- lettura card PS2 (sola lettura)
    class SaveInfo
    {
        public string Name;
        public string Hash;
        public DateTime? Mod;
        public int Files;
        public long Bytes;
    }

    class Ent
    {
        public ushort Mode;
        public uint Length;
        public int Cluster;
        public DateTime? Mod;
        public string Name;
    }

    class Mc
    {
        byte[] d;
        int pageLen, pageSize, ppc, allocOffset, rootRel, clusterBytes;
        uint[] fat;
        public Dictionary<string, SaveInfo> Saves = new Dictionary<string, SaveInfo>(StringComparer.OrdinalIgnoreCase);

        public static Mc TryRead(string path, out string err)
        {
            err = null;
            try
            {
                var m = new Mc();
                m.d = File.ReadAllBytes(path);
                m.Parse();
                return m;
            }
            catch (Exception e) { err = e.Message; return null; }
        }

        int U16(int o) { return d[o] | (d[o + 1] << 8); }
        uint U32(int o) { return BitConverter.ToUInt32(d, o); }

        byte[] Cluster(int abs)
        {
            var buf = new byte[clusterBytes];
            for (int p = 0; p < ppc; p++)
            {
                long off = ((long)abs * ppc + p) * pageSize;
                if (abs < 0 || off + pageLen > d.Length) throw new Exception("cluster fuori dal file");
                Buffer.BlockCopy(d, (int)off, buf, p * pageLen, pageLen);
            }
            return buf;
        }

        void Parse()
        {
            if (d.Length < 1024 || Encoding.ASCII.GetString(d, 0, 27) != "Sony PS2 Memory Card Format")
                throw new Exception("is not a PS2 memory card image");
            pageLen = U16(0x28); ppc = U16(0x2A);
            uint cpc = U32(0x30);
            allocOffset = (int)U32(0x34); rootRel = (int)U32(0x3C);
            if (pageLen != 512 || ppc < 1 || ppc > 8) throw new Exception("geometria card inattesa");
            long pages = (long)cpc * ppc;
            if (d.Length == pages * pageLen) pageSize = pageLen;
            else if (d.Length == pages * (pageLen + 16)) pageSize = pageLen + 16;
            else throw new Exception("dimensione file inattesa");
            clusterBytes = pageLen * ppc;

            var fl = new List<uint>();
            for (int i = 0; i < 32; i++)
            {
                uint ifc = U32(0x50 + 4 * i);
                if (ifc == 0 || ifc == 0xFFFFFFFF) break;
                var ic = Cluster((int)ifc);
                for (int j = 0; j < clusterBytes / 4; j++)
                {
                    uint fc = BitConverter.ToUInt32(ic, j * 4);
                    if (fc == 0 || fc == 0xFFFFFFFF) break;
                    var fcData = Cluster((int)fc);
                    for (int k = 0; k < clusterBytes / 4; k++) fl.Add(BitConverter.ToUInt32(fcData, k * 4));
                }
            }
            fat = fl.ToArray();
            if (fat.Length == 0) throw new Exception("FAT vuota");

            var root = ReadDir(rootRel);
            foreach (var e in root)
            {
                if ((e.Mode & 0x8000) == 0 || (e.Mode & 0x20) == 0) continue;
                if (e.Name == "." || e.Name == "..") continue;
                var sub = ReadDir(e.Cluster);
                var files = sub.Where(x => (x.Mode & 0x8000) != 0 && (x.Mode & 0x10) != 0)
                               .OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
                var si = new SaveInfo { Name = e.Name, Mod = e.Mod, Files = files.Count };
                using (var sha = SHA256.Create())
                {
                    var ms = new MemoryStream();
                    foreach (var f in files)
                    {
                        var nb = Encoding.UTF8.GetBytes(f.Name + "\0" + f.Length + "\0");
                        ms.Write(nb, 0, nb.Length);
                        var data = ReadChain(f.Cluster, f.Length);
                        ms.Write(data, 0, data.Length);
                        si.Bytes += f.Length;
                        if (f.Mod.HasValue && (!si.Mod.HasValue || f.Mod.Value > si.Mod.Value)) si.Mod = f.Mod;
                    }
                    ms.Position = 0;
                    si.Hash = BitConverter.ToString(sha.ComputeHash(ms)).Replace("-", "");
                }
                Saves[e.Name] = si;
            }
        }

        List<int> Chain(int rel)
        {
            var l = new List<int>();
            int guard = 0;
            while (rel >= 0 && rel < fat.Length && guard++ < 100000)
            {
                l.Add(rel);
                uint e = fat[rel];
                if ((e & 0x80000000) == 0 || e == 0xFFFFFFFF) break;
                rel = (int)(e & 0x7FFFFFFF);
            }
            return l;
        }

        byte[] ReadChain(int rel, long length)
        {
            var ms = new MemoryStream();
            foreach (var r in Chain(rel))
            {
                var c = Cluster(allocOffset + r);
                ms.Write(c, 0, c.Length);
                if (length >= 0 && ms.Length >= length) break;
            }
            var a = ms.ToArray();
            if (length >= 0)
            {
                if (a.Length < length) throw new Exception("truncated cluster chain");
                Array.Resize(ref a, (int)length);
            }
            return a;
        }

        DateTime? Tod(byte[] b, int o)
        {
            try { return new DateTime(b[o + 6] | (b[o + 7] << 8), b[o + 5], b[o + 4], b[o + 3], b[o + 2], b[o + 1]); }
            catch { return null; }
        }

        List<Ent> ReadDir(int rel)
        {
            var head = ReadChain(rel, 512);
            uint count = BitConverter.ToUInt32(head, 4);
            if (count == 0 || count > 20000) throw new Exception("invalid directory");
            var data = ReadChain(rel, (long)count * 512);
            var list = new List<Ent>();
            for (int i = 0; i < count; i++)
            {
                int o = i * 512;
                var sb = new StringBuilder();
                for (int k = 0; k < 32 && data[o + 0x40 + k] != 0; k++) sb.Append((char)data[o + 0x40 + k]);
                list.Add(new Ent
                {
                    Mode = (ushort)(data[o] | (data[o + 1] << 8)),
                    Length = BitConverter.ToUInt32(data, o + 4),
                    Cluster = (int)BitConverter.ToUInt32(data, o + 0x10),
                    Mod = Tod(data, o + 0x18),
                    Name = sb.ToString()
                });
            }
            return list;
        }
    }

    // ------------------------------------------------------------------- FTP
    class Ftp
    {
        readonly Config c;
        public Ftp(Config c) { this.c = c; }

        Uri Url(string name)
        {
            string dir = c.RemoteDir.Trim().TrimEnd(new char[] { '/' });
            if (!dir.StartsWith("/")) dir = "/" + dir;
            string path = dir + "/" + (name ?? "");
            path = string.Join("/", path.Split(new char[] { '/' }).Select(s => Uri.EscapeDataString(s)).ToArray());
            return new Uri("ftp://" + c.Host + ":" + c.Port + path);
        }

        FtpWebRequest Req(string method, string name)
        {
            var r = (FtpWebRequest)WebRequest.Create(Url(name));
            r.Method = method;
            r.Credentials = new NetworkCredential(c.User, c.Pass);
            r.UsePassive = true;
            r.UseBinary = true;
            r.KeepAlive = false;
            r.Timeout = 15000;
            r.ReadWriteTimeout = 60000;
            return r;
        }

        public CardInfo Stat()
        {
            string listing;
            using (var resp = (FtpWebResponse)Req(WebRequestMethods.Ftp.ListDirectoryDetails, "").GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                listing = sr.ReadToEnd();

            string line = null;
            foreach (var l in listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = l.TrimEnd(new char[] { (char)32, (char)9 });
                int n = c.CardName.Length;
                if (t.EndsWith(c.CardName, StringComparison.OrdinalIgnoreCase) &&
                    (t.Length == n || char.IsWhiteSpace(t[t.Length - n - 1])))
                { line = t; break; }
            }
            var info = new CardInfo();
            if (line == null) return info;
            info.Exists = true;

            long size; DateTime? when;
            ParseList(line, out size, out when);
            info.Size = size; info.When = when;

            try
            {
                using (var r = (FtpWebResponse)Req(WebRequestMethods.Ftp.GetFileSize, c.CardName).GetResponse())
                    if (r.ContentLength > 0) info.Size = r.ContentLength;
            }
            catch (WebException) { }

            try
            {
                using (var r = (FtpWebResponse)Req(WebRequestMethods.Ftp.GetDateTimestamp, c.CardName).GetResponse())
                {
                    var lm = r.LastModified;
                    if (lm.Year > 1980) info.When = lm.Kind == DateTimeKind.Utc ? lm.ToLocalTime() : lm;
                }
            }
            catch (WebException) { }

            return info;
        }

        static void ParseList(string line, out long size, out DateTime? when)
        {
            size = 0; when = null;
            var inv = CultureInfo.InvariantCulture;
            Match m;

            // formato Unix: -rw-r--r-- 1 u g 8650752 Oct  3 12:34 nome
            m = Regex.Match(line, @"\s(\d+)\s+([A-Za-z]{3})\s+(\d{1,2})\s+(\d{1,2}:\d{2}|\d{4})\s");
            if (m.Success)
            {
                long.TryParse(m.Groups[1].Value, out size);
                string tm = m.Groups[4].Value;
                DateTime d;
                if (tm.Contains(":"))
                {
                    if (DateTime.TryParseExact(m.Groups[2].Value + " " + m.Groups[3].Value + " " + tm + " " + DateTime.Now.Year,
                        "MMM d H:mm yyyy", inv, DateTimeStyles.None, out d))
                    {
                        if (d > DateTime.Now.AddDays(1)) d = d.AddYears(-1);
                        when = d;
                    }
                }
                else if (DateTime.TryParseExact(m.Groups[2].Value + " " + m.Groups[3].Value + " " + tm,
                    "MMM d yyyy", inv, DateTimeStyles.None, out d))
                    when = d;
                return;
            }

            // formato Windows/IIS: 10-03-26  12:34PM  8650752 nome
            m = Regex.Match(line, @"^(\d{2})-(\d{2})-(\d{2,4})\s+(\d{1,2}):(\d{2})(AM|PM)\s+(\d+)\s");
            if (m.Success)
            {
                long.TryParse(m.Groups[7].Value, out size);
                int y = int.Parse(m.Groups[3].Value); if (y < 100) y += 2000;
                int h = int.Parse(m.Groups[4].Value) % 12; if (m.Groups[6].Value == "PM") h += 12;
                when = new DateTime(y, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), h, int.Parse(m.Groups[5].Value), 0);
                return;
            }

            // formato gg/mm/aaaa hh:mm[:ss]
            m = Regex.Match(line, @"(\d{2})/(\d{2})/(\d{4})\s+(\d{2}):(\d{2})(?::(\d{2}))?");
            if (m.Success)
            {
                when = new DateTime(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value),
                    int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value),
                    m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0);
            }
            foreach (var tok in line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                long v;
                if (long.TryParse(tok, out v) && v > 100000) { size = v; break; }
            }
        }

        public void Download(string dest, long expected, Action<int> progress)
        {
            using (var resp = (FtpWebResponse)Req(WebRequestMethods.Ftp.DownloadFile, c.CardName).GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = File.Create(dest))
            {
                var buf = new byte[64 * 1024];
                long done = 0; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n);
                    done += n;
                    if (expected > 0) progress((int)Math.Min(100, done * 100 / expected));
                }
            }
            progress(100);
        }

        public void Upload(string src, Action<int> progress)
        {
            var r = Req(WebRequestMethods.Ftp.UploadFile, c.CardName);
            var total = new FileInfo(src).Length;
            using (var f = File.OpenRead(src))
            using (var s = r.GetRequestStream())
            {
                var buf = new byte[64 * 1024];
                long done = 0; int n;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    s.Write(buf, 0, n);
                    done += n;
                    progress((int)Math.Min(100, done * 100 / total));
                }
            }
            using (r.GetResponse()) { }
            progress(100);
        }
    }

    // ------------------------------------------------- analisi senza UI (form + --dry-run)
    static class Ps2Engine
    {
        static string Fmt(DateTime? d) { return d.HasValue && d.Value.Year > 1980 ? d.Value.ToString("dd/MM/yyyy HH:mm") : "\u2014"; }
        static string Mb(long b) { return b > 0 ? (b / 1048576.0).ToString("0.0") + " MB" : "?"; }

        // Same steps as PS2CardSync's Analyze(): reads both cards (the PS5 one is downloaded to a temp file),
        // compares individual saves and returns the plan. Never writes to the PS5 or to the PC card.
        // persistState=false skips refreshing sync-state.ini when the cards are identical (dry-run).
        public static Plan Analyze(Config cfg, Action<string> Log, Action<int> Progress, Action<CardInfo, CardInfo> ShowCards, bool persistState)
        {
            if (!Directory.Exists(cfg.LocalDir)) throw new Exception("PC folder not found: " + cfg.LocalDir);
            Log("Reading the card on the PC\u2026");
            var local = LocalInfo(cfg);
            Log("Reading the card on the PS5 (" + cfg.Host + ":" + cfg.Port + ")\u2026");
            var ftp = new Ftp(cfg);
            var remote = ftp.Stat();
            Log("PC : " + (local.Exists ? Fmt(local.When) + "  " + Mb(local.Size) : "missing"));
            Log("PS5: " + (remote.Exists ? Fmt(remote.When) + "  " + Mb(remote.Size) : "missing"));
            ShowCards(local, remote);

            var p = new Plan { Local = local, Remote = remote };
            var skew = TimeSpan.FromMinutes(cfg.SkewMin);

            if (!local.Exists && !remote.Exists)
            {
                p.Action = Act.Error; p.Title = "No card found";
                p.Reason = "The card is on neither the PC nor the PS5.";
            }
            else if (local.Exists && !remote.Exists)
            {
                p.Action = Act.Upload; p.Title = "PC \u2192 PS5";
                p.Reason = "No card on the PS5: uploading the PC card.";
            }
            else if (!local.Exists)
            {
                p.Action = Act.Download; p.Title = "PS5 \u2192 PC";
                p.Reason = "No card on the PC: downloading the PS5 card.";
            }
            else
            {
                Log("Downloading the PS5 card to a temp file to compare saves\u2026");
                string tmp = Path.Combine(Path.GetTempPath(), "ps2sync-compare.tmp");
                ftp.Download(tmp, remote.Size, Progress);
                remote.Hash = Sha(tmp);
                string e1, e2;
                var mcPc = Mc.TryRead(cfg.LocalPath, out e1);
                var mcPs = Mc.TryRead(tmp, out e2);
                List<McSave> dPc = null, dPs = null;
                bool mymcOk = false;
                if (File.Exists(cfg.MymcPath))
                {
                    try
                    {
                        Log("Reading saves with mymc\u2026");
                        var mm = new Mymc(cfg.MymcPath);
                        dPc = mm.Dir(cfg.LocalPath);
                        dPs = mm.Dir(tmp);
                        mymcOk = true;
                    }
                    catch (Exception ex) { Log("mymc: " + ex.Message); }
                }
                else Log("mymc.exe not found in " + cfg.MymcPath + ": set the path from \u2699. For now copying whole cards.");
                try { File.Delete(tmp); } catch { }
                if (mcPc != null) Fill(local, mcPc);
                if (mcPs != null) Fill(remote, mcPs);
                ShowCards(local, remote);

                if (local.Hash == remote.Hash)
                {
                    if (persistState) SyncState.Save(local.Hash, remote);
                    p.Action = Act.None; p.Title = "Already aligned";
                    p.Reason = "The two files are identical.";
                }
                else if (mcPc != null && mcPs != null && mcPc.Saves.Count > 0 && mcPs.Saves.Count > 0)
                {
                    Log("Saves read: PC " + mcPc.Saves.Count + ", PS5 " + mcPs.Saves.Count + ".");
                    CompareSaves(p, mcPc, mcPs, dPc, dPs, mymcOk);
                }
                else
                {
                    if (e1 != null) Log("PC card unreadable: " + e1);
                    if (e2 != null) Log("PS5 card unreadable: " + e2);
                    Log("Non riesco a leggere i save: uso on confronto del file intero.");
                    var st = SyncState.Load();
                    bool lc = st == null || st.Sha != local.Hash;
                    bool rc = st == null || st.Sha != remote.Hash;
                    if (lc && !rc) { p.Action = Act.Upload; p.Title = "PC \u2192 PS5"; p.Reason = "The PC changed since the last sync, the PS5 did not."; }
                    else if (!lc && rc) { p.Action = Act.Download; p.Title = "PS5 \u2192 PC"; p.Reason = "The PS5 changed since the last sync, the PC did not."; }
                    else if (local.When.Value > (remote.When ?? DateTime.MinValue) + skew)
                    { p.Action = Act.Upload; p.Title = "PC \u2192 PS5"; p.Reason = "Files differ; the PC one is newer."; }
                    else if (remote.When.HasValue && remote.When.Value > local.When.Value + skew)
                    { p.Action = Act.Download; p.Title = "PS5 \u2192 PC"; p.Reason = "Files differ; the PS5 one is newer."; }
                    else { p.Action = Act.Ask; p.Title = "Timestamps too close"; p.Reason = "Files differ and it is unclear which is newer."; }
                }
            }

            return p;
        }

        public static string Sha(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        }

        public static CardInfo LocalInfo(Config cfg)
        {
            var i = new CardInfo();
            if (File.Exists(cfg.LocalPath))
            {
                var fi = new FileInfo(cfg.LocalPath);
                i.Exists = true; i.When = fi.LastWriteTime; i.Size = fi.Length; i.Hash = Sha(cfg.LocalPath);
            }
            return i;
        }

        public static void Fill(CardInfo c, Mc m)
        {
            c.SaveCount = m.Saves.Count;
            var last = m.Saves.Values.Where(x => x.Mod.HasValue).OrderByDescending(x => x.Mod.Value).FirstOrDefault();
            if (last != null) { c.LastSave = last.Mod; c.LastSaveName = last.Name; }
            else c.LastSaveName = "";
        }

        static string Names(List<string> l)
        {
            return string.Join(", ", l.Take(6).ToArray()) + (l.Count > 6 ? " \u2026(+" + (l.Count - 6) + ")" : "");
        }

        static void CompareSaves(Plan p, Mc pc, Mc ps, List<McSave> dpc, List<McSave> dps, bool useMymc)
        {
            var names = pc.Saves.Keys.Union(ps.Saves.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var n in names)
            {
                SaveInfo a, b;
                pc.Saves.TryGetValue(n, out a); ps.Saves.TryGetValue(n, out b);
                McSave ma = dpc == null ? null : dpc.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                McSave mb = dps == null ? null : dps.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                McSave mt = ma ?? mb;
                var it = new SaveItem
                {
                    Name = n, Title = mt != null ? mt.Title : "", Kb = mt != null ? mt.Kb : 0,
                    PcHash = a != null ? a.Hash : null, PsHash = b != null ? b.Hash : null,
                    PcMod = a != null ? a.Mod : null, PsMod = b != null ? b.Mod : null
                };
                if (a == null) { it.Status = "solo PS5"; it.Dir = 2; }
                else if (b == null) { it.Status = "PC only"; it.Dir = 1; }
                else if (a.Hash == b.Hash) { it.Status = "identical"; it.Dir = 0; }
                else if (a.Mod.GetValueOrDefault() > b.Mod.GetValueOrDefault()) { it.Status = "PC newer"; it.Dir = 1; }
                else if (b.Mod.GetValueOrDefault() > a.Mod.GetValueOrDefault()) { it.Status = "PS5 newer"; it.Dir = 2; }
                else { it.Status = "diverso, stessa data"; it.Dir = 0; }
                p.Items.Add(it);
            }

            var pcNew = p.Items.Where(i => i.Dir == 1).Select(i => i.Name).ToList();
            var psNew = p.Items.Where(i => i.Dir == 2).Select(i => i.Name).ToList();
            int amb = p.Items.Count(i => i.Status.StartsWith("diverso"));

            if (pcNew.Count == 0 && psNew.Count == 0 && amb == 0)
            {
                p.Action = Act.None; p.Title = "Save identici";
                p.Reason = "Both cards contain the same saves: the file only differs in internal details, copying nothing.";
                return;
            }

            if (useMymc)
            {
                p.Action = Act.Merge; p.Title = "Per-save sync";
                p.Reason = pcNew.Count + " to the PS5, " + psNew.Count + " to the PC" + (amb > 0 ? ", " + amb + " to decide" : "") + ". Double-click a save to change direction.";
                return;
            }

            if (amb > 0)
            {
                p.Action = Act.Ask; p.Title = "Save diversi, stessa data";
                p.Reason = "Alcuni save hanno contenuto diverso ma data uguale: scegli quale card tenere.";
                p.Detail = "Newer PC saves: " + (pcNew.Count > 0 ? Names(pcNew) : "none") + "\nNewer PS5 saves: " + (psNew.Count > 0 ? Names(psNew) : "none") + "\nSave ambigui: " + amb;
            }
            else if (psNew.Count == 0)
            {
                p.Action = Act.Upload; p.Title = "PC \u2192 PS5";
                p.Reason = pcNew.Count + " newer or newer saves on the PC (" + Names(pcNew) + "). No newer ones on the PS5.";
            }
            else if (pcNew.Count == 0)
            {
                p.Action = Act.Download; p.Title = "PS5 \u2192 PC";
                p.Reason = psNew.Count + " newer or newer saves on the PS5 (" + Names(psNew) + "). No newer ones on the PC.";
            }
            else
            {
                p.Action = Act.Ask; p.Title = "Entrambe hanno save nuovi";
                p.Reason = "On the PC: " + Names(pcNew) + ". On the PS5: " + Names(psNew) + ".";
                p.Detail = "Newer saves on PC: " + Names(pcNew) + "\nNewer saves on PS5: " + Names(psNew) + "\n\nIf you keep one card, the newer saves on the other side remain only in the backup.";
            }
        }

    }

    // ------------------------------------------------------------------ PS2 window
    class Ps2CardForm : Form
    {
        Config cfg;
        readonly bool readOnly;
        Plan plan;
        bool busy;

        Label lblPc, lblPs, banner, bannerSub;
        Button btnAnalyze, btnSync, btnUp, btnDown, btnSettings;
        TextBox log;
        ProgressBar bar;
        ListView lv;
        bool reanalyze;

        // RetroSync 2.0: dark palette (presentation only)
        static readonly Color Bg = Theme.Content;
        static readonly Color Ink = Theme.Text;
        static readonly Color Muted = Theme.Muted;
        static readonly Color Accent = Theme.Accent;
        static readonly Color Ok = Theme.Ok;
        static readonly Color Warn = Theme.Warn;
        static readonly Color Bad = Theme.Bad;

        public Ps2CardForm(AppSettings app, bool readOnly)
        {
            cfg = Config.FromApp(app);
            this.readOnly = readOnly;
            Text = "RetroSync \u2014 PS2 memory card" + (readOnly ? " (preview)" : "");
            ClientSize = new Size(820, 710);
            MinimumSize = new Size(700, 650);
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.F13;
            BackColor = Bg;
            ForeColor = Ink;
            Icon = MakeIcon();

            var title = new Label { Text = "PS2 Card Sync", Font = Theme.S24, AutoSize = true, Location = new Point(20, 14) };
            var subtitle = new Label { Text = readOnly ? "PREVIEW MODE: analysis only, no card is written" : "PCSX2 \u21C4 PS5SX2 \u2014 move saves with mymc; cards are always archived first", ForeColor = readOnly ? Warn : Muted, AutoSize = true, Location = new Point(22, 48) };
            Controls.Add(title); Controls.Add(subtitle);

            lblPc = MakeCard("PC", 20, 80);
            lblPs = MakeCard("PS5", 420, 80);
            lblPc.Parent.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            lblPs.Parent.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            var bp = new Panel { Location = new Point(20, 200), Size = new Size(780, 70), BackColor = Theme.Card, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            banner = new Label { Location = new Point(14, 8), Size = new Size(750, 30), Font = Theme.S15, Text = "Checking\u2026", ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            bannerSub = new Label { Location = new Point(15, 40), Size = new Size(750, 24), ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            bp.Controls.Add(banner); bp.Controls.Add(bannerSub);
            Controls.Add(bp);

            lv = new ListView
            {
                Location = new Point(20, 284), Size = new Size(780, 160), View = View.Details, FullRowSelect = true,
                BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = true,
                BackColor = Theme.Card, ForeColor = Theme.Text,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            lv.Columns.Add("Save", 125); lv.Columns.Add("Title", 170); lv.Columns.Add("KB", 45); lv.Columns.Add("PC", 100); lv.Columns.Add("PS5", 100); lv.Columns.Add("Result", 115); lv.Columns.Add("Action", 100);
            lv.DoubleClick += (s, e) => { if (lv.SelectedItems.Count > 0) CycleDir((SaveItem)lv.SelectedItems[0].Tag); };
            Controls.Add(lv);

            btnSync = MakeButton("Sync", 20, 456, 150, true);
            btnAnalyze = MakeButton("Recheck", 180, 456, 110, false);
            btnUp = MakeButton("Force PC \u2192 PS5", 300, 456, 130, false);
            btnDown = MakeButton("Force PS5 \u2192 PC", 440, 456, 130, false);
            btnSettings = MakeButton("\u2699", 740, 456, 60, false);
            btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSync.Click += (s, e) => DoSync();
            btnAnalyze.Click += (s, e) => Analyze();
            btnUp.Click += (s, e) => DoForce(Act.Upload);
            btnDown.Click += (s, e) => DoForce(Act.Download);
            btnSettings.Click += (s, e) => OpenSettings();
            btnSync.Enabled = false;

            bar = new ProgressBar { Location = new Point(20, 506), Size = new Size(780, 8), Style = ProgressBarStyle.Continuous, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(bar);

            log = new TextBox
            {
                Location = new Point(20, 524), Size = new Size(780, 170), Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, BackColor = Theme.Field, BorderStyle = BorderStyle.None,
                Font = Theme.Mono11, ForeColor = Theme.TextSoft,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(log);

            Shown += (s, e) => Analyze();
        }

        // ---- RetroSync 2.0: hooks used to embed this window in the nevern app (presentation only, no sync logic)
        public Action<string> LogHook;
        public bool IsBusy { get { return busy; } }
        public int ProgressValue { get { return bar.Value; } }
        public void StartAnalysis() { Analyze(); }
        internal void ShowSample(Plan p, string[] lines)   // --demo screenshots only: shows a sample plan, never runs anything
        {
            busy = true; plan = p;
            ShowCards(p.Local, p.Remote); FillItems(p.Items);
            SetBanner(p.Title, p.Reason, Accent);
            foreach (var l in lines) log.AppendText(l + "\r\n");
            btnAnalyze.Enabled = true; btnSettings.Enabled = true;
            btnUp.Enabled = !readOnly; btnDown.Enabled = !readOnly; btnSync.Enabled = !readOnly && CanSync();
        }

        Label MakeCard(string name, int x, int y)
        {
            var p = new Panel { Location = new Point(x, y), Size = new Size(380, 105), BackColor = Theme.Card };
            var h = new Label { Text = name, Font = Theme.S13, Location = new Point(14, 10), AutoSize = true };
            var body = new Label { Location = new Point(15, 38), Size = new Size(355, 62), ForeColor = Muted, Text = "\u2026" };
            p.Controls.Add(h); p.Controls.Add(body);
            Controls.Add(p);
            return body;
        }

        Button MakeButton(string text, int x, int y, int w, bool primary)
        {
            var b = new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, 36), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? Accent : Theme.Btn, ForeColor = primary ? Theme.OnAccent : Ink,
                Font = Theme.S13, UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = primary ? Accent : Theme.BtnBorder;
            b.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHi : Theme.BtnHover;
            Controls.Add(b);
            return b;
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Accent)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var p = new Pen(Color.White, 3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor })
                {
                    g.DrawLine(p, 8, 12, 24, 12);
                    g.DrawLine(p, 24, 21, 8, 21);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---- helpers UI
        void Ui(Action a) { if (InvokeRequired) BeginInvoke(a); else a(); }

        void Log(string s) { if (LogHook != null) LogHook(s); Ui(() => log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n")); }

        void Progress(int v) { Ui(() => bar.Value = Math.Max(0, Math.Min(100, v))); }

        void SetBusy(bool b)
        {
            busy = b;
            Ui(() =>
            {
                btnAnalyze.Enabled = !b; btnUp.Enabled = !b && !readOnly; btnDown.Enabled = !b && !readOnly; btnSettings.Enabled = !b;
                btnSync.Enabled = !b && !readOnly && CanSync();
                UseWaitCursor = b;
                if (!b) bar.Value = 0;
            });
        }

        void SetBanner(string text, string sub, Color color)
        {
            Ui(() => { banner.Text = text; banner.ForeColor = color; bannerSub.Text = sub; });
        }

        static string Fmt(DateTime? d) { return d.HasValue && d.Value.Year > 1980 ? d.Value.ToString("dd/MM/yyyy HH:mm") : "\u2014"; }
        static string Mb(long b) { return b > 0 ? (b / 1048576.0).ToString("0.0") + " MB" : "?"; }

        string CardText(string where, CardInfo i)
        {
            if (i == null || !i.Exists) return where + "\r\nCard not found";
            if (i.SaveCount > 0)
                return where + "\r\n" + i.SaveCount + " saves \u00B7 last: " + Fmt(i.LastSave) + "\r\n" + i.LastSaveName + " \u00B7 " + Mb(i.Size);
            return where + "\r\nModified: " + Fmt(i.When) + "\r\nSize: " + Mb(i.Size);
        }

        void ShowCards(CardInfo local, CardInfo remote)
        {
            Ui(() =>
            {
                lblPc.Text = CardText(cfg.LocalDir, local);
                lblPs.Text = CardText(cfg.Host + ":" + cfg.Port + cfg.RemoteDir, remote);
            });
        }

        bool CanSync()
        {
            if (plan == null) return false;
            if (plan.Action == Act.Merge) return plan.Items.Any(i => i.Dir != 0);
            return plan.Action == Act.Upload || plan.Action == Act.Download || plan.Action == Act.Ask;
        }

        static string DirText(SaveItem it) { return it.Dir == 1 ? "PC \u2192 PS5" : it.Dir == 2 ? "PS5 \u2192 PC" : "\u2014"; }

        void RefreshRow(ListViewItem v)
        {
            var it = (SaveItem)v.Tag;
            v.SubItems[6].Text = DirText(it);
            v.ForeColor = it.Dir != 0 ? Accent : it.Status.StartsWith("diverso") ? Warn : Muted;
        }

        void FillItems(List<SaveItem> items)
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var it in items)
            {
                var v = new ListViewItem(new[] { it.Name, it.Title, it.Kb > 0 ? it.Kb.ToString() : "", Fmt(it.PcMod), Fmt(it.PsMod), it.Status, "" });
                v.Tag = it;
                RefreshRow(v);
                lv.Items.Add(v);
            }
            lv.EndUpdate();
        }

        void CycleDir(SaveItem it)
        {
            if (busy || plan == null || plan.Action != Act.Merge) return;
            int[] opts = it.PcHash == null ? new[] { 2, 0 }
                       : it.PsHash == null ? new[] { 1, 0 }
                       : it.PcHash == it.PsHash ? new[] { 0 } : new[] { 0, 1, 2 };
            int idx = Array.IndexOf(opts, it.Dir);
            it.Dir = opts[(idx + 1) % opts.Length];
            foreach (ListViewItem v in lv.Items) if (v.Tag == it) RefreshRow(v);
            btnSync.Enabled = !readOnly && CanSync();
        }

        void Run(Action work)
        {
            SetBusy(true);
            Task.Factory.StartNew(() =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    var e = ex; while (e.InnerException != null) e = e.InnerException;
                    Log("ERROR: " + e.Message);
                    SetBanner("Something went wrong", e.Message, Bad);
                }
                finally
                {
                    SetBusy(false);
                    if (reanalyze) { reanalyze = false; Ui(() => Analyze()); }
                }
            });
        }

        static string Sha(string path) { return Ps2Engine.Sha(path); }
        CardInfo LocalInfo() { return Ps2Engine.LocalInfo(cfg); }

        void Analyze()
        {
            if (busy) return;
            plan = null;
            SetBanner("Checking\u2026", "Connecting to the PS5 and reading both cards", Muted);
            Run(() =>
            {
                var p = Ps2Engine.Analyze(cfg, Log, Progress, ShowCards, !readOnly);
                plan = p;
                Ui(() => FillItems(p.Items));
                Color col = p.Action == Act.None ? Ok : p.Action == Act.Error ? Bad : p.Action == Act.Ask ? Warn : Accent;
                SetBanner(p.Title, p.Reason, col);
                Log(p.Title + " \u2014 " + p.Reason);
                if (readOnly) Log("Preview mode: no changes.");
            });
        }

        // ---- azioni
        static bool PcsxRunning()
        {
            return Process.GetProcesses().Any(pr => pr.ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase));
        }

        bool CheckEmulator()
        {
            if (!PcsxRunning()) return true;
            DarkDialog.Info(this, "PCSX2 is running", "PCSX2 is open: close it before syncing, or it will rewrite the card in memory.", Ico.Warning, Theme.Warn);
            return false;
        }

        void DoSync()
        {
            if (busy || plan == null || readOnly) return;
            if (!CheckEmulator()) return;
            if (plan.Action == Act.Merge) { DoMerge(); return; }
            Act a = plan.Action;
            if (a == Act.Ask)
            {
                var r = DarkDialog.Ask3(this, "Quale card tenere?",
                    (plan.Detail.Length > 0 ? plan.Detail : "PC: " + Fmt(plan.Local.When) + "\nPS5: " + Fmt(plan.Remote.When)) + "\n\nYes = keep the PC card\nNo = keep the PS5 card",
                    "Yes (PC card)", "No (PS5 card)", "Cancel", Ico.Card, Theme.Warn);
                if (r == DialogResult.Cancel) return;
                a = r == DialogResult.Yes ? Act.Upload : Act.Download;
            }
            if (a == Act.None || a == Act.Error) return;
            Execute(a);
        }

        void DoMerge()
        {
            var items = plan.Items.Where(i => i.Dir != 0).ToList();
            if (items.Count == 0) return;
            int a = items.Count(i => i.Dir == 1), b = items.Count(i => i.Dir == 2);
            if (DarkDialog.Ask(this, "Confirm",
                "Moving " + a + " saves to the PS5 and " + b + " to the PC, using mymc.\n\nBefore touching anything I archive both cards. If something looks wrong, I write nothing.",
                "OK", "Cancel", Ico.Card) != DialogResult.OK) return;
            ExecuteMerge(items);
        }

        void ExecuteMerge(List<SaveItem> items)
        {
            Run(() =>
            {
                var mm = new Mymc(cfg.MymcPath);
                var ftp = new Ftp(cfg);
                string wd = Path.Combine(Config.AppDir, "work");
                if (Directory.Exists(wd)) Directory.Delete(wd, true);
                Directory.CreateDirectory(wd);
                string pcW = Path.Combine(wd, "pc.ps2"), psW = Path.Combine(wd, "ps5.ps2");

                var remote = ftp.Stat();
                if (!remote.Exists) throw new Exception("The card does not exist on the PS5.");
                Log("Downloading the PS5 card\u2026");
                ftp.Download(psW, remote.Size, Progress);
                File.Copy(cfg.LocalPath, pcW);
                Backup(cfg.LocalPath, "pc");
                Backup(psW, "ps5");

                bool preP = mm.Check(pcW), preS = mm.Check(psW);
                if (!preP || !preS) Log("Warning: mymc already reports problems in the card originale (" + (!preP ? "PC" : "PS5") + ").");

                int k = 0;
                foreach (var it in items)
                {
                    string src = it.Dir == 1 ? pcW : psW, dst = it.Dir == 1 ? psW : pcW;
                    bool dstHas = it.Dir == 1 ? it.PsHash != null : it.PcHash != null;
                    string psu = Path.Combine(wd, (++k) + ".psu");
                    Log((it.Dir == 1 ? "PC \u2192 PS5: " : "PS5 \u2192 PC: ") + it.Name);
                    mm.Export(src, it.Name, psu);
                    if (dstHas) mm.Delete(dst, it.Name);
                    mm.Import(dst, psu);
                    Progress(k * 100 / items.Count);
                }

                Log("Verifico le card risultanti\u2026");
                if (preP && !mm.Check(pcW)) throw new Exception("mymc reports errors in the resulting PC card: nothing was changed.");
                if (preS && !mm.Check(psW)) throw new Exception("mymc reports errors in the resulting PS5 card: nothing was changed.");
                string e1, e2;
                var mp = Mc.TryRead(pcW, out e1);
                var ms = Mc.TryRead(psW, out e2);
                if (mp == null || ms == null) throw new Exception("Could not re-read the resulting cards: nothing was changed.");
                foreach (var it in items)
                {
                    var tgt = it.Dir == 1 ? ms : mp;
                    string want = it.Dir == 1 ? it.PcHash : it.PsHash;
                    SaveInfo si;
                    if (!tgt.Saves.TryGetValue(it.Name, out si) || si.Hash != want)
                        throw new Exception("Verification failed for " + it.Name + ": nothing was changed.");
                }

                if (items.Any(i => i.Dir == 1))
                {
                    Log("Uploading the updated card to the PS5\u2026");
                    ftp.Upload(psW, Progress);
                    var after = ftp.Stat();
                    if (!after.Exists || (after.Size > 0 && after.Size != new FileInfo(psW).Length))
                        throw new Exception("Upload to the PS5 not verified: the PC card was not touched.");
                }
                if (items.Any(i => i.Dir == 2))
                {
                    File.Copy(pcW, cfg.LocalPath, true);
                    Log("PC card updated.");
                }
                try { Directory.Delete(wd, true); } catch { }
                Log("Sync completed.");
                reanalyze = true;
            });
        }

        void DoForce(Act a)
        {
            if (busy || readOnly) return;
            if (!CheckEmulator()) return;
            string msg = a == Act.Upload
                ? "Overwrite the PS5 card with the PC one (the old one is archived). Continue?"
                : "Overwrite the PC card with the PS5 one (the old one is archived). Continue?";
            if (DarkDialog.Ask(this, "Confirm", msg, "OK", "Cancel", Ico.Card) != DialogResult.OK) return;
            Execute(a);
        }

        void Backup(string src, string tag)
        {
            Directory.CreateDirectory(Config.BackupDir);
            string dest = Path.Combine(Config.BackupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + tag + "-" + cfg.CardName);
            File.Copy(src, dest, true);
            Log("Archiviata in backup\\" + Path.GetFileName(dest));
            // keep only the last 10 backups per side
            var old = new DirectoryInfo(Config.BackupDir).GetFiles("*-" + tag + "-*").OrderByDescending(f => f.Name).Skip(10);
            foreach (var f in old) { try { f.Delete(); } catch { } }
        }

        void Execute(Act a)
        {
            Run(() =>
            {
                var ftp = new Ftp(cfg);
                if (a == Act.Upload)
                {
                    var remote = ftp.Stat();
                    if (remote.Exists)
                    {
                        Log("Downloading the PS5 card to archive it\u2026");
                        string tmp = Path.Combine(Path.GetTempPath(), "ps2sync-backup.tmp");
                        ftp.Download(tmp, remote.Size, Progress);
                        Backup(tmp, "ps5");
                        File.Delete(tmp);
                    }
                    Log("Uploading the PC card to the PS5\u2026");
                    ftp.Upload(cfg.LocalPath, Progress);
                    var after = ftp.Stat();
                    if (!after.Exists || (after.Size > 0 && after.Size != new FileInfo(cfg.LocalPath).Length))
                        throw new Exception("Upload not verified: size on the PS5 does not match.");
                    SyncState.Save(Sha(cfg.LocalPath), after);
                    Log("Done. In PS5SX2 the slot must point to '" + cfg.CardName + "'.");
                    SetBanner("Done \u2714", "The PS5 now has the PC card.", Ok);
                }
                else
                {
                    var remote = ftp.Stat();
                    if (!remote.Exists) throw new Exception("The card does not exist on the PS5.");
                    string part = cfg.LocalPath + ".partial";
                    Log("Downloading the card from the PS5\u2026");
                    ftp.Download(part, remote.Size, Progress);
                    if (remote.Size > 0 && new FileInfo(part).Length != remote.Size)
                    {
                        File.Delete(part);
                        throw new Exception("Incomplete download: the PC card was not touched.");
                    }
                    if (File.Exists(cfg.LocalPath)) { Backup(cfg.LocalPath, "pc"); File.Delete(cfg.LocalPath); }
                    File.Move(part, cfg.LocalPath);
                    SyncState.Save(Sha(cfg.LocalPath), remote);
                    Log("Done.");
                    SetBanner("Done \u2714", "The PC now has the PS5 card.", Ok);
                }
                // aggiorna schede e stato
                var l2 = LocalInfo(); var r2 = ftp.Stat();
                ShowCards(l2, r2);
                plan = new Plan { Action = Act.None, Local = l2, Remote = r2 };
            });
        }

        void OpenSettings()
        {
            using (var f = new SettingsForm(cfg))
                if (f.ShowDialog(this) == DialogResult.OK) { cfg.Save(); Analyze(); }
        }
    }

    // -------------------------------------------------------------- settings
    class SettingsForm : Form
    {
        TextBox host, port, remote, local, card, skew, mymc;

        public SettingsForm(Config c)
        {
            Text = "Settings";
            ClientSize = new Size(480, 370);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f);

            host = Row("PS5 IP", c.Host, 16);
            port = Row("FTP port", c.Port.ToString(), 56);
            remote = Row("Folder on the PS5", c.RemoteDir, 96);
            local = Row("Folder on the PC", c.LocalDir, 136);
            card = Row("Card name", c.CardName, 176);
            skew = Row("Tolleranza orari (min)", c.SkewMin.ToString(), 216);
            mymc = Row("Path to mymc.exe", c.MymcPath, 256);
            var browse2 = new Button { Text = "\u2026", Location = new Point(436, 255), Size = new Size(30, 26) };
            browse2.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "mymc.exe|mymc.exe|Programmi|*.exe", FileName = mymc.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) mymc.Text = d.FileName;
            };
            Controls.Add(browse2);

            var browse = new Button { Text = "\u2026", Location = new Point(436, 135), Size = new Size(30, 26) };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = local.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) local.Text = d.SelectedPath;
            };
            Controls.Add(browse);

            var ok = new Button { Text = "Save", Location = new Point(290, 320), Size = new Size(85, 32), DialogResult = DialogResult.None };
            var cancel = new Button { Text = "Cancel", Location = new Point(385, 320), Size = new Size(85, 32), DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                int p, m;
                if (!int.TryParse(port.Text, out p) || !int.TryParse(skew.Text, out m))
                { DarkDialog.Info(this, "Settings", "Porta e tolleranza devono essere numeri.", Ico.Warning, Theme.Warn); return; }
                c.Host = host.Text.Trim(); c.Port = p; c.RemoteDir = remote.Text.Trim();
                c.LocalDir = local.Text.Trim(); c.CardName = card.Text.Trim(); c.SkewMin = m; c.MymcPath = mymc.Text.Trim();
                DialogResult = DialogResult.OK;
            };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            Theme.ApplyDialog(this);   // RetroSync 2.0: dark look
        }

        TextBox Row(string label, string value, int y)
        {
            Controls.Add(new Label { Text = label, Location = new Point(16, y + 4), AutoSize = true });
            var t = new TextBox { Text = value, Location = new Point(180, y), Size = new Size(250, 26) };
            Controls.Add(t);
            return t;
        }
    }
}
