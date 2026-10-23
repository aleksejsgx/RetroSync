# RetroSync 2.2.0 (draft)

> Draft: not published yet. Test build only.

## What's new in 2.2.0

### GameCube / Wii: Dolphin ↔ Porpoise
- New **GameCube / Wii** page: syncs saves between [Dolphin](https://dolphin-emu.org/) on Windows and [Porpoise](https://github.com/elripalda/Porpoise-Dolphin-Emulator-for-PS5) (Dolphin port for PS5) over FTP.
- GameCube saves are synced as **per-game GCI files** (Dolphin *GCI folder* mode) in `User/GC/<EUR|USA|JAP>/Card A/`.
- Files are matched by the **GCI header** (game code, maker, internal file name), not the file name, so `burnout_2_point_of_impact_pal.gci` (PC) and `51-GB4P-savegame.dat.gci` (PS5) are recognised as the same save.
- Optional **Wii** save data sync (`User/Wii/title/<type>/<id>/data`, disc titles only by default).
- Same rules as the other systems: history-based change detection, newest wins outside the time tolerance, conflicts shown, **backup before every overwrite** (`<BackupRoot>\Dolphin\<timestamp>`).
- Savestates are **not** synced (different Dolphin builds are not state-compatible).
- New Dolphin folder backup module on the Backup page, and a CLI preview: `RetroSync.exe --dry-run --saves --dolphin`.
- Tested only with Dolphin (PC) and Porpoise (PS5); other GameCube/Wii emulators are not tested or supported.

### Tray and autostart
- *Settings → Advanced*: **Start with Windows** (starts hidden in the notification area) and **Minimize / close to the notification area**. Tray menu: Open, Sync saves, Dolphin saves, Exit.

### Fixes
- RetroArch / Opera (3DO): per-game saves inside the core's own sub tree (`opera/per_game`) now also teach RetroSync which core the game belongs to, so 3DO savestates and per-game files go to the right PS5 core folder (`Opera/`).

## Upgrade notes
- Settings gain a `Dolphin` section and three `Ui` options; older settings files load unchanged.
- Dolphin must use **GCI folder** memory cards (Config → GameCube → Slot A: *GCI Folder*). A `.raw` memory card image is not synced.
- Close Dolphin on the PC and Porpoise on the PS5 before applying a sync.
