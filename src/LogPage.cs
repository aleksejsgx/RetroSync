using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RetroSync
{
    class LogPage : Page
    {
        readonly CardPanel card;
        readonly TextBox box;
        readonly DarkButton copy, saveBtn, clear, folder;

        public LogPage(MainWindow w) : base(w, "log", "Log")
        {
            card = new CardPanel();
            Controls.Add(card);
            box = Ui.LogBox(card);
            box.WordWrap = false; box.ScrollBars = ScrollBars.Both; box.BackColor = Theme.Card;
            copy = new DarkButton("Copia", BtnKind.Normal, Ico.Save, true);
            copy.Click += (s, e) => { try { if (box.TextLength > 0) Clipboard.SetText(box.Text); } catch { } };
            saveBtn = new DarkButton("Save su file\u2026", BtnKind.Normal, Ico.Download, true);
            saveBtn.Click += (s, e) => SaveLog();
            clear = new DarkButton("Svuota", BtnKind.Ghost, Ico.Close, true);
            clear.Click += (s, e) => { W.LogLines.Clear(); box.Clear(); };
            folder = new DarkButton("Open backup folder", BtnKind.Normal, Ico.Folder, true);
            folder.Click += (s, e) => { try { Directory.CreateDirectory(S.BackupRoot); Process.Start("explorer.exe", "\"" + S.BackupRoot + "\""); } catch (Exception ex) { W.Log("ERROR: " + ex.Message); } };
            Controls.Add(copy); Controls.Add(saveBtn); Controls.Add(clear); Controls.Add(folder);
            w.LogAdded += line => { box.AppendText(line + "\r\n"); };
        }

        public override string Subtitle { get { return "Everything RetroSync does, in order (including PS2 memory card lines, marked [PS2])"; } }

        public override void Entered()
        {
            if (box.Lines.Length != W.LogLines.Count) { box.Text = string.Join("\r\n", W.LogLines) + (W.LogLines.Count > 0 ? "\r\n" : ""); }
            box.SelectionStart = box.TextLength; box.ScrollToCaret();
        }

        protected override void DoLayout()
        {
            int x = 0;
            foreach (var b in new[] { copy, saveBtn, clear }) { b.Location = new Point(x, 0); x += b.Width + 8; }
            folder.Location = new Point(Width - folder.Width, 0);
            card.SetBounds(0, 40, Width, Height - 40);
            box.SetBounds(14, 12, card.Width - 20, card.Height - 20);
        }

        void SaveLog()
        {
            using (var d = new SaveFileDialog { Filter = "Text|*.txt", FileName = "RetroSync-log-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt" })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try { File.WriteAllLines(d.FileName, W.LogLines.ToArray()); W.Log("Log salvato in " + d.FileName); }
                catch (Exception ex) { DarkDialog.Info(FindForm(), "Could not save", ex.Message, Ico.Warning, Theme.Warn); }
            }
        }
    }
}
