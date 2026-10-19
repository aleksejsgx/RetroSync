using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroSync
{
    public class RemoteEntry
    {
        public string Name;
        public bool IsDir;
        public long Size;
        public DateTime? ModifiedUtc;
    }

    public class FtpException : Exception
    {
        public int Code;
        public FtpException(int code, string msg) : base(msg) { Code = code; }
    }

    // Minimal passive-mode FTP client on raw sockets (no external tools).
    // Written for ftpsrv on the PS5: no MDTM, MLSD only lists the current directory,
    // anonymous login is accepted right after USER. One persistent control connection.
    public class FtpClient : IDisposable
    {
        readonly string host, user, pass;
        readonly int port, timeoutMs;
        public bool ReadOnly;            // when true every write command throws (dry-run safety net)
        public bool ListTimesUtc = true;
        public int ClockOffsetMinutes;
        public Action<string> Trace;

        TcpClient ctrl;
        NetworkStream cs;
        StreamReader rd;
        bool hasMlsd;
        string cwd;
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        public FtpClient(string host, int port, string user, string pass, int timeoutSeconds)
        {
            this.host = host; this.port = port; this.user = string.IsNullOrEmpty(user) ? "anonymous" : user; this.pass = pass ?? "";
            timeoutMs = Math.Max(3, timeoutSeconds) * 1000;
        }

        class Reply { public int Code; public string Text; }

        public void Dispose()
        {
            try { if (cs != null) { Write("QUIT"); } } catch { }
            Close();
        }

        void Close()
        {
            try { if (ctrl != null) ctrl.Close(); } catch { }
            ctrl = null; cs = null; rd = null; cwd = null;
        }

        static TcpClient ConnectTcp(string h, int p, int timeout)
        {
            var t = new TcpClient();
            var ar = t.BeginConnect(h, p, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeout)) { try { t.Close(); } catch { } throw new IOException("Connection timed out to " + h + ":" + p); }
            t.EndConnect(ar);
            t.ReceiveTimeout = timeout; t.SendTimeout = timeout;
            return t;
        }

        public void Connect()
        {
            Close();
            ctrl = ConnectTcp(host, port, timeoutMs);
            cs = ctrl.GetStream();
            rd = new StreamReader(cs, Utf8);
            var r = Read();
            if (r.Code != 220) throw new FtpException(r.Code, "Server FTP: " + r.Text);
            r = Cmd("USER " + user);
            if (r.Code == 331 || r.Code == 332) r = Cmd("PASS " + pass);
            if (r.Code != 230 && r.Code != 202) throw new FtpException(r.Code, "FTP login rejected: " + r.Text);
            Cmd("TYPE I");
            r = Cmd("FEAT");
            hasMlsd = r.Code == 211 && r.Text.ToUpperInvariant().Contains("MLSD");
            if (Trace != null) Trace("FTP connesso a " + host + ":" + port + (hasMlsd ? " (MLSD)" : " (LIST)"));
        }

        void Write(string line)
        {
            var b = Utf8.GetBytes(line + "\r\n");
            cs.Write(b, 0, b.Length);
        }

        Reply Read()
        {
            string first = rd.ReadLine();
            if (first == null) throw new IOException("FTP connection closed by the server");
            var sb = new StringBuilder(first);
            if (first.Length >= 4 && first[3] == '-')
            {
                string code = first.Substring(0, 3);
                while (true)
                {
                    string l = rd.ReadLine();
                    if (l == null) throw new IOException("FTP connection closed by the server");
                    sb.Append('\n').Append(l);
                    if (l.Length >= 4 && l.StartsWith(code) && l[3] == ' ') break;
                }
            }
            int c; int.TryParse(first.Length >= 3 ? first.Substring(0, 3) : "0", out c);
            return new Reply { Code = c, Text = sb.ToString() };
        }

        Reply Cmd(string line) { Write(line); return Read(); }

        // Runs an operation, reconnecting once when the control connection dropped.
        T Do<T>(Func<T> f)
        {
            if (cs == null) Connect();
            try { return f(); }
            catch (IOException) { Connect(); return f(); }
            catch (SocketException) { Connect(); return f(); }
        }

        void Guard(string what)
        {
            if (ReadOnly) throw new InvalidOperationException("Preview mode: FTP write blocked (" + what + ")");
        }

        bool Cwd(string path)
        {
            if (cwd == path) return true;
            var r = Cmd("CWD " + path);
            if (r.Code == 250 || r.Code == 200) { cwd = path; return true; }
            cwd = null;
            return false;
        }

        TcpClient OpenPasv()
        {
            var r = Cmd("PASV");
            if (r.Code != 227) throw new FtpException(r.Code, "PASV rejected: " + r.Text);
            var m = Regex.Match(r.Text, @"(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)");
            if (!m.Success) throw new FtpException(r.Code, "Invalid PASV reply: " + r.Text);
            string ip = m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value + "." + m.Groups[4].Value;
            int p = int.Parse(m.Groups[5].Value) * 256 + int.Parse(m.Groups[6].Value);
            if (ip == "0.0.0.0" || ip.StartsWith("127.")) ip = host;
            return ConnectTcp(ip, p, timeoutMs);
        }

        byte[] RetrData(string command)
        {
            using (var d = OpenPasv())
            {
                var r = Cmd(command);
                if (r.Code != 150 && r.Code != 125) throw new FtpException(r.Code, command.Split(' ')[0] + ": " + r.Text);
                var ms = new MemoryStream();
                using (var ds = d.GetStream()) ds.CopyTo(ms);
                d.Close();
                r = Read();
                if (r.Code != 226 && r.Code != 250) throw new FtpException(r.Code, "Transfer not completed: " + r.Text);
                return ms.ToArray();
            }
        }

        static string Parent(string path)
        {
            int i = path.TrimEnd('/').LastIndexOf('/');
            return i <= 0 ? "/" : path.Substring(0, i);
        }

        static string NameOf(string path)
        {
            string t = path.TrimEnd('/');
            return t.Substring(t.LastIndexOf('/') + 1);
        }

        // Lists a directory. Returns null when the directory does not exist.
        public List<RemoteEntry> ListDir(string path)
        {
            return Do(() =>
            {
                if (!Cwd(path)) return (List<RemoteEntry>)null;
                var data = RetrData(hasMlsd ? "MLSD" : "LIST");
                var text = Utf8.GetString(data);
                var res = new List<RemoteEntry>();
                foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = hasMlsd ? ParseMlsd(raw) : ParseList(raw);
                    if (e == null || e.Name == "." || e.Name == "..") continue;
                    res.Add(e);
                }
                return res;
            });
        }

        RemoteEntry ParseMlsd(string line)
        {
            int sp = line.IndexOf(' ');
            if (sp < 0) return null;
            var e = new RemoteEntry { Name = line.Substring(sp + 1) };
            foreach (var f in line.Substring(0, sp).Split(';'))
            {
                int eq = f.IndexOf('=');
                if (eq < 0) continue;
                string k = f.Substring(0, eq).ToLowerInvariant(), v = f.Substring(eq + 1);
                if (k == "type")
                {
                    string t = v.ToLowerInvariant();
                    if (t == "cdir" || t == "pdir") return null;
                    e.IsDir = t == "dir";
                }
                else if (k == "size") long.TryParse(v, out e.Size);
                else if (k == "modify")
                {
                    DateTime d;
                    if (DateTime.TryParseExact(v.Length > 14 ? v.Substring(0, 14) : v, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                        e.ModifiedUtc = DateTime.SpecifyKind(d, DateTimeKind.Utc).AddMinutes(ClockOffsetMinutes);
                }
            }
            return e;
        }

        static readonly Regex UnixList = new Regex(@"^([dl\-])\S+\s+\d+\s+\S+\s+\S+\s+(\d+)\s+([A-Za-z]{3})\s+(\d{1,2})\s+(\d{1,2}:\d{2}|\d{4})\s+(.+)$");

        RemoteEntry ParseList(string line)
        {
            var m = UnixList.Match(line);
            if (!m.Success) return null;
            var e = new RemoteEntry { IsDir = m.Groups[1].Value == "d", Name = m.Groups[6].Value };
            long.TryParse(m.Groups[2].Value, out e.Size);
            var inv = CultureInfo.InvariantCulture;
            string tm = m.Groups[5].Value;
            DateTime d;
            bool ok;
            int year = DateTime.UtcNow.Year;
            if (tm.Contains(":"))
                ok = DateTime.TryParseExact(m.Groups[3].Value + " " + m.Groups[4].Value + " " + tm + " " + year, "MMM d H:mm yyyy", inv, DateTimeStyles.None, out d);
            else
                ok = DateTime.TryParseExact(m.Groups[3].Value + " " + m.Groups[4].Value + " " + tm, "MMM d yyyy", inv, DateTimeStyles.None, out d);
            if (ok)
            {
                if (tm.Contains(":") && d > DateTime.UtcNow.AddDays(1)) d = d.AddYears(-1);
                DateTime u = ListTimesUtc ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime();
                e.ModifiedUtc = u.AddMinutes(ClockOffsetMinutes);
            }
            return e;
        }

        // Recursively lists files under root. Keys are paths relative to root with '/' separators.
        // Returns null when root does not exist.
        public Dictionary<string, RemoteEntry> Walk(string root, int maxDepth = 12)
        {
            var res = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
            var top = ListDir(root);
            if (top == null) return null;
            WalkInto(root, "", top, res, maxDepth);
            return res;
        }

        void WalkInto(string abs, string rel, List<RemoteEntry> entries, Dictionary<string, RemoteEntry> res, int depth)
        {
            foreach (var e in entries)
            {
                string r = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
                if (e.IsDir)
                {
                    if (depth <= 0) continue;
                    var sub = ListDir(abs.TrimEnd('/') + "/" + e.Name);
                    if (sub != null) WalkInto(abs.TrimEnd('/') + "/" + e.Name, r, sub, res, depth - 1);
                }
                else res[r] = e;
            }
        }

        public byte[] Download(string path)
        {
            return Do(() =>
            {
                string dir = Parent(path);
                if (!Cwd(dir)) throw new FtpException(550, "PS5 folder not found: " + dir);
                return RetrData("RETR " + NameOf(path));
            });
        }

        // Streams a file to 'output', starting at 'offset' (REST) when > 0. No automatic reconnect: the caller
        // retries (with Connect()) so a half-written output is never silently duplicated.
        public void DownloadTo(string path, Stream output, long offset, Action<long> progress)
        {
            if (cs == null) Connect();
            string dir = Parent(path);
            if (!Cwd(dir)) throw new FtpException(550, "PS5 folder not found: " + dir);
            using (var d = OpenPasv())
            {
                if (offset > 0)
                {
                    var rr = Cmd("REST " + offset);
                    if (rr.Code != 350) throw new FtpException(rr.Code, "REST not supported: " + rr.Text);
                }
                var r = Cmd("RETR " + NameOf(path));
                if (r.Code != 150 && r.Code != 125) throw new FtpException(r.Code, "RETR: " + r.Text);
                var buf = new byte[1 << 17];
                long n = 0;
                using (var ds = d.GetStream())
                {
                    int k;
                    while ((k = ds.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, k); n += k;
                        if (progress != null) progress(n);
                    }
                }
                d.Close();
                r = Read();
                if (r.Code != 226 && r.Code != 250) throw new FtpException(r.Code, "Transfer not completed: " + r.Text);
            }
        }

        public void Upload(string path, byte[] data)
        {
            Guard("STOR " + path);
            Do(() =>
            {
                string dir = Parent(path);
                if (!Cwd(dir)) throw new FtpException(550, "PS5 folder not found: " + dir);
                using (var d = OpenPasv())
                {
                    var r = Cmd("STOR " + NameOf(path));
                    if (r.Code != 150 && r.Code != 125) throw new FtpException(r.Code, "STOR: " + r.Text);
                    using (var ds = d.GetStream()) ds.Write(data, 0, data.Length);
                    d.Close();
                    r = Read();
                    if (r.Code != 226 && r.Code != 250) throw new FtpException(r.Code, "Upload not completed: " + r.Text);
                }
                return true;
            });
        }

        public void MakeDirs(string path)
        {
            Guard("MKD " + path);
            Do(() =>
            {
                string cur = "";
                foreach (var seg in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    cur += "/" + seg;
                    if (Cwd(cur)) continue;
                    var r = Cmd("MKD " + cur);
                    if (r.Code != 257 && r.Code != 250 && !Cwd(cur)) throw new FtpException(r.Code, "MKD " + cur + ": " + r.Text);
                }
                return true;
            });
        }
    }
}
