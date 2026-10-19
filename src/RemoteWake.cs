using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// Remote power-on of a PS5 in rest mode ("Wake PS5").
//
// Protocol (Sony Device Discovery Protocol + Remote Play registration), reimplemented from the public
// descriptions in playactor (ISC licence, dhleong/playactor), chiaki-ng (AGPL, behaviour only, no code) and
// PS5Upload (GPL-3.0, behaviour only, no code). The two PS5 key tables below are data taken from playactor
// (ISC, see THIRD_PARTY_NOTICES.txt). See RESEARCH_wake.md for the full write-up.
//
//  * SRCH   UDP 9302  "SRCH * HTTP/1.1\ndevice-discovery-protocol-version:00030010\n" + NUL
//           reply "HTTP/1.1 200 Ok" (awake) or "HTTP/1.1 620 Server Standby" (rest mode) + host-id, host-name...
//  * WAKEUP UDP 9302  "WAKEUP * HTTP/1.1\nclient-type:vr\nauth-type:R\nmodel:w\napp-type:r\n
//           user-credential:<decimal>\ndevice-discovery-protocol-version:00030010\n" + NUL  (no reply)
//  * user-credential = the PS5-RegistKey of a Remote Play registration: hex -> bytes -> ASCII text -> hex number.
//  * Registration: UDP "SRC3" -> "RES3" on 9295, then POST /sie/ps5/rp/sess/rgst on TCP 9295 with a 480 byte
//    preface + AES-128-CFB record "Client-Type: ...\r\nNp-AccountId: <base64 8 byte account id>\r\n";
//    key = f(PIN shown in Settings > System > Remote Play > Link Device).
// Wake-on-LAN magic packets are ignored by the PS5, so they are not used.
namespace RetroSync
{
    public class DdpStatus
    {
        public int Code;
        public string StatusText = "", HostId = "", HostName = "", HostType = "", SystemVersion = "", RunningApp = "", RunningTitleId = "";
        public bool Awake { get { return Code == 200; } }
        public bool Standby { get { return Code == 620; } }

        // "09600004" -> "9.60"
        public string Firmware
        {
            get
            {
                var v = SystemVersion ?? "";
                if (v.Length < 4) return v;
                int maj; if (!int.TryParse(v.Substring(0, 2), out maj)) return v;
                return maj + "." + v.Substring(2, 2);
            }
        }

        public static DdpStatus Parse(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var head = lines[0].Trim().Split(new[] { ' ' }, 3);
            int code;
            if (head.Length < 2 || !head[0].StartsWith("HTTP") || !int.TryParse(head[1], out code)) return null;
            var s = new DdpStatus { Code = code, StatusText = head.Length > 2 ? head[2] : "" };
            foreach (var raw in lines.Skip(1))
            {
                string l = raw.TrimEnd('\0').Trim();
                int i = l.IndexOf(':');
                if (i <= 0) continue;
                string k = l.Substring(0, i).Trim(), v = l.Substring(i + 1).Trim();
                switch (k)
                {
                    case "host-id": s.HostId = v; break;
                    case "host-name": s.HostName = v; break;
                    case "host-type": s.HostType = v; break;
                    case "system-version": s.SystemVersion = v; break;
                    case "running-app-name": s.RunningApp = v; break;
                    case "running-app-titleid": s.RunningTitleId = v; break;
                }
            }
            return s;
        }
    }

    static class Ddp
    {
        public const int Port = 9302;
        public const string Version = "00030010";
        // test hook (tests/WakeTests.cs)
#pragma warning disable 649
        public static int PortOverride;   // tests only
#pragma warning restore 649

