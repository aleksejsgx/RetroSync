using System.Drawing;

// Emulator modules: each one contributes a sidebar entry + page (and optionally a folder backup module).
// To add an emulator: implement IEmulatorModule, add it to Modules.All, and (optionally) an IBackupModule
// to FolderBackup.Modules. The sync engines stay independent of the UI.
namespace RetroSync
{
    interface IEmulatorModule
    {
        string Id { get; }
        string NavTitle { get; }
        Ico Icon { get; }
        bool Available { get; }       // false = shown greyed out with "soon"
        Page CreatePage(MainWindow w);
    }

    class RetroArchModule : IEmulatorModule
    {
        public string Id { get { return "retroarch"; } }
        public string NavTitle { get { return "RetroArch"; } }
        public Ico Icon { get { return Ico.Gamepad; } }
        public bool Available { get { return true; } }
        public Page CreatePage(MainWindow w) { return new RetroArchPage(w); }
    }

    class Ps2Module : IEmulatorModule
    {
        public string Id { get { return "ps2"; } }
        public string NavTitle { get { return "PS2"; } }
        public Ico Icon { get { return Ico.Card; } }
        public bool Available { get { return true; } }
        public Page CreatePage(MainWindow w) { return new Ps2Page(w); }
    }

    class DolphinModule : IEmulatorModule
    {
        public string Id { get { return "dolphin"; } }
        public string NavTitle { get { return "Dolphin"; } }
        public Ico Icon { get { return Ico.Dolphin; } }
        public bool Available { get { return false; } }
        public Page CreatePage(MainWindow w) { return null; }
    }

    static class Modules
    {
        public static readonly IEmulatorModule[] All = { new RetroArchModule(), new Ps2Module(), new DolphinModule() };
    }
}
