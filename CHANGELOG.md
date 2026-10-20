# Changelog

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
