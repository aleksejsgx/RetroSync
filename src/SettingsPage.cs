using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

// Settings: same fields and validation as the v1 settings dialog, plus backup, remote power-on and UI options.
namespace RetroSync
{
    class SettingsPage : Page
    {
        readonly Panel body;
        readonly List<CardPanel> cards = new List<CardPanel>();
        readonly Panel footer;
        readonly DarkButton save, revert;
        readonly Label dirtyLbl;
        CardPanel wakeCard;

        DarkTextField host, port, user, pass, timeout, pcRoot, psRoot, psApp, ps2Local, ps2Card, ps2Remote, mymc, backup, keep, emuKeep, mirLocal, mirRemote;
        Toggle ps2InSave;
        DarkTextField dolPc, dolPs5;
        Toggle dolGc, dolWii, dolInSave, autoStart, toTray;
        DarkTextField cred, waitS, settleS, pollS;
        Segmented after;
        Toggle useDdp;
        Label wakeResult, connResult;
        Segmented langSeg;
        DarkButton pairBtn, restBtn, probeBtn, testWakeBtn, connBtn;

        public SettingsPage(MainWindow w) : base(w, "settings", "Settings")
        {
            body = new Panel { AutoScroll = true, BackColor = Theme.Content };
            body.HandleCreated += (s, e) => Theme.DarkScrollbars(body);
            Controls.Add(body);
            footer = new Panel { BackColor = Theme.Content };
            Controls.Add(footer);
            save = new DarkButton(Lang.T("Save", "Salva"), BtnKind.Primary, Ico.Check) { Width = 110 };
            save.Click += (s, e) => Save();
            revert = new DarkButton(Lang.T("Discard changes", "Annulla modifiche")) { };
            revert.Click += (s, e) => Load();
            dirtyLbl = Ui.Label(footer, "Advanced options (key filters, exclusions, mappings) are in RetroSync.settings.json.", Theme.F12, Theme.Muted, 0, 10);
            footer.Controls.Add(save); footer.Controls.Add(revert);
            Build();
            Load();
        }

        public override string Subtitle { get { return SettingsStore.SettingsPath; } }

        // ------------------------------------------------------------------ building
        CardPanel Card(string title, string hint = null)
        {
            var c = new CardPanel(title) { Hint = hint, Width = 900 };
            cards.Add(c); body.Controls.Add(c);
            return c;
        }

        DarkTextField Row(CardPanel c, ref int y, string label, string placeholder = null, bool pwd = false, int width = 0)
        {
            Ui.Label(c, label, Theme.F13, Theme.TextSoft, 18, y + 7, 220, 20);
            var f = new DarkTextField("", placeholder, pwd) { BackColor = Theme.Card };
            f.SetBounds(240, y, width > 0 ? width : c.Width - 240 - 18, 32);
            if (width == 0) f.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            c.Controls.Add(f);
            y += 40;
            return f;
        }

        void Folder(DarkTextField f)
        {
            f.AddBrowse(() => { using (var d = new FolderBrowserDialog { SelectedPath = f.Text }) if (d.ShowDialog(FindForm()) == DialogResult.OK) f.Text = d.SelectedPath; });
        }