        static IPEndPoint Ep(string host, int port)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host.Trim(), out ip))
            {
                ip = Dns.GetHostAddresses(host.Trim()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) throw new Exception("Invalid address: " + host);
            }
            return new IPEndPoint(ip, port);
        }

        static byte[] Packet(string text) { var b = Encoding.ASCII.GetBytes(text); Array.Resize(ref b, b.Length + 1); return b; }   // trailing NUL, like chiaki-ng

        public static byte[] SrchPacket() { return Packet("SRCH * HTTP/1.1\ndevice-discovery-protocol-version:" + Version + "\n"); }

        public static byte[] WakePacket(ulong credential)
        {
            return Packet("WAKEUP * HTTP/1.1\nclient-type:vr\nauth-type:R\nmodel:w\napp-type:r\nuser-credential:" + credential.ToString(CultureInfo.InvariantCulture)
                + "\ndevice-discovery-protocol-version:" + Version + "\n");
        }

        // Asks the console for its status. Returns null when nothing answered (Remote Play disabled, PS5 off,
        // rest mode without network features, wrong address).
        public static DdpStatus Probe(string host, int timeoutMs = 1200, int sends = 2)
        {
            var ep = Ep(host, PortOverride > 0 ? PortOverride : Port);
            using (var u = new UdpClient(0, AddressFamily.InterNetwork))
            {
                var pkt = SrchPacket();
                var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                int slice = Math.Max(150, timeoutMs / Math.Max(1, sends));
                for (int i = 0; i < sends; i++)
                {
                    try { u.Send(pkt, pkt.Length, ep); } catch (SocketException) { return null; }
                    var sliceEnd = DateTime.UtcNow.AddMilliseconds(slice);
                    if (sliceEnd > until) sliceEnd = until;
                    while (DateTime.UtcNow < sliceEnd)
                    {
                        int left = (int)(sliceEnd - DateTime.UtcNow).TotalMilliseconds;
                        if (left <= 0) break;
                        u.Client.ReceiveTimeout = Math.Max(1, left);
                        try
                        {
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            var data = u.Receive(ref from);
                            if (!from.Address.Equals(ep.Address)) continue;
                            var st = DdpStatus.Parse(Encoding.UTF8.GetString(data));
                            if (st != null) return st;
                        }
                        catch (SocketException) { break; }
                    }
                }
            }
            return null;
        }

        // Fire and forget: the console never acknowledges a WAKEUP. Sent 'repeat' times (UDP can drop one).
        public static void Wake(string host, ulong credential, int repeat = 3)
        {
            if (credential == 0) throw new Exception("Wake credential missing: configure remote wake in Settings.");
            var ep = Ep(host, PortOverride > 0 ? PortOverride : Port);
            var pkt = WakePacket(credential);
            using (var u = new UdpClient(0, AddressFamily.InterNetwork))
            {
                for (int i = 0; i < Math.Max(1, repeat); i++)
                {
                    u.Send(pkt, pkt.Length, ep);
                    if (i + 1 < repeat) Thread.Sleep(400);
                }
            }
        }
    }

    public class RpRegistration
    {
        public string RegistKey = "", RpKey = "", Mac = "", Nickname = "", ApName = "";
        public Dictionary<string, string> All = new Dictionary<string, string>();
    }

    static class RemotePlay
    {
        public const int RegistPort = 9295;
#pragma warning disable 649
        public static int RegistPortOverride;   // tests only
#pragma warning restore 649
        const string ClientType = "dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f";   // PS Remote Play for Windows
        static readonly byte[] HmacKeyPs5 = { 0x46, 0x46, 0x87, 0xb3, 0x49, 0xca, 0x8c, 0xe8, 0x59, 0xc5, 0x27, 0x0f, 0x5d, 0x7a, 0x69, 0xd6 };
        const int PaddingBytes = 480;

        // ---- credential helpers
        public static byte[] Hex(string s)
        {
            s = (s ?? "").Trim().Replace(" ", "");
            if (s.Length == 0 || s.Length % 2 != 0) return null;
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
                if (!byte.TryParse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return null;
            return b;
        }

        // PS5-RegistKey (hex) -> wake credential. The key bytes are ASCII text that is itself a hex number.
        public static ulong CredentialFromRegistKey(string registKeyHex)
        {
            var b = Hex(registKeyHex);
            if (b == null) throw new FormatException("The registration key must be hexadecimal.");
            int end = Array.IndexOf(b, (byte)0); if (end < 0) end = b.Length;
            string text = Encoding.ASCII.GetString(b, 0, end).Trim();
            ulong v;
            if (text.Length == 0 || text.Length > 16 || !ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) || v == 0)
                throw new FormatException("Invalid registration key (" + registKeyHex + ").");
            return v;
        }

        // Accepts the PSN account id either as base64 (as Remote Play uses it, 8 bytes) or as the decimal user id.
        public static string AccountIdToBase64(string input)
        {
            string t = (input ?? "").Trim();
            if (t.Length == 0) throw new FormatException("PSN Account ID missing.");
            ulong n;
            if (t.All(char.IsDigit) && ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                return Convert.ToBase64String(BitConverter.IsLittleEndian ? BitConverter.GetBytes(n) : BitConverter.GetBytes(n).Reverse().ToArray());
            byte[] b;
            try { b = Convert.FromBase64String(t); } catch { throw new FormatException("Invalid Account ID: use a number (e.g. 1234567890123456789) or base64 (12 characters)."); }
            if (b.Length != 8) throw new FormatException("L'Account ID base64 deve corrispondere a 8 byte (ne ha " + b.Length + ").");
            return t;
        }

        // ---- registration crypto (PS5 flavour)
        public static byte[] Seed(uint pin, int initKeyOff)
        {
            var seed = new byte[16];
            for (int i = 0; i < 16; i++) seed[i] = RpKeys.Ps5InitKey[(i * 0x20 + initKeyOff) % RpKeys.Ps5InitKey.Length];
            seed[0xc] ^= (byte)((pin >> 24) & 0xFF);
            seed[0xd] ^= (byte)((pin >> 16) & 0xFF);
            seed[0xe] ^= (byte)((pin >> 8) & 0xFF);
            seed[0xf] ^= (byte)(pin & 0xFF);
            return seed;
        }

        static byte[] Aeropause(byte[] nonce, byte[] padding)
        {
            int off = padding[0] >> 3;
            var a = new byte[16];
            for (int i = 0; i < 16; i++) a[i] = (byte)(((nonce[i] ^ RpKeys.Ps5AeroKey[i * 0x20 + off]) + 0xd3 + i) & 0xFF);
            return a;
        }

        public static byte[] Iv(byte[] nonce, ulong counter)
        {
            var c = BitConverter.GetBytes(counter); if (BitConverter.IsLittleEndian) Array.Reverse(c);
            using (var h = new HMACSHA256(HmacKeyPs5))
            {
                var buf = new byte[nonce.Length + 8];
                Buffer.BlockCopy(nonce, 0, buf, 0, nonce.Length); Buffer.BlockCopy(c, 0, buf, nonce.Length, 8);
                return h.ComputeHash(buf).Take(16).ToArray();
            }
        }

        // AES-128-CFB with 128 bit feedback, built on ECB so it behaves the same on every .NET / Mono version.
        public static byte[] Cfb(byte[] key, byte[] iv, byte[] data, bool encrypt)
        {
            var outp = new byte[data.Length];
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
                using (var enc = aes.CreateEncryptor())
                {
                    var reg = (byte[])iv.Clone(); var ks = new byte[16];
                    for (int pos = 0; pos < data.Length; pos += 16)
                    {
                        enc.TransformBlock(reg, 0, 16, ks, 0);
                        int n = Math.Min(16, data.Length - pos);
                        for (int i = 0; i < n; i++) outp[pos + i] = (byte)(data[pos + i] ^ ks[i]);
                        // next register = ciphertext block
                        var ct = encrypt ? outp : data;
                        if (n == 16) Buffer.BlockCopy(ct, pos, reg, 0, 16);
                    }
                }
            }
            return outp;
        }

        public static byte[] BuildBody(string accountIdB64, uint pin, byte[] nonce, out byte[] seed, out byte[] iv)
        {
            var padding = Enumerable.Repeat((byte)'A', PaddingBytes).ToArray();
            int initKeyOff = padding[0x18D] & 0x1F;
            seed = Seed(pin, initKeyOff);
            iv = Iv(nonce, 0);
            var aero = Aeropause(nonce, padding);
            Buffer.BlockCopy(aero, 8, padding, 0xc7, 8);
            Buffer.BlockCopy(aero, 0, padding, 0x191, 8);
            var record = Encoding.UTF8.GetBytes("Client-Type: " + ClientType + "\r\nNp-AccountId: " + accountIdB64 + "\r\n");
            var enc = Cfb(seed, iv, record, true);
            var body = new byte[padding.Length + enc.Length];
            Buffer.BlockCopy(padding, 0, body, 0, padding.Length); Buffer.BlockCopy(enc, 0, body, padding.Length, enc.Length);
            return body;
        }

        static string Reason(string code)
        {
            switch ((code ?? "").ToLowerInvariant())
            {
                case "80108b09": return "Wrong or expired PIN";
                case "80108b03": return "a previous pairing is still pending: try again";
                case "80108b02": return "the console rejected the PSN account (wrong Account ID?)";
                case "80108b10": return "Remote Play already in use";
                case "80108b15": return "Remote Play blocked on the console";
                case "80108b11": return "Remote Play version not accepted";
                default: return "the console rejected the pairing";
            }
        }

        static bool ResponseComplete(byte[] raw)
        {
            int split = -1;
            for (int i = 0; i + 3 < raw.Length; i++) if (raw[i] == 13 && raw[i + 1] == 10 && raw[i + 2] == 13 && raw[i + 3] == 10) { split = i; break; }
            if (split < 0) return false;
            foreach (var h in Encoding.ASCII.GetString(raw, 0, split).Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                int c = h.IndexOf(':');
                int len;
                if (c > 0 && h.Substring(0, c).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(h.Substring(c + 1).Trim(), out len))
                    return raw.Length - (split + 4) >= len;
            }
            return false;   // no Content-Length: read until the console closes the connection
        }

        // Pairs with the console (it must be awake, showing the PIN in Settings > System > Remote Play > Link Device).
        public static RpRegistration Register(string host, string accountId, string pinText, Action<string> log)
        {
            if (log == null) log = x => { };
            string account = AccountIdToBase64(accountId);
            string pinDigits = new string((pinText ?? "").Where(char.IsDigit).ToArray());
            uint pin;
            if (pinDigits.Length != 8 || !uint.TryParse(pinDigits, out pin)) throw new FormatException("The PIN must be 8 digits.");
            int port = RegistPortOverride > 0 ? RegistPortOverride : RegistPort;
            IPAddress ip;
            if (!IPAddress.TryParse(host.Trim(), out ip)) ip = Dns.GetHostAddresses(host.Trim()).First(a => a.AddressFamily == AddressFamily.InterNetwork);

            log("Announcing pairing (SRC3 \u2192 " + ip + ":" + port + ")\u2026");
            UdpClient u;
            try { u = new UdpClient(port, AddressFamily.InterNetwork); } catch { u = new UdpClient(0, AddressFamily.InterNetwork); }
            using (u)
            {
                var src = Encoding.ASCII.GetBytes("SRC3");
                u.Send(src, src.Length, new IPEndPoint(ip, port));
                u.Client.ReceiveTimeout = 8000;
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = u.Receive(ref from);
                    if (Encoding.ASCII.GetString(data).IndexOf("RES3", StringComparison.Ordinal) != 0) throw new Exception("risposta inattesa");
                }
                catch (Exception e)
                {
                    throw new Exception("PS5 is not responding on port " + port + " (" + e.Message + "). It must be on, with Remote Play enabled and the 'Link Device' screen open.");
                }
            }
            Thread.Sleep(100);

            var nonce = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
            byte[] seed, iv;
            var body = BuildBody(account, pin, nonce, out seed, out iv);
            log("Sending the pairing request\u2026");
            byte[] raw;
            using (var tcp = new TcpClient())
            {
                var ar = tcp.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(10000)) throw new Exception("Connection timed out to " + ip + ":" + port);
                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = 30000; tcp.SendTimeout = 10000;
                var s = tcp.GetStream();
                var head = Encoding.ASCII.GetBytes("POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\nHOST: " + ip + "\r\nUser-Agent: remoteplay Windows\r\nConnection: close\r\nContent-Length: "
                    + body.Length + "\r\nRP-Version: 1.0\r\n\r\n");
                s.Write(head, 0, head.Length); s.Write(body, 0, body.Length); s.Flush();
                // read the HTTP response: until Content-Length bytes of body arrived, or the console closes the socket
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[8192]; int n;
                    try
                    {
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ResponseComplete(ms.ToArray())) break;
                        }
                    }
                    catch (IOException) { if (ms.Length == 0) throw; }
                    raw = ms.ToArray();
                }
            }
            int split = -1;
            for (int i = 0; i + 3 < raw.Length; i++) if (raw[i] == 13 && raw[i + 1] == 10 && raw[i + 2] == 13 && raw[i + 3] == 10) { split = i; break; }
            if (split < 0) throw new Exception("Invalid reply from the PS5.");
            var headText = Encoding.ASCII.GetString(raw, 0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
            int code = 0;
            var st = headText[0].Split(' ');
            if (st.Length > 1) int.TryParse(st[1], out code);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headText.Skip(1)) { int i = h.IndexOf(':'); if (i > 0) headers[h.Substring(0, i).Trim()] = h.Substring(i + 1).Trim(); }
            if (code >= 300 || code == 0)
            {
                string reason; headers.TryGetValue("RP-Application-Reason", out reason);
                throw new Exception("Pairing rejected (HTTP " + code + "): " + Reason(reason) + (reason != null ? " [" + reason + "]" : ""));
            }
            var payload = raw.Skip(split + 4).ToArray();
            if (payload.Length == 0) throw new Exception("The PS5 accepted the request but sent no keys.");
            var plain = Encoding.UTF8.GetString(Cfb(seed, iv, payload, false));
            var r = new RpRegistration();
            foreach (var line in plain.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = line.IndexOf(':'); if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                r.All[k] = v;
                if (k == "PS5-RegistKey") r.RegistKey = v;
                else if (k == "RP-Key") r.RpKey = v;
                else if (k == "PS5-Mac") r.Mac = v;
                else if (k == "PS5-Nickname") r.Nickname = v;
                else if (k == "AP-Name") r.ApName = v;
            }
            if (r.RegistKey.Length == 0) throw new Exception("No key in the reply: usually the PIN is wrong or expired.");
            CredentialFromRegistKey(r.RegistKey);   // validates
            return r;
        }
    }

    // Power state of the PS5 as seen from the PC: FTP first (what RetroSync needs), then Remote Play discovery.
    enum Ps5Power { Unknown, FtpOnline, AwakeNoFtp, RestMode, Offline }

    class Ps5State
    {
        public Ps5Power Power;
        public int LatencyMs = -1;
        public DdpStatus Ddp;
        public DateTime Checked = DateTime.Now;
    }

    static class Ps5Probe
    {
        public static bool FtpAnswers(string host, int port, int timeoutMs, out int latencyMs)
        {
            latencyMs = -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var t = new TcpClient())
                {
                    var ar = t.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    t.EndConnect(ar);
                    latencyMs = (int)sw.ElapsedMilliseconds;
                    return true;
                }
            }
            catch { return false; }
        }

        public static Ps5State Check(AppSettings s, bool useDdp)
        {
            var r = new Ps5State();
            int lat;
            if (FtpAnswers(s.FtpHost, s.FtpPort, 1500, out lat)) { r.Power = Ps5Power.FtpOnline; r.LatencyMs = lat; }
            if (useDdp)
            {
                try { r.Ddp = Ddp.Probe(s.FtpHost, r.Power == Ps5Power.FtpOnline ? 600 : 1000, 2); } catch { }
            }
            if (r.Power != Ps5Power.FtpOnline)
                r.Power = r.Ddp == null ? Ps5Power.Offline : r.Ddp.Standby ? Ps5Power.RestMode : Ps5Power.AwakeNoFtp;
            return r;
        }

        // Sends WAKEUP (unless FTP already answers) and waits until the FTP port answers.
        public static bool WakeAndWait(AppSettings s, Action<string> status, Func<bool> cancelled)
        {
            int lat;
            if (FtpAnswers(s.FtpHost, s.FtpPort, 1500, out lat)) { status("The PS5 is already awake (FTP up)."); return true; }
            ulong cred = s.Wake.CredentialValue();
            status("Sending wake signal to " + s.FtpHost + "\u2026");
            Ddp.Wake(s.FtpHost, cred, 3);
            var start = DateTime.Now;
            int resend = 0;
            bool sawAwake = false;
            while ((DateTime.Now - start).TotalSeconds < s.Wake.WaitSeconds)
            {
                if (cancelled != null && cancelled()) return false;
                int el = (int)(DateTime.Now - start).TotalSeconds;
                if (FtpAnswers(s.FtpHost, s.FtpPort, 1500, out lat))
                {
                    status("FTP up after " + el + " s, waiting " + s.Wake.SettleSeconds + " s for the payload to be ready\u2026");
                    Thread.Sleep(Math.Max(0, s.Wake.SettleSeconds) * 1000);
                    return true;
                }
                DdpStatus d = null;
                try { d = Ddp.Probe(s.FtpHost, 800, 1); } catch { }
                if (d != null && d.Awake && !sawAwake) sawAwake = true;
                status((sawAwake ? "PS5 awake, attendo the server FTP on port " + s.FtpPort : d != null && d.Standby ? "PS5 still in rest mode" : "Waiting for the PS5")
                    + "\u2026 " + el + " / " + s.Wake.WaitSeconds + " s");
                // the console may drop the first datagram while entering rest mode: resend every 20 s while it still sleeps
                if (!sawAwake && el >= 20 * (resend + 1)) { resend++; try { Ddp.Wake(s.FtpHost, cred, 2); } catch { } }
                Thread.Sleep(1500);
            }
            return false;
        }
    }

    static class RpKeys
    {
        public static readonly byte[] Ps5InitKey = {
            0x24, 0xd8, 0xc2, 0x69, 0x4c, 0x67, 0x78, 0x71, 0xee, 0x31, 0xbd, 0x2b, 0x83, 0xb2, 0x1d, 0x61,
            0xc9, 0xa7, 0x8e, 0xed, 0x9a, 0xd3, 0x6a, 0x6b, 0x5c, 0xc8, 0x35, 0x79, 0xa7, 0x24, 0xe2, 0x17,
            0x06, 0x60, 0x2e, 0xdf, 0xf4, 0xdb, 0x27, 0x10, 0x55, 0xd9, 0xea, 0x16, 0x4e, 0x90, 0x0c, 0xbf,
            0x40, 0x6f, 0x54, 0xa5, 0x31, 0x70, 0x2d, 0x5d, 0x1e, 0x27, 0xdf, 0x37, 0x40, 0xba, 0x9d, 0x5d,
            0xff, 0xe1, 0x05, 0x70, 0x80, 0xd4, 0xb7, 0xc2, 0x96, 0x7f, 0x2f, 0x42, 0xeb, 0x5a, 0x08, 0xde,
            0xc1, 0xb5, 0x52, 0x15, 0xf6, 0xb5, 0xf2, 0xd9, 0x69, 0xa5, 0xc7, 0xc4, 0x7f, 0x46, 0x64, 0xa4,
            0xfd, 0x46, 0x98, 0xa7, 0xe1, 0x2a, 0x8e, 0x6f, 0xaf, 0x65, 0x42, 0x28, 0xb9, 0xc2, 0x6f, 0x3e,
            0xe3, 0xe4, 0x4e, 0xe4, 0x5b, 0x9d, 0x60, 0x10, 0xb8, 0x5a, 0xb0, 0x7d, 0x04, 0x0c, 0x4c, 0x24,
            0x78, 0xbd, 0xb8, 0xba, 0xdb, 0x8f, 0xe3, 0xa0, 0x75, 0x6d, 0x28, 0xc2, 0x33, 0x5b, 0x32, 0x83,
            0xdd, 0x51, 0xb0, 0xa5, 0x8d, 0x09, 0x66, 0xe4, 0x5c, 0xb8, 0x70, 0x0b, 0xe6, 0x82, 0x14, 0xb6,
            0xd2, 0xb0, 0xc2, 0xe0, 0x55, 0xf3, 0x84, 0xad, 0x9d, 0x3a, 0xf8, 0x77, 0xf5, 0x9d, 0x9a, 0xa9,
            0x7d, 0xf1, 0x45, 0x1b, 0x9b, 0x55, 0x25, 0xd8, 0xc1, 0xff, 0x03, 0xa5, 0x48, 0x0b, 0x1b, 0x19,
            0x0c, 0xbd, 0xe0, 0xcd, 0x48, 0xf3, 0x2c, 0x99, 0x19, 0xd6, 0xb8, 0xbb, 0xd6, 0x35, 0x43, 0x6f,
            0x71, 0xe3, 0xef, 0x3e, 0x97, 0xb8, 0xe9, 0x40, 0xa8, 0x47, 0xe0, 0xe0, 0x01, 0x16, 0x9d, 0xa7,
            0xe5, 0x94, 0x4b, 0x1d, 0xd2, 0x80, 0xa2, 0x7f, 0xf2, 0x98, 0x10, 0x38, 0x0d, 0xb8, 0x56, 0xc3,
            0x7a, 0x4b, 0x4c, 0x85, 0xec, 0x2f, 0x23, 0x89, 0xaf, 0xd5, 0xba, 0x9a, 0xad, 0xb0, 0x61, 0x9c,
            0x51, 0xb4, 0x6d, 0x02, 0x49, 0x26, 0xa4, 0x34, 0x84, 0x20, 0x35, 0x30, 0x23, 0x0a, 0x47, 0x14,
            0x32, 0x1a, 0x96, 0x0e, 0xe8, 0x0f, 0x96, 0x96, 0xd4, 0xba, 0x68, 0x3a, 0x67, 0x15, 0x74, 0xe0,
            0xd6, 0x60, 0x4c, 0x68, 0x50, 0x73, 0x14, 0x2f, 0x11, 0x59, 0xac, 0xc8, 0x32, 0xd1, 0xdb, 0x4c,
            0x8a, 0x94, 0x75, 0x33, 0x61, 0xd1, 0xd4, 0xfd, 0xaa, 0x6a, 0x61, 0x68, 0xd8, 0xae, 0x31, 0x4f,
            0xb8, 0x07, 0x7b, 0x27, 0x0f, 0xf9, 0x0b, 0xb0, 0xc2, 0x64, 0xb3, 0x72, 0xea, 0x8b, 0x87, 0x40,
            0x09, 0xb4, 0x82, 0xb4, 0xad, 0x76, 0xf9, 0x36, 0x05, 0x60, 0x89, 0xc8, 0x20, 0xeb, 0xa5, 0xf1,
            0x51, 0x0b, 0x27, 0xa7, 0xf0, 0x76, 0x84, 0x96, 0xeb, 0xb1, 0x2e, 0xc2, 0x85, 0x28, 0xbc, 0x48,
            0x34, 0xd4, 0x01, 0x8d, 0x5b, 0x25, 0x54, 0xe0, 0xc4, 0x4f, 0xa0, 0xfa, 0x99, 0x8d, 0x6d, 0x7a,
            0x64, 0xb1, 0xa9, 0x5d, 0xa4, 0xf9, 0xf5, 0x22, 0xeb, 0x9a, 0xf4, 0xa8, 0x7a, 0x78, 0x4b, 0x7f,
            0xe2, 0x8b, 0x04, 0x50, 0x43, 0x7d, 0x26, 0x2d, 0x19, 0x98, 0x38, 0x6a, 0x4f, 0x2d, 0x30, 0x15,
            0x2e, 0x4f, 0xcd, 0xb9, 0xce, 0x9e, 0x8d, 0x12, 0xc9, 0xfe, 0x33, 0x8b, 0x84, 0xce, 0x5b, 0x40,
            0xe3, 0x7f, 0x72, 0x6d, 0x6c, 0x8a, 0x6a, 0x9e, 0x54, 0xf1, 0xe3, 0x64, 0x5d, 0x6e, 0x7f, 0xac,
            0x1a, 0xe7, 0xf7, 0xfa, 0x00, 0x22, 0xed, 0x2b, 0x23, 0xfa, 0x58, 0xc5, 0xeb, 0x44, 0x92, 0x5d,
            0xcc, 0xaa, 0x82, 0x9f, 0x23, 0xfb, 0xa6, 0xc9, 0x65, 0x2a, 0xe0, 0x79, 0x12, 0x65, 0x2c, 0x34,
            0xc5, 0x23, 0x16, 0xc9, 0xcc, 0x05, 0x30, 0xf3, 0x96, 0x0b, 0x90, 0x67, 0x1a, 0xa7, 0x69, 0x4c,
            0x3e, 0x43, 0x24, 0x9d, 0x4e, 0x68, 0xbd, 0x8b, 0x75, 0x6e, 0x9d, 0x07, 0x6f, 0x1a, 0x6a, 0xba,
        };

        public static readonly byte[] Ps5AeroKey = {
            0x79, 0x4d, 0x78, 0x30, 0xfe, 0x10, 0x52, 0x4c, 0xa8, 0x90, 0x5b, 0x9a, 0x7e, 0x5f, 0xd3, 0xe1,
            0x13, 0xe0, 0xf1, 0x0f, 0xa3, 0xe7, 0xbb, 0x45, 0x7f, 0xdc, 0x8e, 0xd5, 0xf1, 0x04, 0x5c, 0x78,
            0x51, 0xef, 0xf8, 0x65, 0x59, 0x03, 0x39, 0x84, 0x37, 0xae, 0x59, 0xdf, 0x23, 0xb6, 0x60, 0x34,
            0xe6, 0x4b, 0xe2, 0xf5, 0x4c, 0x13, 0xc6, 0xda, 0xf9, 0xfd, 0xb3, 0x65, 0x84, 0xd6, 0x45, 0xec,
            0x2c, 0x00, 0xf2, 0xed, 0xdc, 0xcb, 0x93, 0x6e, 0x61, 0x46, 0xe5, 0xd6, 0x01, 0x94, 0xee, 0x78,
            0x85, 0x0e, 0x68, 0x5e, 0xb5, 0x5b, 0xcd, 0xd3, 0x63, 0x41, 0xfc, 0x81, 0x43, 0x1c, 0x6f, 0x7c,
            0xba, 0xe8, 0xbd, 0x86, 0x31, 0xd5, 0x70, 0x7f, 0xb5, 0x4a, 0x90, 0x3e, 0x84, 0xe1, 0x71, 0xe0,
            0x02, 0x99, 0xf4, 0x71, 0xe7, 0x02, 0xed, 0x36, 0xaf, 0xde, 0x56, 0xc2, 0x90, 0xe0, 0xae, 0xc2,
            0xf9, 0xaf, 0x53, 0xc6, 0xd8, 0x62, 0x16, 0x32, 0x27, 0xfb, 0x6e, 0x9b, 0x48, 0xc6, 0xea, 0xff,
            0x6f, 0x78, 0x02, 0x22, 0x98, 0x2c, 0x1f, 0xbf, 0xb0, 0x8e, 0xa9, 0x39, 0xbc, 0xdf, 0x17, 0xde,
            0xd7, 0x0e, 0xe1, 0x7a, 0x01, 0x0e, 0xc3, 0x87, 0xfc, 0xaa, 0xe4, 0x6b, 0x0f, 0x5b, 0x0a, 0xf1,
            0x18, 0x19, 0x8a, 0xe5, 0x2c, 0x36, 0x9b, 0x40, 0x30, 0x99, 0x24, 0x94, 0x48, 0xd7, 0x47, 0xb2,
            0xaf, 0x6b, 0x8c, 0x40, 0x9e, 0x4d, 0x6d, 0x34, 0x07, 0xc1, 0x26, 0x2f, 0xbb, 0x14, 0xf7, 0xbc,
            0x36, 0x52, 0xbd, 0x84, 0xfe, 0x4a, 0x9a, 0xf4, 0x8a, 0xdb, 0x34, 0x89, 0xaa, 0xf1, 0x0d, 0x94,
            0x0b, 0x92, 0xf4, 0x1c, 0xe4, 0x6c, 0x79, 0x2d, 0x6e, 0xc0, 0x19, 0x0a, 0xd5, 0x55, 0x94, 0x14,
            0x05, 0x13, 0xc2, 0x62, 0x23, 0xb3, 0xd4, 0x26, 0xc4, 0x44, 0x56, 0x7a, 0xcd, 0x1c, 0xea, 0xd4,
            0x74, 0xb9, 0x36, 0x40, 0x9f, 0x08, 0xfb, 0x49, 0x62, 0x05, 0x92, 0x98, 0xad, 0x1d, 0x9f, 0x8a,
            0x76, 0x8b, 0xd4, 0x0f, 0x21, 0x40, 0x76, 0xb6, 0x16, 0x91, 0x45, 0x93, 0x66, 0xcc, 0x12, 0xea,
            0x4d, 0xf4, 0x09, 0xe2, 0xac, 0x33, 0xd0, 0x6f, 0x43, 0x51, 0x07, 0x3e, 0xd7, 0x95, 0x2c, 0x1e,
            0x1f, 0x0c, 0x24, 0xb3, 0x0e, 0x3a, 0xef, 0x95, 0xf5, 0xeb, 0x77, 0xdd, 0x20, 0xf2, 0x35, 0x98,
            0xf2, 0xae, 0xa9, 0x66, 0xe6, 0x13, 0xef, 0x5d, 0x3a, 0x2d, 0x66, 0xed, 0xe2, 0x1e, 0xe9, 0x32,
            0x4a, 0x40, 0xbf, 0x37, 0xc6, 0x70, 0x29, 0xd9, 0x8c, 0xa1, 0x61, 0x4a, 0x29, 0x3d, 0xc7, 0x55,
            0x9c, 0x94, 0x9e, 0xc9, 0x11, 0x45, 0x10, 0x28, 0xa7, 0x27, 0xd1, 0xd3, 0xd0, 0x84, 0x79, 0xc7,
            0xa9, 0xb0, 0xf6, 0xaf, 0x45, 0x8c, 0x3c, 0xd4, 0xdf, 0x3b, 0xf7, 0x0d, 0xa2, 0x4f, 0x13, 0x97,
            0x78, 0x27, 0xf0, 0x48, 0xc0, 0xa5, 0xab, 0x83, 0x01, 0x05, 0xd0, 0x12, 0xd7, 0x1e, 0x12, 0x3a,
            0x4e, 0x98, 0x77, 0xae, 0xba, 0xb1, 0x4e, 0xb5, 0x3b, 0x59, 0xca, 0x6d, 0xa5, 0x11, 0x80, 0x91,
            0x9c, 0x07, 0x69, 0x59, 0x5a, 0x53, 0x70, 0x7c, 0x95, 0x97, 0x11, 0x6d, 0x66, 0x8d, 0xa3, 0xbd,
            0xbb, 0x2d, 0xb0, 0xbf, 0x9b, 0x10, 0xcb, 0xc7, 0x0f, 0x5b, 0x7e, 0x67, 0xe2, 0xb0, 0x4b, 0xba,
            0x10, 0x12, 0xb9, 0xbc, 0x97, 0xfd, 0x48, 0xe4, 0x8a, 0xc1, 0x0f, 0xa1, 0x30, 0x9d, 0x56, 0x20,
            0x24, 0x1a, 0x7d, 0x5b, 0xa0, 0xb4, 0xbe, 0x9d, 0x38, 0x4f, 0xb4, 0x56, 0xa8, 0x4d, 0x13, 0x7c,
            0x44, 0xe8, 0x84, 0x97, 0xeb, 0x78, 0x2c, 0x52, 0x85, 0xe4, 0xa2, 0xf6, 0xf3, 0xd9, 0x71, 0x9e,
            0xee, 0xb8, 0x11, 0x47, 0xfb, 0xa9, 0x1b, 0xc7, 0x40, 0xc6, 0xe1, 0x19, 0x6d, 0x50, 0xa1, 0x2a,
        };

    }
}
