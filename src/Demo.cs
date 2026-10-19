using System;
using System.Collections.Generic;
using System.Linq;

// "--demo": fills the UI with sample data for screenshots. Nothing is read from or written to the PS5,
// RetroArch or the settings file while it is active (Apply/backup/wake buttons only show a notice).
namespace RetroSync
{
    static class Demo
    {
        public static bool On;

        public static void PrepareSettings(AppSettings s)
        {
            s.PcRetroArchRoot = @"C:\Games\RetroArch";
            s.FtpHost = "192.168.1.50"; s.FtpPort = 2121;
            s.Ps5RetroArchRoot = "/data/homebrew/RetroArch";
            s.Ps2.LocalDir = @"C:\Games\PCSX2\memcards";
            s.Wake.Credential = "1234567890123"; s.Wake.Nickname = "PS5-DEMO"; s.Wake.Mac = "demo"; s.Wake.PairedOn = "18/10/2026 22:40";
            s.Wake.AccountId = "1234567890123456789";
            s.Ui.StatusPollSeconds = 20;
        }

        static DateTime Ago(double hours) { return DateTime.UtcNow.AddHours(-hours); }
        static string Sha(int seed) { var r = new Random(seed); var b = new byte[32]; r.NextBytes(b); return BitConverter.ToString(b).Replace("-", ""); }

        public static void Apply(MainWindow w)
        {
            w.MachineName = "DESKTOP-DEMO";
            w.Status = new Ps5State
            {
                Power = Ps5Power.FtpOnline, LatencyMs = 4,
                Ddp = new DdpStatus { Code = 200, StatusText = "Ok", HostName = "PS5-DEMO", HostType = "PS5", SystemVersion = "04030000" }
            };
            var now = DateTime.Now;
            ActivityLog.ReplaceForDemo(new List<ActivityEntry>
            {
                new ActivityEntry { When = now.Date.AddHours(5).AddMinutes(2).Ticks, Kind = "Saves", Title = "Sync Saves", Detail = "12 files \u00B7 Star Fox 64, Rayman 2 and others", Status = "ok", ToPs5 = 9, ToPc = 3, Files = 12, Bytes = 3565158 },
                new ActivityEntry { When = now.Date.AddHours(4).AddMinutes(58).Ticks, Kind = "RetroArch", Title = "Preview", Detail = "Sync RetroArch \u00B7 dry-run, 7 differences", Status = "dry" },
                new ActivityEntry { When = now.Date.AddDays(-1).AddHours(21).AddMinutes(40).Ticks, Kind = "PS2", Title = "Memory card PS2", Detail = "2 save PC \u2192 PS5 (mymc)", Status = "ok" },
                new ActivityEntry { When = now.Date.AddDays(-1).AddHours(21).AddMinutes(31).Ticks, Kind = "Wake", Title = "Wake PS5", Detail = "PS5 awake, FTP up after 41 s", Status = "ok" },
                new ActivityEntry { When = now.Date.AddDays(-3).AddHours(22).AddMinutes(14).Ticks, Kind = "Backup", Title = "Full PS5 mirror", Detail = "41.2 GB \u00B7 18,904 files, 312 copied", Status = "ok", Files = 312, Bytes = 1288490188 },
                new ActivityEntry { When = now.Date.AddDays(-5).AddHours(3).Ticks, Kind = "Backup", Title = "Backup RetroArch", Detail = "312 MB \u00B7 1,204 files", Status = "ok" },
                new ActivityEntry { When = now.Date.AddDays(-10).AddHours(3).Ticks, Kind = "Backup", Title = "Full PS5 mirror", Detail = "40.8 GB \u00B7 18,611 files, 96 copied", Status = "ok" },
            });
            w.ApplyStatus();
            foreach (var l in new[]
            {
                "RetroSync 2.0 \u2014 DEMO mode: sample data, no access to PS5 or RetroArch.",
                "PS5 status: online (FTP up) \u00B7 192.168.1.50:2121 \u00B7 4 ms",
                "Remote Play: PS5-DEMO \u00B7 awake \u00B7 firmware 4.03",
            }) w.Log(l);
            w.SetDryRun(true);
            FillRetroArch(w.RetroArch, SyncScope.All);
            if (w.CurrentPage != null) w.CurrentPage.Entered();
        }