        void Build()
        {
            int y;
            var c = Card("Connection PS5", "server FTP del payload (es. ftpsrv / etaHEN)");
            y = 50;
            host = Row(c, ref y, "PS5 IP address", "192.168.1.50", false, 220);
            port = Row(c, ref y, "FTP port", "2121", false, 100);
            user = Row(c, ref y, "FTP user", "anonymous", false, 220);
            pass = Row(c, ref y, "FTP password", "", true, 220);
            timeout = Row(c, ref y, "FTP timeout (s)", "20", false, 100);
            connBtn = Ui.Add(c, new DarkButton(Lang.T("Test connection", "Prova connessione"), BtnKind.Normal, Ico.Link, true), 240, y);
            connBtn.Click += (s, e) => TestConnection();
            connResult = Ui.Label(c, "", Theme.F125, Theme.Muted, 250 + connBtn.Width, y + 5, 560, 20);
            connResult.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            c.Height = y + 44;

            c = Card("RetroArch");
            y = 50;
            pcRoot = Row(c, ref y, "RetroArch folder on PC"); Folder(pcRoot);
            psRoot = Row(c, ref y, "RetroArch folder the PS5 (FTP)", "/data/homebrew/RetroArch");
            psApp = Row(c, ref y, "Radice app PS5 (playlist)", "/app0");
            c.Height = y + 8;

            c = Card("PS2 \u00B7 memory card");
            y = 50;
            ps2Local = Row(c, ref y, "Memory card folder on PC"); Folder(ps2Local);
            ps2Card = Row(c, ref y, "Card name");
            ps2Remote = Row(c, ref y, "Memory card folder the PS5", "/data/PCSX2");
            mymc = Row(c, ref y, "Path to mymc.exe");
            mymc.AddBrowse(() => { using (var d = new OpenFileDialog { Filter = "Programmi|*.exe", FileName = mymc.Text }) if (d.ShowDialog(FindForm()) == DialogResult.OK) mymc.Text = d.FileName; });
            ps2InSave = Ui.Add(c, new Toggle("Includi la card PS2 in \u201CSync saves\u201D", true), 240, y + 2);
            c.Height = y + 42;

            c = Card("GameCube / Wii \u00B7 Dolphin \u21C4 Porpoise", "tested with Dolphin (PC) and Porpoise (PS5)");
            y = 50;
            dolPc = Row(c, ref y, "Dolphin User folder on PC", @"e.g. D:\Dolphin\User (portable) or %APPDATA%\Dolphin Emulator"); Folder(dolPc);
            dolPs5 = Row(c, ref y, "Porpoise User folder on PS5", "/data/porpoise/saves/User");
            dolGc = Ui.Add(c, new Toggle("Sync GameCube saves (GCI folder: GC/<region>/Card A)", true), 240, y + 2); y += 34;
            dolWii = Ui.Add(c, new Toggle("Sync Wii save data (Wii/title/.../data)", false), 240, y + 2); y += 34;
            dolInSave = Ui.Add(c, new Toggle("Include Dolphin in the command-line preview (--dry-run)", false), 240, y + 2); y += 34;
            Ui.Label(c, "Savestates are never synced: different Dolphin builds cannot load each other's states.", Theme.F12, Theme.Muted, 240, y, 620, 20).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            c.Height = y + 34;

            c = Card("Backup");
            y = 50;
            backup = Row(c, ref y, "Backup folder"); Folder(backup);
            keep = Row(c, ref y, "Backups to keep", "20", false, 100);
            mirLocal = Row(c, ref y, "PS5 mirror: PC folder"); Folder(mirLocal);
            mirRemote = Row(c, ref y, "PS5 mirror: PS5 folder", "/data/homebrew/RetroArch");
            emuKeep = Row(c, ref y, "Versions per emulator", "10", false, 100);
            c.Height = y + 8;

            wakeCard = c = Card("Remote power-on", "PS5 Remote Play \u00B7 UDP 9302 / 9295");
            y = 50;
            c.PaintExtra = PaintWakeStatus;
            y += 56;   // status box
            Ui.Label(c, Lang.T("1. Pair once (recommended)", "1. Associa una volta (consigliato)"), Theme.S13, Theme.Text, 18, y, 400, 22); y += 28;
            pairBtn = Ui.Add(c, new DarkButton(Lang.T("Pair PS5"), BtnKind.Primary, Ico.Link, true), 240, y);
            pairBtn.Click += (s, e) => Pair();
            Ui.Label(c, "The PS5 must be on, with your user signed in, Remote Play enabled and the ELF loader listening on port 9021 (jailbreak started). " +
                "RetroSync sends a small payload that reads the Account ID and PIN, then pairs by itself — nothing to type.",
                Theme.F12, Theme.Muted, 240, y + 40, 620, 36).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += 82;
            Ui.Label(c, Lang.T("2. Or enter a wake credential", "2. Oppure inserisci un codice di accensione"), Theme.S13, Theme.Text, 18, y, 400, 22); y += 28;
            cred = Row(c, ref y, "Credential (user-credential)", "decimal, or PS5-RegistKey (hex)", false, 380);
            Ui.Label(c, "For example the one shown by other Remote Play tools (chiaki-ng, PS5-RegistKey).", Theme.F12, Theme.Muted, 240, y - 4, 620, 20).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += 26;
            Ui.Label(c, Lang.T("3. Behaviour", "3. Comportamento"), Theme.S13, Theme.Text, 18, y, 400, 22); y += 28;
            Ui.Label(c, Lang.T("After wake", "Dopo l'accensione"), Theme.F13, Theme.TextSoft, 18, y + 7, 220, 20);
            after = new Segmented();
            after.Add("Sync saves", Ico.Save); after.Add("Sync RetroArch", Ico.Gamepad); after.Add("Sync all", Ico.Sync); after.Add("Wake only", Ico.Power);
            after.ItemClicked += i => after.SelectedIndex = i;
            Ui.Add(c, after, 240, y);
            y += 44;
            waitS = Row(c, ref y, "Max FTP wait (s)", "180", false, 100);
            settleS = Row(c, ref y, "Wait after FTP (s)", "8", false, 100);
            useDdp = Ui.Add(c, new Toggle("Read PS5 status over the network (rest / awake / firmware)", true), 240, y + 2); y += 36;
            pollS = Row(c, ref y, "Status check every (s)", "20 (0 = never)", false, 100);
            probeBtn = Ui.Add(c, new DarkButton(Lang.T("Check status"), BtnKind.Normal, Ico.Search, true), 240, y);
            probeBtn.Click += (s, e) => Probe();
            testWakeBtn = Ui.Add(c, new DarkButton(Lang.T("Test wake"), BtnKind.Normal, Ico.Power, true), 250 + probeBtn.Width, y);
            testWakeBtn.Click += (s, e) => TestWake();
            restBtn = Ui.Add(c, new DarkButton(Lang.T("Rest mode"), BtnKind.Normal, Ico.Power, true), 260 + probeBtn.Width + testWakeBtn.Width, y);
            restBtn.Click += (s, e) => W.Rest();
            wakeResult = Ui.Label(c, "", Theme.F125, Theme.Muted, 270 + probeBtn.Width + testWakeBtn.Width + restBtn.Width, y + 5, 340, 20);
            wakeResult.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += 40;
            var chk = Ui.Label(c, MainWindow.WakeChecklist, Theme.F12, Theme.Muted, 240, y, 620, 92);
            chk.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += 96;
            c.Height = y + 8;

            c = Card("Advanced");
            y = 48;
            Ui.Label(c, "Language / Lingua", Theme.F13, Theme.TextSoft, 18, y + 7, 220, 20);
            langSeg = new Segmented();
            langSeg.Add("English", Ico.Gear); langSeg.Add("Italiano", Ico.Gear);
            langSeg.ItemClicked += i => langSeg.SelectedIndex = i;
            Ui.Add(c, langSeg, 240, y); y += 44;

            autoStart = Ui.Add(c, new Toggle(Lang.T("Start with Windows (this exe, hidden in the tray)", "Avvia con Windows (questo exe, nascosto nell'area di notifica)"), false), 240, y + 2); y += 34;
            toTray = Ui.Add(c, new Toggle(Lang.T("Minimize / close to the notification area (tray icon)", "Riduci / chiudi nell'area di notifica (icona)"), false), 240, y + 2); y += 40;

            Ui.Label(c, "retroarch.cfg key filters, exclusions, path mappings and mirror: editable in the settings file.", Theme.F125, Theme.Muted, 18, y, 640, 20).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var open = Ui.Add(c, new DarkButton(Lang.T("Open RetroSync.settings.json", "Apri RetroSync.settings.json"), BtnKind.Normal, Ico.Folder, true), 18, y + 28);
            open.Click += (s, e) =>
            {
                try { if (!File.Exists(SettingsStore.SettingsPath)) SettingsStore.Save(S); Process.Start("notepad.exe", "\"" + SettingsStore.SettingsPath + "\""); }
                catch (Exception ex) { DarkDialog.Info(FindForm(), "Could not open", ex.Message, Ico.Warning, Theme.Warn); }
            };
            var folder = Ui.Add(c, new DarkButton(Lang.T("Open program folder", "Apri cartella del programma"), BtnKind.Normal, Ico.Folder, true), 28 + open.Width, y + 28);
            folder.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + SettingsStore.ExeDir + "\""); } catch { } };
            c.Height = y + 72;
        }

        protected override void DoLayout()
        {
            footer.SetBounds(0, Height - 44, Width, 44);
            save.Location = new Point(Width - save.Width, 8);
            revert.Location = new Point(save.Left - 10 - revert.Width, 8);
            dirtyLbl.Location = new Point(0, 18);
            body.SetBounds(0, 0, Width, Height - 54);
            int y = -body.VerticalScroll.Value;
            int cw = Width - 22;
            foreach (var c in cards)
            {
                c.SetBounds(0, y, cw, c.Height);
                y += c.Height + 14;
            }
        }

        public void ScrollToWake()
        {
            if (wakeCard == null) return;
            body.ScrollControlIntoView(wakeCard);
            body.AutoScrollPosition = new Point(0, wakeCard.Top - body.AutoScrollPosition.Y);
            pairBtn.Focus();
        }

        // ------------------------------------------------------------------ load / save (v1 validation)
        void Load()
        {
            var s = S;
            host.Text = s.FtpHost; port.Text = s.FtpPort.ToString(); user.Text = s.FtpUser; pass.Text = s.FtpPassword; timeout.Text = s.FtpTimeoutSeconds.ToString();
            pcRoot.Text = s.PcRetroArchRoot; psRoot.Text = s.Ps5RetroArchRoot; psApp.Text = s.Ps5AppRoot;
            ps2Local.Text = s.Ps2.LocalDir; ps2Card.Text = s.Ps2.CardName; ps2Remote.Text = s.Ps2.RemoteDir; mymc.Text = s.Ps2.MymcPath;
            ps2InSave.SetSilently(s.Ps2.IncludeInSyncSave);
            backup.Text = s.BackupRoot; keep.Text = s.BackupKeep.ToString(); mirLocal.Text = s.MirrorLocalRoot; mirRemote.Text = s.MirrorRemoteRoot; emuKeep.Text = s.Ui.EmulatorBackupKeep.ToString();
            cred.Text = !string.IsNullOrEmpty(s.Wake.Credential) ? s.Wake.Credential : s.Wake.RegistKey;
            after.SelectedIndex = s.Wake.AfterWake == "saves" ? 0 : s.Wake.AfterWake == "config" ? 1 : s.Wake.AfterWake == "none" ? 3 : 2;
            waitS.Text = s.Wake.WaitSeconds.ToString(); settleS.Text = s.Wake.SettleSeconds.ToString();
            useDdp.SetSilently(s.Wake.UseDiscovery); pollS.Text = s.Ui.StatusPollSeconds.ToString();
            var d = s.Dolphin ?? new DolphinSettings();
            dolPc.Text = d.PcUserDir; dolPs5.Text = d.Ps5UserDir;
            dolGc.SetSilently(d.SyncGc); dolWii.SetSilently(d.SyncWii); dolInSave.SetSilently(d.IncludeInSyncSave);
            autoStart.SetSilently(Autostart.IsEnabled()); toTray.SetSilently(s.Ui.MinimizeToTray);
            wakeCard.Invalidate();
        }

        static bool Int(DarkTextField f, int min, int max, out int v) { return int.TryParse(f.Text.Trim(), out v) && v >= min && v <= max; }

        bool Save()
        {
            int p, k, to, ek, ws, ss, ps;
            if (!Int(port, 1, 65535, out p)) return Fail("Port must be a number between 1 and 65535.");
            if (!Int(keep, 1, 100000, out k)) return Fail("Backups to keep must be at least 1.");
            if (!Int(timeout, 3, 600, out to)) return Fail("FTP timeout must be between 3 and 600 seconds.");
            if (!Int(emuKeep, 1, 1000, out ek)) return Fail("Versions per emulator must be at least 1.");
            if (!Int(waitS, 20, 1800, out ws)) return Fail("Max wait must be between 20 and 1800 seconds.");
            if (!Int(settleS, 0, 300, out ss)) return Fail("Wait after FTP must be between 0 and 300 seconds.");
            if (!Int(pollS, 0, 3600, out ps)) return Fail("Status check must be between 0 (never) and 3600 seconds.");
            string credErr;
            if (!ApplyCredential(cred.Text, out credErr)) return Fail(credErr);
            var s = S;
            s.PcRetroArchRoot = pcRoot.Text.Trim().TrimEnd('\\');
            s.FtpHost = host.Text.Trim(); s.FtpPort = p; s.FtpUser = user.Text.Trim(); s.FtpPassword = pass.Text; s.FtpTimeoutSeconds = to;
            s.Ps5RetroArchRoot = "/" + psRoot.Text.Trim().Replace('\\', '/').Trim('/');
            s.Ps5AppRoot = "/" + psApp.Text.Trim().Replace('\\', '/').Trim('/');
            s.Ps2.LocalDir = ps2Local.Text.Trim(); s.Ps2.CardName = ps2Card.Text.Trim(); s.Ps2.RemoteDir = ps2Remote.Text.Trim();
            s.Ps2.MymcPath = mymc.Text.Trim(); s.Ps2.IncludeInSyncSave = ps2InSave.Checked;
            s.BackupRoot = backup.Text.Trim().TrimEnd('\\'); s.BackupKeep = k;
            s.MirrorLocalRoot = mirLocal.Text.Trim().TrimEnd('\\');
            s.MirrorRemoteRoot = "/" + mirRemote.Text.Trim().Replace('\\', '/').Trim('/');
            s.Ui.EmulatorBackupKeep = ek;
            s.Wake.AfterWake = after.SelectedIndex == 0 ? "saves" : after.SelectedIndex == 1 ? "config" : after.SelectedIndex == 3 ? "none" : "all";
            s.Wake.WaitSeconds = ws; s.Wake.SettleSeconds = ss; s.Wake.UseDiscovery = useDdp.Checked; s.Ui.StatusPollSeconds = ps;
            if (s.Dolphin == null) s.Dolphin = new DolphinSettings();
            s.Dolphin.PcUserDir = dolPc.Text.Trim().TrimEnd('\\');
            s.Dolphin.Ps5UserDir = "/" + dolPs5.Text.Trim().Replace('\\', '/').Trim('/');
            s.Dolphin.SyncGc = dolGc.Checked; s.Dolphin.SyncWii = dolWii.Checked; s.Dolphin.IncludeInSyncSave = dolInSave.Checked;
            s.Ui.MinimizeToTray = toTray.Checked; s.Ui.StartWithWindows = autoStart.Checked;
            if (!Demo.On && autoStart.Checked != Autostart.IsEnabled())
            {
                try { Autostart.Set(autoStart.Checked); W.Log(autoStart.Checked ? "Start with Windows: on (" + Autostart.Command + ")" : "Start with Windows: off"); }
                catch (Exception ex) { W.Log("ERROR autostart: " + ex.Message); }
            }
            if (Demo.On) { W.Log("Demo: settings not written to disk."); W.SettingsSaved(); return true; }
            try { SettingsStore.Save(s); W.Log("Settings salvate."); }
            catch (Exception ex) { W.Log("ERROR saving settings: " + ex.Message); return Fail("Could not save: " + ex.Message); }
            W.SettingsSaved();
            Load();
            return true;
        }

        // decimal user-credential, or a PS5-RegistKey (hex) from which it is derived
        bool ApplyCredential(string text, out string err)
        {
            err = null;
            text = (text ?? "").Trim();
            var w = S.Wake;
            if (text.Length == 0)
            {
                // cleared: forget the manual code (a pairing keeps its RegistKey unless cleared too)
                w.Credential = ""; w.RegistKey = "";
                return true;
            }
            ulong v;
            if (text.All(char.IsDigit) && ulong.TryParse(text, out v) && v > 0)
            {
                if (text != w.Credential) { w.Credential = text; if (w.RegistKey.Length > 0 && SafeCred(w.RegistKey) != v) w.RegistKey = ""; }
                return true;
            }
            try
            {
                v = RemotePlay.CredentialFromRegistKey(text);
                if (v == 0) throw new Exception();
                w.RegistKey = text; w.Credential = v.ToString();
                return true;
            }
            catch { err = "The wake credential must be a decimal number (user-credential) or the hex PS5-RegistKey."; return false; }
        }

        static ulong SafeCred(string rk) { try { return RemotePlay.CredentialFromRegistKey(rk); } catch { return 0; } }

        bool Fail(string msg) { DarkDialog.Info(FindForm(), "Settings", msg, Ico.Warning, Theme.Warn); return false; }

        // ------------------------------------------------------------------ actions
        void TestConnection()
        {
            string h = host.Text.Trim(); int p;
            if (!int.TryParse(port.Text.Trim(), out p)) { connResult.Text = "Invalid port"; return; }
            connResult.ForeColor = Theme.Muted; connResult.Text = "Provo " + h + ":" + p + "\u2026";
            connBtn.Enabled = false;
            Task.Factory.StartNew(() => { int ms; bool ok = Ps5Probe.FtpAnswers(h, p, 2500, out ms); return ok ? ms : -1; }).ContinueWith(t =>
            {
                connBtn.Enabled = true;
                int ms = t.IsFaulted ? -1 : t.Result;
                connResult.ForeColor = ms >= 0 ? Theme.Ok : Theme.Bad;
                connResult.Text = ms >= 0 ? "\u2714 FTP answers (" + ms + " ms)" : "\u2716 No reply on " + h + ":" + p;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Probe()
        {
            string h = host.Text.Trim();
            wakeResult.ForeColor = Theme.Muted; wakeResult.Text = "Asking status from " + h + " (UDP 9302)\u2026";
            probeBtn.Enabled = false;
            Task.Factory.StartNew(() => Ddp.Probe(h, 1500, 3)).ContinueWith(t =>
            {
                probeBtn.Enabled = true;
                var d = t.IsFaulted ? null : t.Result;
                if (d == null) { wakeResult.ForeColor = Theme.Bad; wakeResult.Text = "No reply (PS5 fully off, wrong IP or different network)"; return; }
                wakeResult.ForeColor = d.Standby ? Theme.Blue : d.Awake ? Theme.Ok : Theme.Warn;
                wakeResult.Text = (d.Standby ? Lang.T("In rest mode") : d.Awake ? Lang.T("Awake") : d.Code + " " + d.StatusText) +
                    (d.HostName.Length > 0 ? " \u00B7 " + d.HostName : "") + (d.Firmware.Length > 0 ? " \u00B7 firmware " + d.Firmware : "");
                W.Log("Remote Play status: " + wakeResult.Text);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void TestWake()
        {
            string err;
            if (cred.Text.Trim() != (S.Wake.Credential ?? "") && cred.Text.Trim() != (S.Wake.RegistKey ?? ""))
            {
                if (!ApplyCredential(cred.Text, out err)) { Fail(err); return; }
                if (!Demo.On) { try { SettingsStore.Save(S); } catch { } }
            }
            if (!S.Wake.IsConfigured()) { Fail("Pair the PS5 first, or enter a wake credential."); return; }
            W.Wake(false);
        }

        void Pair()
        {
            if (W.Busy) return;
            string h = host.Text.Trim();
            if (h.Length == 0) { Fail("Inserisci prima l'IP del PS5 (sezione Connection)."); return; }
            if (Demo.On) { DarkDialog.Info(FindForm(), "Demo", "Demo mode does not send anything."); return; }
            W.SetBusy(true);
            pairBtn.Enabled = false;
            wakeResult.ForeColor = Theme.Muted; wakeResult.Text = "Pairing with " + h + "\u2026";
            W.Log("Pairing automatica con " + h + "\u2026");
            string acc = null;
            Task.Factory.StartNew(() => PairHelper.Pair(h, W.Log, out acc)).ContinueWith(t =>
            {
                W.SetBusy(false);
                pairBtn.Enabled = true;
                if (t.IsFaulted)
                {
                    string m = MainWindow.Root(t.Exception);
                    W.Log("ERROR pairing: " + m);
                    wakeResult.ForeColor = Theme.Bad; wakeResult.Text = "Pairing failed";
                    DarkDialog.Info(FindForm(), "Pairing failed", m, Ico.Warning, Theme.Bad);
                    return;
                }
                var r = t.Result;
                var w = S.Wake;
                w.AccountId = acc ?? ""; w.RegistKey = r.RegistKey; w.RpKey = r.RpKey; w.Mac = r.Mac; w.Nickname = r.Nickname;
                w.Credential = RemotePlay.CredentialFromRegistKey(r.RegistKey).ToString();
                w.PairedOn = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                try { SettingsStore.Save(S); } catch (Exception ex) { W.Log("ERROR saving settings: " + ex.Message); }
                W.Log("Pairto a " + (r.Nickname.Length > 0 ? r.Nickname : "PS5") + " (" + r.Mac + "). Wake credential saved.");
                ActivityLog.Add(new ActivityEntry { Kind = "Wake", Title = "Pairing PS5", Detail = "paired with " + r.Nickname, Status = "ok" });
                cred.Text = w.Credential;
                wakeResult.ForeColor = Theme.Ok; wakeResult.Text = "\u2714 Pairto a " + r.Nickname;
                wakeCard.Invalidate();
                W.SettingsSaved();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void PaintWakeStatus(Graphics g)
        {
            var w = S.Wake;
            var r = new Rectangle(18, 48, wakeCard.Width - 36, 46);
            bool ok = w.IsConfigured();
            Theme.FillRound(g, Theme.Alpha(ok ? Theme.Ok : Theme.Muted, 22), r, 6);
            Theme.DrawRound(g, Theme.Alpha(ok ? Theme.Ok : Theme.Muted, 80), r, 6);
            Icons.Draw(g, ok ? Ico.Check : Ico.Power, new RectangleF(r.X + 14, r.Y + 14, 17, 17), ok ? Theme.Ok : Theme.Muted, 1.7f);
            string t = ok
                ? (w.Nickname.Length > 0
                    ? Lang.T("Configured", "Configurata") + " \u00B7 " + Lang.T("paired with ", "associato a ") + w.Nickname
                      + (w.Mac.Length > 0 ? " (" + w.Mac + ")" : "")
                      + (w.PairedOn.Length > 0 ? Lang.T(" on ", " il ") + w.PairedOn : "")
                    : Lang.T("Configured \u00B7 wake credential entered manually", "Configurata \u00B7 codice di accensione inserito a mano"))
                : Lang.T("Not configured \u00B7 pair the PS5 once to wake it from rest mode",
                    "Non configurata \u00B7 associa il PS5 una volta per accenderlo dalla modalit\u00E0 riposo");
            Theme.Text1(g, t, Theme.S13, Theme.Text, new Rectangle(r.X + 42, r.Y, r.Width - 50, r.Height));
        }
    }
}
