// RetroSync - "Rest mode": puts the user's own PS5 into rest mode.
// Sends the one-shot helper retrosync-rest.elf (payload/rest.c, MIT) to the ELF loader on 9021; the helper
// calls the system's enter-standby function and exits. RetroSync then watches Remote Play discovery
// (UDP 9302: "620 Server Standby") and the FTP port to confirm. No reboot / power-off code anywhere.
// (Sony's Remote Play "standby" command needs a full Remote Play session, so it is not used.)
using System;
using System.IO;
using System.Threading;

namespace RetroSync
{
    static class RestMode
    {
        public const string ResourceName = "retrosync-rest.elf";
        public static Func<byte[]> ElfOverride;   // tests only
        public static int ConfirmSeconds = 60;
        public static int PollMs = 1500;

        public static byte[] LoadElf()
        {
            if (ElfOverride != null) return ElfOverride();
            using (var st = typeof(RestMode).Assembly.GetManifestResourceStream(ResourceName))
                if (st != null) { var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray(); }
            string side = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ResourceName);
            if (File.Exists(side)) return File.ReadAllBytes(side);
            throw new Exception("The rest-mode payload (" + ResourceName + ") is not included in this RetroSync.exe.");
        }

        // true = rest mode confirmed (DDP 620, or FTP gone and DDP silent); false = not confirmed in time.
        public static bool Enter(AppSettings s, Action<string> log)
        {
            if (log == null) log = x => { };
            string host = (s.FtpHost ?? "").Trim();
            if (host.Length == 0) throw new Exception("Inserisci l'IP del PS5 nelle Settings.");
            var elf = LoadElf();
            log("Sending the rest-mode command to " + host + ":" + PairHelper.LoaderPort + "\u2026");
            PairHelper.SendElf(host, elf);
            log("Command sent: waiting for the PS5 to enter rest mode\u2026");
            var end = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
            bool ftpGone = false;
            while (DateTime.UtcNow < end)
            {
                Thread.Sleep(PollMs);
                DdpStatus d = s.Wake.UseDiscovery ? Ddp.Probe(host, 1000, 1) : null;
                if (d != null && d.Standby) { log("PS5 is in rest mode."); return true; }
                int ms;
                bool ftp = Ps5Probe.FtpAnswers(host, s.FtpPort, 1500, out ms);
                if (!ftp && (!s.Wake.UseDiscovery || d == null))
                {
                    // FTP down and no discovery answer: either still going down, or resting without network features
                    if (ftpGone && !s.Wake.UseDiscovery) { log("The PS5 no longer answers (FTP down): rest mode is likely."); return true; }
                    ftpGone = true;
                }
            }
            return false;
        }
    }
}