        public static void FillRetroArch(RetroArchPage page, SyncScope scope)
        {
            var items = new List<SyncItem>();
            Func<string, string, Dir, string, double?, double?, long, SyncItem> mk = (cat, rel, dir, reason, pcH, psH, size) =>
            {
                var it = new SyncItem
                {
                    Category = cat, PcRel = rel, Ps5Rel = rel, Kind = ItemKind.Raw,
                    PcFull = @"C:\Games\RetroArch\" + rel.Replace('/', '\\'),
                    Ps5Path = "/data/homebrew/RetroArch/" + rel,
                    PcExists = pcH.HasValue, Ps5Exists = psH.HasValue,
                    PcMtime = pcH.HasValue ? Ago(pcH.Value) : (DateTime?)null, Ps5Mtime = psH.HasValue ? Ago(psH.Value) : (DateTime?)null,
                    PcSize = pcH.HasValue ? size : 0, Ps5Size = psH.HasValue ? size : 0,
                    PcSha = pcH.HasValue ? Sha(rel.Length) : null, Ps5Sha = psH.HasValue ? Sha(rel.Length + 7) : null,
                    Direction = dir, Reason = reason
                };
                it.Options = dir == Dir.None ? new List<Dir> { Dir.None } : new List<Dir> { dir, Dir.None };
                it.Selected = dir != Dir.None;
                items.Add(it);
                return it;
            };
            mk("Saves", "saves/Mupen64Plus-Next/Star Fox 64 (USA).srm", Dir.ToPs5, "PC newer", 9, 70, 524288);
            mk("Saves", "saves/Mupen64Plus-Next/Rayman 2 - The Great Escape (USA).srm", Dir.ToPs5, "new on PC", 10.5, null, 32768);
            var z = mk("Saves", "saves/Snes9x/Zelda - A Link to the Past (USA).srm", Dir.ToPc, "changed on both", 36, 10.2, 8192);
            z.Conflict = true; z.Selected = false; z.Options = new List<Dir> { Dir.ToPs5, Dir.ToPc, Dir.None };
            z.Details.Add("Last sync: 12/10/2026 21:05 \u2014 both files changed since then.");
            z.Details.Add("Differing bytes: 214 (offset 0x0400\u20130x05FF)");
            mk("Saves", "saves/Mesen/Ghosts'n Goblins (USA).srm", Dir.ToPc, "PS5 newer", 190, 52, 8192);
            mk("States", "states/Mupen64Plus-Next/Star Fox 64 (USA).state1", Dir.ToPs5, "PC newer", 9.1, 69, 4404019);
            var cfg = mk("Config", "retroarch.cfg", Dir.Merge, "12 different keys", 3, 180, 98304);
            cfg.Kind = ItemKind.KeyValue; cfg.Options = new List<Dir> { Dir.Merge, Dir.ToPs5, Dir.ToPc, Dir.None };
            cfg.Ps5Path = "/data/homebrew/RetroArch/config/retroarch.cfg";
            cfg.Details.AddRange(new[]
            {
                "Shared keys in common: 812, platform/path keys ignored: 143",
                "\u2192 PS5  menu_driver: \"xmb\" \u2192 \"ozone\"",
                "\u2192 PS5  video_shader_enable: \"false\" \u2192 \"true\"",
                "\u2192 PS5  rewind_enable: \"false\" \u2192 \"true\"",
                "\u2192 PC   input_overlay_opacity: \"0.700000\" \u2192 \"0.500000\"",
                "\u2192 PC   savestate_auto_load: \"false\" \u2192 \"true\"",
            });
            var opt = mk("Config", "config/Mupen64Plus-Next/Mupen64Plus-Next.opt", Dir.ToPc, "PS5 only", null, 120, 2048);
            opt.Selected = false;
            mk("Remap", "config/remaps/Snes9x/Snes9x.rmp", Dir.ToPs5, "new on PC", 100, null, 1024);
            var ex = mk("Config", "retroarch-core-options.cfg", Dir.None, "excluded (settings)", 3, 180, 20480);
            ex.Info = true;
            var id = mk("Saves", "saves/Snes9x/Super Metroid (JU).srm", Dir.None, "identical (hash)", 260, 260, 8192);
            id.Identical = true; id.Ps5Sha = id.PcSha;
            var plan = new SyncPlan { Scope = scope };
            plan.Items.AddRange(items.Where(i => scope == SyncScope.All || ((i.Category == "Saves" || i.Category == "States") == (scope == SyncScope.Saves))));
            page.ShowPlan(null, plan, true);
        }

        public static void FillPs2(Ps2CardForm f)
        {
            var now = DateTime.Now;
            var p = new Plan
            {
                Action = Act.Merge, Title = "Per-save sync",
                Reason = "2 to the PS5, 1 to the PC. Double-click a save to change direction.",
                Local = new CardInfo { Exists = true, When = now.AddHours(-14), Size = 8650752, SaveCount = 14, LastSave = now.AddHours(-14), LastSaveName = "BASLUS-21005 \u00B7 Ratchet & Clank" },
                Remote = new CardInfo { Exists = true, When = now.AddDays(-2), Size = 8650752, SaveCount = 13, LastSave = now.AddDays(-1).AddHours(-3), LastSaveName = "BESLES-50330 \u00B7 Gran Turismo 3" },
            };
            p.Items.Add(new SaveItem { Name = "BASLUS-21005", Title = "Ratchet & Clank", Kb = 160, PcMod = now.AddHours(-14), PsMod = now.AddDays(-6), PcHash = "a", PsHash = "b", Status = "PC newer", Dir = 1 });
            p.Items.Add(new SaveItem { Name = "BASLUS-20963", Title = "Jak and Daxter", Kb = 96, PcMod = now.AddDays(-1), PcHash = "c", Status = "PC only", Dir = 1 });
            p.Items.Add(new SaveItem { Name = "BESLES-50330", Title = "Gran Turismo 3 A-spec", Kb = 512, PcMod = now.AddDays(-20), PsMod = now.AddDays(-1).AddHours(-3), PcHash = "d", PsHash = "e", Status = "PS5 newer", Dir = 2 });
            p.Items.Add(new SaveItem { Name = "BASLUS-20199", Title = "Final Fantasy X", Kb = 288, PcMod = now.AddDays(-40), PsMod = now.AddDays(-40), PcHash = "f", PsHash = "f", Status = "identical", Dir = 0 });
            p.Items.Add(new SaveItem { Name = "BASLUS-21065", Title = "Shadow of the Colossus", Kb = 64, PcMod = now.AddDays(-55), PsMod = now.AddDays(-55), PcHash = "g", PsHash = "g", Status = "identical", Dir = 0 });
            string t = now.ToString("HH:mm:ss");
            f.ShowSample(p, new[]
            {
                t + "  Reading the card on the PC\u2026",
                t + "  Reading the card on the PS5 (192.168.1.50:2121)\u2026",
                t + "  Downloading the PS5 card to a temp file to compare saves\u2026",
                t + "  Reading saves with mymc\u2026",
                t + "  Saves read: PC 14, PS5 13.",
                t + "  Per-save sync — 2 to the PS5, 1 to the PC.",
            });
        }
    }
}
