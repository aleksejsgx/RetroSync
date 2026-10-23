# Changelog

## 2.2.0 — 2026-10

### New
- **Dolphin ↔ Porpoise** (GameCube / Wii) save sync: GCI files per region (`GC/<EUR|USA|JAP>/Card A`) matched by GCI header game code + maker + internal file name; optional Wii `title/.../data` sync; history + newest-wins with time tolerance; backups under `<BackupRoot>\Dolphin`. New sidebar page, Settings card, Dolphin folder backup module and CLI preview (`--dry-run --dolphin`).
- **Start with Windows** (HKCU Run entry, starts hidden with `--minimized`) and **minimize / close to the notification area** with a tray menu (Open, Sync saves, Dolphin saves, Exit).

### Fixed
- RetroArch / Opera (3DO): games found inside a core sub tree (`opera/per_game/<game>.0.srm` on the PC, `Opera/opera/per_game/...` on the PS5) are now indexed to the Opera core, so their flat savestates and other per-game files resolve to the right PS5 core folder.

### Changed
- Version shown from a single constant (`Program.Version`); assembly version metadata added.

## 2.1.1 – 2026-10

### Changed
- Settings and state use a writable **data directory**: next to the exe when portable; `%APPDATA%\RetroSync` when `installed.flag` is present or the exe folder is not writable. Existing portable installs with settings beside the exe are unchanged.
- Release packages: Windows portable exe/zip, per-user NSIS setup, experimental Linux Mono tarball.

### Tests
- Offline coverage for data-directory resolution (portable / installed.flag / non-writable).

## 2.1.0 – 2026-10

### New
- Automatic PS5 pairing (*Pair PS5*) via a one-shot helper payload.
- Rest mode button (*Rest mode*) via a one-shot helper (standby only).
- UI language: English default; Italian optional (Settings → Advanced).

### Changed
- Default settings use empty paths and no personal IP (first-run setup).
- Public docs and screenshots use placeholder data only.

## 2.0.0 – 2026-10

- Unified dark UI, RetroArch/PS2/Backup pages, remote wake, dry-run, CLI `--wake`.
