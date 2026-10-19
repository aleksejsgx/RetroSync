// RetroSync 2.1 - automatic pairing with the user's own PS5 running homebrew.
//
// Flow ("Pair PS5"):
//   1. take the small helper payload embedded in RetroSync.exe (retrosync-pair.elf, source in payload/pair.c, MIT),
//      write a fresh random one-time token into it and send it to the ELF loader on <PS5 IP>:9021;
//   2. connect to the helper on TCP 9303, send "RETROSYNC-PAIR 1 <token>", read one JSON line
//      {ok, account_id (base64), pin, rp_enabled};
//   3. at once run the normal Remote Play registration on 9295 (RemotePlay.Register) with that account and PIN.
//      Nothing is polled in between: asking the console for the registration status first would finalise the
//      pending PIN and the console would then refuse the registration (HTTP 403, 80108b03).
// Written from scratch for RetroSync (behaviour studied in PS5Upload, GPL - no code copied).
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace RetroSync
{
    public class PairInfo
    {
        public string AccountId = "";   // base64 of the 8-byte little-endian account id
        public string Pin = "";
        public int RpEnabled = -1;      // registry flag read by the helper: 1 on, 0 off, -1 unknown
    }

    static class PairHelper
    {
        public const string ResourceName = "retrosync-pair.elf";
        public const string Marker = "RSYNC-TOKEN-SLOT:";
        public const int TokenLength = 32;
        public static int LoaderPort = 9021;          // ELF loader already running on the PS5
        public static int HelperPort = 9303;          // where the helper listens
        public static int HelperWaitMs = 30000;       // how long we try to reach the helper after sending it
        public static Func<byte[]> ElfOverride;       // tests only

        public static byte[] LoadElf()
        {
            if (ElfOverride != null) return ElfOverride();
            using (var st = typeof(PairHelper).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (st != null) { var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray(); }
            }
            string side = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ResourceName);
            if (File.Exists(side)) return File.ReadAllBytes(side);
            throw new Exception("The pairing payload (" + ResourceName + ") is not embedded in this RetroSync.exe.");
        }

        public static string NewToken()
        {
            var b = new byte[TokenLength / 2];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")));
        }

        // Returns a copy of the ELF with the token written after the marker (exactly one marker must exist).
        public static byte[] PatchToken(byte[] elf, string token)
        {
            if (token == null || token.Length != TokenLength || !token.All(Uri.IsHexDigit)) throw new ArgumentException("token");
            var m = Encoding.ASCII.GetBytes(Marker);
            int at = -1, count = 0;
            for (int i = 0; i + m.Length + TokenLength <= elf.Length; i++)
            {
                int j = 0; while (j < m.Length && elf[i + j] == m[j]) j++;
                if (j == m.Length) { if (count++ == 0) at = i; }
            }
            if (count != 1) throw new Exception("Invalid pairing payload (token placeholder " + (count == 0 ? "missing" : "duplicated") + ").");
            var copy = (byte[])elf.Clone();
            Buffer.BlockCopy(Encoding.ASCII.GetBytes(token), 0, copy, at + m.Length, TokenLength);
            return copy;
        }

        static TcpClient Connect(string host, int port, int timeoutMs)
        {
            var tcp = new TcpClient();
            try
            {
                var ar = tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException();
                tcp.EndConnect(ar);
                return tcp;
            }
            catch { tcp.Close(); throw; }
        }

        public static void SendElf(string host, byte[] elf)
        {
            try
            {
                using (var tcp = Connect(host, LoaderPort, 5000))
                {
                    tcp.SendTimeout = 15000;
                    var s = tcp.GetStream();
                    s.Write(elf, 0, elf.Length); s.Flush();
                    tcp.Client.Shutdown(SocketShutdown.Send);
                }
            }
            catch (Exception e)
            {
                throw new Exception("Could not send the payload to the PS5 (" + host + ":" + LoaderPort + "): " + e.Message +
                    ". Start the jailbreak with the ELF loader first (port " + LoaderPort + ") and check the IP.");
            }
        }

        public static PairInfo Query(string host, string token, Action<string> log)
        {
            var end = DateTime.UtcNow.AddMilliseconds(HelperWaitMs);
            Exception last = null;
            while (DateTime.UtcNow < end)
            {
                TcpClient tcp;
                try { tcp = Connect(host, HelperPort, 2000); }
                catch (Exception e) { last = e; Thread.Sleep(500); continue; }
                using (tcp)
                {
                    tcp.ReceiveTimeout = 15000; tcp.SendTimeout = 5000;
                    var s = tcp.GetStream();
                    var line = Encoding.ASCII.GetBytes("RETROSYNC-PAIR 1 " + token + "\n");
                    s.Write(line, 0, line.Length); s.Flush();
                    var ms = new MemoryStream(); int b;
                    while (ms.Length < 4096 && (b = s.ReadByte()) >= 0 && b != '\n') ms.WriteByte((byte)b);
                    return Parse(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            throw new Exception("The pairing payload is not answering on port " + HelperPort + " (" + (last != null ? last.Message : "timeout") +
                "). Check that the PS5 showed the notification \u201CRetroSync\u201D.");
        }

        public static PairInfo Parse(string json)
        {
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); } catch { d = null; }
            if (d == null) throw new Exception("Invalid reply from the payload.");
            Func<string, string> str = k => { object o; return d.TryGetValue(k, out o) && o != null ? Convert.ToString(o) : ""; };
            var info = new PairInfo { AccountId = str("account_id"), Pin = str("pin") };
            int rp; if (int.TryParse(str("rp_enabled"), out rp)) info.RpEnabled = rp;
            if (!string.Equals(str("ok"), "true", StringComparison.OrdinalIgnoreCase))
            {
                string e = str("error");
                string msg = e == "auth" ? "the payload rejected the token (was it started from somewhere else?)"
                    : e == "no signed-in user" ? "no user is signed in on the PS5: sign in with your profile and try again"
                    : e.StartsWith("account id") ? "could not read the user's PSN Account ID (" + e + ")"
                    : e.Contains("Remoteplay") || e.Contains("sceRemoteplay") ? "the PS5 did not generate a Remote Play PIN (" + e + ")"
                    : e;
                if (info.RpEnabled == 0) msg += ". Remote Play appears disabled: enable it in Settings \u203A System \u203A Remote Play";
                throw new Exception("Pairing failed: " + msg + ".");
            }
            byte[] acc = null;
            try { acc = Convert.FromBase64String(info.AccountId); } catch { }
            if (acc == null || acc.Length != 8 || acc.All(x => x == 0)) throw new Exception("The payload sent an invalid Account ID.");
            if (info.Pin.Length != 8 || !info.Pin.All(char.IsDigit)) throw new Exception("The payload sent an invalid PIN.");
            return info;
        }

        // Full automatic pairing. Returns the registration; account id (base64) through accountId.
        public static RpRegistration Pair(string host, Action<string> log, out string accountId)
        {
            if (log == null) log = x => { };
            host = (host ?? "").Trim();
            if (host.Length == 0) throw new Exception("Enter the PS5 IP address.");
            string token = NewToken();
            var elf = PatchToken(LoadElf(), token);
            log("Sending the pairing payload to " + host + ":" + LoaderPort + " (" + elf.Length / 1024 + " KB)\u2026");
            SendElf(host, elf);
            log("Waiting for the payload on port " + HelperPort + "\u2026");
            var info = Query(host, token, log);
            log("Got Account ID and PIN from the PS5" + (info.RpEnabled == 0 ? " (warning: Remote Play appears disabled)" : "") + ". Registering\u2026");
            accountId = info.AccountId;
            try { return RemotePlay.Register(host, info.AccountId, info.Pin, log); }
            catch (Exception e)
            {
                if (info.RpEnabled == 0) throw new Exception(e.Message + " Remote Play appears disabled on the PS5: enable it in Settings \u203A System \u203A Remote Play and try again.");
                throw;
            }
        }
    }
}
