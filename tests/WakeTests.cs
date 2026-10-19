using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Offline tests for the remote power-on code: known vectors from playactor's test suite, a byte-for-byte
// comparison with a Node reference (tests/playactor_ref.js) and a fake console on 127.0.0.1.
namespace RetroSync
{
    static class WakeTests
    {
        static int fails;
        static void Check(string name, bool ok, string info = "") { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (info.Length > 0 ? "  " + info : "")); if (!ok) fails++; }
        static string H(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

        static int Main(string[] args)
        {
            // args: refSeed refIv refBody refReply refAccountB64(from decimal)
            Check("seed vector (playactor generateSeed PS5, pin 78703893, off 0x1e)", H(RemotePlay.Seed(78703893, 0x1e)) == "e29d644c141b9d617431a56d34cfc17f");
            Check("iv vector (playactor generateIv PS5_1, counter 0)", H(RemotePlay.Iv(RemotePlay.Hex("3e7e7a825973adab2f694346bd44dab5"), 0)) == "9044408273f8044dca767b5a16394d64");
            byte[] seed, iv;
            var body = RemotePlay.BuildBody("FWbzJ0XoRBE=", 12345678, RemotePlay.Hex("3e7e7a825973adab2f694346bd44dab5"), out seed, out iv);
            Check("seed == node reference", H(seed) == args[0]);
            Check("iv == node reference", H(iv) == args[1]);
            Check("registration body == node reference (" + body.Length + " bytes)", H(body) == args[2]);
            var reply = Encoding.UTF8.GetString(RemotePlay.Cfb(seed, iv, RemotePlay.Hex(args[3]), false));
            Check("reply decryption", reply.Contains("PS5-RegistKey: 3132333435363738") && reply.Contains("PS5-Nickname: PS5-TEST"));
            Check("credential from RegistKey", RemotePlay.CredentialFromRegistKey("3132333435363738") == 0x12345678UL, RemotePlay.CredentialFromRegistKey("3132333435363738").ToString());
            Check("account id decimal -> base64 == node writeBigUInt64LE", RemotePlay.AccountIdToBase64("1234567890123456789") == args[4], RemotePlay.AccountIdToBase64("1234567890123456789"));
            var wp = Encoding.ASCII.GetString(Ddp.WakePacket(305419896));
            Check("WAKEUP packet format", wp == "WAKEUP * HTTP/1.1\nclient-type:vr\nauth-type:R\nmodel:w\napp-type:r\nuser-credential:305419896\ndevice-discovery-protocol-version:00030010\n\0");
            var st = DdpStatus.Parse("HTTP/1.1 620 Server Standby\nhost-id:A1B2C3D4E5F6\nhost-type:PS5\nhost-name:PS5-TEST\nhost-request-port:997\ndevice-discovery-protocol-version:00030010\nsystem-version:09600004\n");
            Check("DDP parse standby", st != null && st.Standby && st.Firmware == "9.60" && st.HostName == "PS5-TEST", st == null ? "null" : st.Code + " " + st.Firmware);

            FakeDdp();
            FakeRegistration();
            FakePairing();
            FakeRest();
            FakeWakeAndWait();
            Console.WriteLine(fails == 0 ? "ALL TESTS PASSED" : fails + " TEST(S) FAILED");
            return fails == 0 ? 0 : 1;
        }

        static void FakeDdp()
        {
            Ddp.PortOverride = 19302;
            var got = new List<string>();
            var srv = new UdpClient(new IPEndPoint(IPAddress.Loopback, 19302));
            bool awake = false;
            var t = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var d = srv.Receive(ref from);
                        var s = Encoding.ASCII.GetString(d);
                        lock (got) got.Add(s);
                        if (s.StartsWith("SRCH"))
                        {
                            var r = Encoding.ASCII.GetBytes((awake ? "HTTP/1.1 200 Ok\n" : "HTTP/1.1 620 Server Standby\n") + "host-id:A1B2C3D4E5F6\nhost-type:PS5\nhost-name:PS5-TEST\nsystem-version:09600004\ndevice-discovery-protocol-version:00030010\n");
                            srv.Send(r, r.Length, from);
                        }
                        else if (s.StartsWith("WAKEUP")) awake = true;
                    }
                }
                catch { }
            }) { IsBackground = true };
            t.Start();
            var p1 = Ddp.Probe("127.0.0.1", 800, 2);
            Check("fake console: probe in rest mode", p1 != null && p1.Standby);
            Ddp.Wake("127.0.0.1", 305419896, 2);
            Thread.Sleep(200);
            lock (got) Check("fake console: WAKEUP received twice with credential", got.Count(x => x.StartsWith("WAKEUP") && x.Contains("user-credential:305419896")) == 2);
            var p2 = Ddp.Probe("127.0.0.1", 800, 2);
            Check("fake console: awake after WAKEUP", p2 != null && p2.Awake);
            Ddp.PortOverride = 0;
            srv.Close();
        }

        // "Accendi e sincronizza": WAKEUP, then the payload's FTP server comes up a few seconds later
        static void FakeWakeAndWait()
        {
            Ddp.PortOverride = 19303;
            var srv = new UdpClient(new IPEndPoint(IPAddress.Loopback, 19303));
            TcpListener ftp = null; bool armed = false;
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var s0 = Encoding.ASCII.GetString(srv.Receive(ref from));
                        if (s0.StartsWith("WAKEUP") && !armed && (armed = true))
                            new Thread(() => { Thread.Sleep(3000); ftp = new TcpListener(IPAddress.Loopback, 12121); ftp.Start(); }) { IsBackground = true }.Start();
                        if (s0.StartsWith("SRCH")) { var r = Encoding.ASCII.GetBytes(ftp == null ? "HTTP/1.1 620 Server Standby\nhost-type:PS5\n" : "HTTP/1.1 200 Ok\nhost-type:PS5\n"); srv.Send(r, r.Length, from); }
                    }
                }
                catch { }
            }) { IsBackground = true }.Start();
            var s = new AppSettings(); s.FtpHost = "127.0.0.1"; s.FtpPort = 12121;
            s.Wake = new WakeSettings { Credential = "305419896", WaitSeconds = 20, SettleSeconds = 0 };
            var lines = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = Ps5Probe.WakeAndWait(s, x => lines.Add(x), () => false);
            Check("wake and wait: FTP detected after power-on", ok && sw.ElapsedMilliseconds < 9000, sw.ElapsedMilliseconds + " ms, " + lines.Count + " status lines, last: " + lines.LastOrDefault());
            var s2 = new AppSettings(); s2.FtpHost = "127.0.0.1"; s2.FtpPort = 12122;
            s2.Wake = new WakeSettings { Credential = "305419896", WaitSeconds = 30, SettleSeconds = 0 };
            int calls = 0; sw.Restart();
            bool ok2 = Ps5Probe.WakeAndWait(s2, x => { }, () => ++calls > 2);
            Check("wake and wait: cancel stops waiting", !ok2 && sw.ElapsedMilliseconds < 8000, sw.ElapsedMilliseconds + " ms");
            if (ftp != null) ftp.Stop();
            srv.Close();
            Ddp.PortOverride = 0;
        }

        // ---------------------------------------------------------------- automatic pairing ("Associa PS5")
        // Fake PS5: an ELF loader on 127.0.0.3:19021 that receives the helper and, like the real helper, then listens
        // on 19303 for the token found in the ELF; and a fake Remote Play console on 19297 that decrypts with the PIN.
        static void FakeConsole(IPAddress con, int port, uint pin, Action<string> onAccount, List<string> order)
        {
            var udp = new UdpClient(new IPEndPoint(con, port));
            var tl = new TcpListener(con, port); tl.Start();
            new Thread(() => { try { var from = new IPEndPoint(IPAddress.Any, 0); udp.Receive(ref from); lock (order) order.Add("console:SRC3"); var r = Encoding.ASCII.GetBytes("RES3"); udp.Send(r, r.Length, from); } catch { } finally { Thread.Sleep(200); udp.Close(); } }) { IsBackground = true }.Start();
            new Thread(() =>
            {
                try
                {
                    using (var c = tl.AcceptTcpClient())
                    {
                        lock (order) order.Add("console:rgst");
                        var s = c.GetStream(); var ms = new MemoryStream(); var buf = new byte[4096]; int split = -1, len = -1;
                        while (true)
                        {
                            int n = s.Read(buf, 0, buf.Length); if (n <= 0) break; ms.Write(buf, 0, n);
                            var txt = Encoding.ASCII.GetString(ms.ToArray()); split = txt.IndexOf("\r\n\r\n"); if (split < 0) continue;
                            foreach (var h in txt.Substring(0, split).Split(new[] { "\r\n" }, StringSplitOptions.None)) if (h.StartsWith("Content-Length:")) len = int.Parse(h.Substring(15).Trim());
                            if (ms.Length - split - 4 >= len) break;
                        }
                        var body = ms.ToArray().Skip(split + 4).ToArray(); var pad = body.Take(480).ToArray();
                        var aero = new byte[16]; Buffer.BlockCopy(pad, 0xc7, aero, 8, 8); Buffer.BlockCopy(pad, 0x191, aero, 0, 8);
                        int off = pad[0] >> 3; var nonce = new byte[16];
                        for (int i = 0; i < 16; i++) nonce[i] = (byte)(((aero[i] - 0xd3 - i) & 0xff) ^ RpKeys.Ps5AeroKey[i * 0x20 + off]);
                        var seed = RemotePlay.Seed(pin, pad[0x18D] & 0x1F); var iv = RemotePlay.Iv(nonce, 0);
                        var rec = Encoding.UTF8.GetString(RemotePlay.Cfb(seed, iv, body.Skip(480).ToArray(), false));
                        string account = null;
                        foreach (var l in rec.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) if (l.StartsWith("Np-AccountId: ")) account = l.Substring(14);
                        onAccount(account);
                        byte[] resp, payload = new byte[0];
                        if (account == null) resp = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108b09\r\nContent-Length: 0\r\n\r\n");
                        else
                        {
                            payload = RemotePlay.Cfb(seed, iv, Encoding.UTF8.GetBytes("AP-Name: PS5-AUTO\r\nPS5-Mac: 0a0b0c0d0e0f\r\nPS5-RegistKey: 3132333435363738\r\nRP-Key: 00112233445566778899aabbccddeeff\r\nPS5-Nickname: PS5-AUTO\r\n"), true);
                            resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\n\r\n");
                        }
                        s.Write(resp, 0, resp.Length); s.Write(payload, 0, payload.Length); s.Flush(); Thread.Sleep(500);
                    }
                }
                catch (Exception e) { Console.WriteLine("fake console: " + e.Message); }
                finally { tl.Stop(); }
            }) { IsBackground = true }.Start();
        }

        // Fake loader + helper. replyFor(token) gives the JSON the helper sends to a client presenting that token.
        static void FakeLoader(IPAddress con, List<string> order, Func<string, string, string> reply, int helperDelayMs)
        {
            var loader = new TcpListener(con, 19021); loader.Start();
            new Thread(() =>
            {
                try
                {
                    byte[] elf;
                    using (var c = loader.AcceptTcpClient()) { var ms = new MemoryStream(); c.GetStream().CopyTo(ms); elf = ms.ToArray(); }
                    loader.Stop();
                    string txt = Encoding.ASCII.GetString(elf);
                    int at = txt.IndexOf(PairHelper.Marker);
                    string token = at < 0 ? "" : txt.Substring(at + PairHelper.Marker.Length, PairHelper.TokenLength);
                    lock (order) order.Add("loader:elf " + elf.Length + " token " + token);
                    Thread.Sleep(helperDelayMs);   // the helper needs a moment to start: the client must retry
                    var hl = new TcpListener(con, 19303); hl.Start();
                    lock (order) order.Add("helper:listening");
                    var end = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < end)
                    {
                        if (!hl.Pending()) { Thread.Sleep(20); continue; }
                        using (var c = hl.AcceptTcpClient())
                        {
                            var r = new StreamReader(c.GetStream()); string line = r.ReadLine() ?? "";
                            lock (order) order.Add("helper:" + line);
                            string tok = line.StartsWith("RETROSYNC-PAIR 1 ") ? line.Substring(17) : "";
                            var o = Encoding.ASCII.GetBytes(reply(tok, token) + "\n"); c.GetStream().Write(o, 0, o.Length);
                            if (tok == token) break;
                        }
                    }
                    hl.Stop();
                }
                catch (Exception e) { Console.WriteLine("fake loader: " + e.Message); }
            }) { IsBackground = true }.Start();
        }

        static void FakePairing()
        {
            var con = IPAddress.Parse("127.0.0.3");
            var fakeElf = Encoding.ASCII.GetBytes("\x7f" + "ELF....data...RSYNC-TOKEN-SLOT:00000000000000000000000000000000\0...end");
            PairHelper.ElfOverride = () => fakeElf;
            PairHelper.LoaderPort = 19021; PairHelper.HelperPort = 19303; PairHelper.HelperWaitMs = 8000;
            RemotePlay.RegistPortOverride = 19297;

            // token patching
            var t1 = PairHelper.NewToken(); var p1 = PairHelper.PatchToken(fakeElf, t1);
            Check("pair: token patched into ELF copy", Encoding.ASCII.GetString(p1).Contains(PairHelper.Marker + t1) && p1.Length == fakeElf.Length && Encoding.ASCII.GetString(fakeElf).Contains(PairHelper.Marker + new string('0', 32)));
            Check("pair: tokens are random", PairHelper.NewToken() != t1 && t1.Length == 32);
            try { PairHelper.PatchToken(Encoding.ASCII.GetBytes("no marker here, just bytes.................................."), t1); Check("pair: ELF without marker rejected", false); }
            catch (Exception e) { Check("pair: ELF without marker rejected", (e.Message.Contains("placeholder") || e.Message.Contains("segnaposto") || e.Message.Contains("token")), e.Message); }

            // real helper ELF (if built) has exactly one marker
            var real = Path.Combine("payload", "retrosync-pair.elf");
            if (File.Exists(real)) { try { PairHelper.PatchToken(File.ReadAllBytes(real), t1); Check("pair: built helper ELF has one token slot", true); } catch (Exception e) { Check("pair: built helper ELF has one token slot", false, e.Message); } }

            string acc = RemotePlay.AccountIdToBase64("1234567890123456789");
            var order = new List<string>(); string seen = null;
            FakeConsole(con, 19297, 24681357, a => seen = a, order);
            FakeLoader(con, order, (tok, expected) => tok == expected
                ? "{\"ok\":true,\"account_id\":\"" + acc + "\",\"pin\":\"24681357\",\"rp_enabled\":1,\"user_slot\":1}"
                : "{\"ok\":false,\"error\":\"auth\"}", 1200);
            try
            {
                string gotAcc;
                var r = PairHelper.Pair("127.0.0.3", x => Console.WriteLine("      log: " + x), out gotAcc);
                Check("pair: helper sent to loader with a patched token", order.Count > 0 && order[0].StartsWith("loader:elf ") && !order[0].EndsWith(new string('0', 32)), order.FirstOrDefault());
                string tokLine = order.First(x => x.StartsWith("loader:")).Split(' ').Last();
                Check("pair: client retried until helper listened, sent token", order.Contains("helper:RETROSYNC-PAIR 1 " + tokLine));
                Check("pair: console decrypted account id from helper", seen == acc, seen);
                Check("pair: keys returned, credential derivable", r.RegistKey == "3132333435363738" && r.Nickname == "PS5-AUTO" && RemotePlay.CredentialFromRegistKey(r.RegistKey) == 0x12345678UL && gotAcc == acc);
                int iHelper = order.FindIndex(x => x.StartsWith("helper:RETROSYNC")), iSrc = order.IndexOf("console:SRC3"), iReg = order.IndexOf("console:rgst");
                Check("pair: registration right after helper reply, nothing else in between", iHelper >= 0 && iSrc == iHelper + 1 && iReg == iSrc + 1 && order.Count(x => x.StartsWith("helper:R")) == 1, string.Join(" | ", order));
            }
            catch (Exception e) { Check("pair: full flow", false, e.Message + " | " + string.Join(" | ", order)); }

            // error from the helper -> Italian message, no registration attempted
            Thread.Sleep(300);
            var order2 = new List<string>();
            FakeLoader(con, order2, (tok, exp) => "{\"ok\":false,\"error\":\"no signed-in user\",\"rp_enabled\":0}", 100);
            try { string a2; PairHelper.Pair("127.0.0.3", null, out a2); Check("pair: helper error reported", false); }
            catch (Exception e) { Check("pair: helper error -> English message with Remote Play hint", ((e.Message.Contains("no user") || e.Message.Contains("nessun utente")) && (e.Message.Contains("disabled") || e.Message.Contains("disattivata") || e.Message.Contains("Remote Play"))), e.Message); }

            // no loader on 9021
            try { string a3; PairHelper.LoaderPort = 19099; PairHelper.Pair("127.0.0.3", null, out a3); Check("pair: no loader", false); }
            catch (Exception e) { Check("pair: no ELF loader -> readable error", e.Message.Contains("ELF loader"), e.Message); }

            // reply validation
            try { PairHelper.Parse("{\"ok\":true,\"account_id\":\"AAAAAAAAAAA=\",\"pin\":\"12345678\"}"); Check("pair: zero account id rejected", false); }
            catch (Exception e) { Check("pair: zero account id rejected", e.Message.Contains("Account ID"), e.Message); }
            try { PairHelper.Parse("{\"ok\":true,\"account_id\":\"" + acc + "\",\"pin\":\"1234\"}"); Check("pair: short PIN rejected", false); }
            catch (Exception e) { Check("pair: short PIN rejected", e.Message.Contains("PIN"), e.Message); }

            // old settings file (2.0, with AccountId) still loads
            var old = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AppSettings>("{\"FtpHost\":\"192.168.1.9\",\"Wake\":{\"AccountId\":\"1234567890123456789\",\"Credential\":\"305419896\",\"Bogus\":1}}");
            Check("settings: 2.0 file with AccountId still loads", old.FtpHost == "192.168.1.9" && old.Wake.AccountId == "1234567890123456789" && old.Wake.IsConfigured());

            PairHelper.ElfOverride = null; PairHelper.LoaderPort = 9021; PairHelper.HelperPort = 9303; PairHelper.HelperWaitMs = 30000;
            RemotePlay.RegistPortOverride = 0;
        }

        // ---------------------------------------------------------------- "Metti a riposo"
        // Fake PS5 on 127.0.0.4: ELF loader (19021) + FTP (12123) + DDP (19304). When the rest ELF arrives the
        // console closes FTP and answers 620 after a short delay.
        static void FakeRest()
        {
            var con = IPAddress.Parse("127.0.0.4");
            var restElf = Encoding.ASCII.GetBytes("\x7f" + "ELF-rest-helper");
            RestMode.ElfOverride = () => restElf;
            RestMode.PollMs = 200; RestMode.ConfirmSeconds = 8;
            PairHelper.LoaderPort = 19021;
            Ddp.PortOverride = 19304;
            var order = new List<string>();
            byte[] got = null;
            var loader = new TcpListener(con, 19021); loader.Start();
            var ftp = new TcpListener(con, 12123); ftp.Start();
            var ddp = new UdpClient(new IPEndPoint(con, 19304));
            bool resting = false;
            new Thread(() =>
            {
                try
                {
                    using (var c = loader.AcceptTcpClient())
                    {
                        var ms = new MemoryStream(); c.GetStream().CopyTo(ms); got = ms.ToArray();
                        lock (order) order.Add("loader:" + got.Length);
                    }
                    Thread.Sleep(600);
                    resting = true;
                    try { ftp.Stop(); } catch { }
                    lock (order) order.Add("standby");
                }
                catch (Exception e) { Console.WriteLine("fake rest loader: " + e.Message); }
            }) { IsBackground = true }.Start();
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var d = ddp.Receive(ref from);
                        if (Encoding.ASCII.GetString(d).StartsWith("SRCH"))
                        {
                            var r = Encoding.ASCII.GetBytes(resting
                                ? "HTTP/1.1 620 Server Standby\nhost-type:PS5\nhost-name:PS5-REST\n"
                                : "HTTP/1.1 200 Ok\nhost-type:PS5\nhost-name:PS5-REST\n");
                            ddp.Send(r, r.Length, from);
                        }
                    }
                }
                catch { }
            }) { IsBackground = true }.Start();
            try
            {
                var s = new AppSettings { FtpHost = "127.0.0.4", FtpPort = 12123 };
                s.Wake.UseDiscovery = true;
                var lines = new List<string>();
                bool ok = RestMode.Enter(s, x => { lines.Add(x); Console.WriteLine("      log: " + x); });
                Check("rest: ELF sent to loader", got != null && Encoding.ASCII.GetString(got).Contains("ELF-rest-helper"), got == null ? "null" : got.Length.ToString());
                Check("rest: rest mode confirmed via DDP 620", ok && order.Contains("standby"), string.Join("|", order) + " / " + string.Join("; ", lines));
            }
            catch (Exception e) { Check("rest: flow", false, e.Message); }
            try { loader.Stop(); } catch { }
            PairHelper.LoaderPort = 19099;   // nothing listens here
            try { RestMode.Enter(new AppSettings { FtpHost = "127.0.0.4" }, null); Check("rest: no loader", false); }
            catch (Exception e) { Check("rest: no ELF loader -> readable error", e.Message.Contains("ELF loader"), e.Message); }
            // host ELF has the expected size when built
            var real = Path.Combine("payload", "retrosync-rest.elf");
            if (File.Exists(real))
            {
                RestMode.ElfOverride = null;
                long n = new FileInfo(real).Length;
                Check("rest: built helper ELF present", n > 50000 && Encoding.ASCII.GetString(File.ReadAllBytes(real)).IndexOf("sceSystemStateMgrEnterStandby") >= 0, n + " bytes");
            }
            RestMode.ElfOverride = null; RestMode.PollMs = 1500; RestMode.ConfirmSeconds = 60;
            PairHelper.LoaderPort = 9021; Ddp.PortOverride = 0;
            try { loader.Stop(); } catch { } try { ftp.Stop(); } catch { } try { ddp.Close(); } catch { }
        }


        static void FakeRegistration()
        {
            const int port = 19295;
            RemotePlay.RegistPortOverride = port;
            var con = IPAddress.Parse("127.0.0.2");   // the "console" lives on another address, like a real PS5
            var udp = new UdpClient(new IPEndPoint(con, port));
            var tl = new TcpListener(con, port); tl.Start();
            string seenAccount = null, seenPath = null, seenVersion = null;
            new Thread(() =>
            {
                try { var from = new IPEndPoint(IPAddress.Any, 0); var d = udp.Receive(ref from); if (Encoding.ASCII.GetString(d) == "SRC3") { var r = Encoding.ASCII.GetBytes("RES3"); udp.Send(r, r.Length, from); } } catch { }
            }) { IsBackground = true }.Start();
            new Thread(() =>
            {
                try
                {
                    using (var c = tl.AcceptTcpClient())
                    {
                        var s = c.GetStream();
                        var ms = new MemoryStream(); var buf = new byte[4096];
                        int split = -1, len = -1;
                        while (true)
                        {
                            int n = s.Read(buf, 0, buf.Length); if (n <= 0) break; ms.Write(buf, 0, n);
                            var raw = ms.ToArray(); var txt = Encoding.ASCII.GetString(raw);
                            split = txt.IndexOf("\r\n\r\n");
                            if (split < 0) continue;
                            var head = txt.Substring(0, split);
                            seenPath = head.Split('\n')[0].Trim();
                            foreach (var h in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
                            {
                                if (h.StartsWith("Content-Length:")) len = int.Parse(h.Substring(15).Trim());
                                if (h.StartsWith("RP-Version:")) seenVersion = h.Substring(11).Trim();
                            }
                            if (raw.Length - split - 4 >= len) break;
                        }
                        var all = ms.ToArray();
                        var body = all.Skip(split + 4).ToArray();
                        // what the console does: recover the nonce from the padding, derive seed + iv from the PIN
                        var pad = body.Take(480).ToArray();
                        var aero = new byte[16];
                        Buffer.BlockCopy(pad, 0xc7, aero, 8, 8); Buffer.BlockCopy(pad, 0x191, aero, 0, 8);
                        int off = pad[0] >> 3; var nonce = new byte[16];
                        for (int i = 0; i < 16; i++) nonce[i] = (byte)(((aero[i] - 0xd3 - i) & 0xff) ^ RpKeys.Ps5AeroKey[i * 0x20 + off]);
                        var seed = RemotePlay.Seed(87654321, pad[0x18D] & 0x1F);
                        var iv = RemotePlay.Iv(nonce, 0);
                        var rec = Encoding.UTF8.GetString(RemotePlay.Cfb(seed, iv, body.Skip(480).ToArray(), false));
                        foreach (var l in rec.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) if (l.StartsWith("Np-AccountId: ")) seenAccount = l.Substring(14);
                        var payload = RemotePlay.Cfb(seed, iv, Encoding.UTF8.GetBytes("AP-Name: PS5-TEST\r\nPS5-Mac: a1b2c3d4e5f6\r\nPS5-RegistKey: 3132333435363738\r\nRP-Key: 00112233445566778899aabbccddeeff\r\nPS5-Nickname: PS5-TEST\r\n"), true);
                        var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: " + payload.Length + "\r\n\r\n");
                        s.Write(resp, 0, resp.Length); s.Write(payload, 0, payload.Length); s.Flush();
                        Thread.Sleep(3000);   // keep the socket open: the client must stop at Content-Length
                    }
                }
                catch (Exception e) { Console.WriteLine("fake tcp: " + e.Message); }
            }) { IsBackground = true }.Start();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = RemotePlay.Register("127.0.0.2", "1234567890123456789", "8765 4321", x => Console.WriteLine("      log: " + x));
                Check("fake console: registration request line", seenPath == "POST /sie/ps5/rp/sess/rgst HTTP/1.1", seenPath);
                Check("fake console: RP-Version header", seenVersion == "1.0");
                Check("fake console: account id decrypted by console", seenAccount == RemotePlay.AccountIdToBase64("1234567890123456789"), seenAccount);
                Check("fake console: keys parsed", r.RegistKey == "3132333435363738" && r.RpKey == "00112233445566778899aabbccddeeff" && r.Nickname == "PS5-TEST" && r.Mac == "a1b2c3d4e5f6");
                Check("fake console: response read without waiting for close", sw.ElapsedMilliseconds < 2500, sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception e) { Check("fake console: registration", false, e.Message); }
            // wrong PIN -> console answers 403 with reason
            var tl2 = new TcpListener(con, 19296); tl2.Start();
            var udp2 = new UdpClient(new IPEndPoint(con, 19296));
            RemotePlay.RegistPortOverride = 19296;
            new Thread(() => { try { var from = new IPEndPoint(IPAddress.Any, 0); udp2.Receive(ref from); var r = Encoding.ASCII.GetBytes("RES3"); udp2.Send(r, r.Length, from); } catch { } }) { IsBackground = true }.Start();
            new Thread(() =>
            {
                try
                {
                    using (var c = tl2.AcceptTcpClient())
                    {
                        var s = c.GetStream(); var buf = new byte[8192]; Thread.Sleep(300); s.Read(buf, 0, buf.Length);
                        var resp = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108b09\r\nContent-Length: 0\r\n\r\n");
                        s.Write(resp, 0, resp.Length);
                    }
                }
                catch { }
            }) { IsBackground = true }.Start();
            try { RemotePlay.Register("127.0.0.2", "1234567890123456789", "00000000", null); Check("fake console: wrong PIN rejected", false); }
            catch (Exception e) { Check("fake console: wrong PIN -> readable error", e.Message.Contains("Wrong or expired PIN") || e.Message.Contains("PIN errato"), e.Message); }
            RemotePlay.RegistPortOverride = 0;
            tl.Stop(); udp.Close(); tl2.Stop(); udp2.Close();
        }
    }
}
