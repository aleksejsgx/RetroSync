# RetroSync 2.1.1

Patch release focused on install layouts and release packages. Application features match 2.1; settings storage is smarter when the app is installed for the current user.

## Download

Get the latest files from: https://github.com/aleksejsgx/RetroSync/releases/latest

| Asset | Platform | Notes |
|---|---|---|
| `RetroSync-2.1.1-win-x64-portable.exe` | Windows 10/11 x64 | Single executable. Settings live next to the exe (portable). |
| `RetroSync-2.1.1-win-x64-portable.zip` | Windows 10/11 x64 | Portable exe + README + LICENSE + THIRD_PARTY_NOTICES. |
| `RetroSync-2.1.1-win-x64-setup.exe` | Windows 10/11 x64 | Per-user installer (no admin). Installs to `%LOCALAPPDATA%\Programs\RetroSync`, Start menu shortcut, optional desktop shortcut, uninstaller. Writes `installed.flag` so settings go to `%APPDATA%\RetroSync`. |
| `RetroSync-2.1.1-win-x64-setup.zip` | Windows 10/11 x64 | Setup exe packaged with a short README. |
| `RetroSync-2.1.1-linux-x64-experimental.tar.gz` | Linux x64 + Mono | **Experimental.** Same WinForms exe launched with `retrosync.sh` / Mono. See `README-LINUX.md` inside the archive. |

Checksums: `SHA256SUMS.txt` in this release.

## What’s new in 2.1.1

- **Portable vs installed data directory.** Portable builds keep `RetroSync.settings.json` (and related state) next to the executable. Installer builds drop an `installed.flag` beside the exe; the app then stores settings under `%APPDATA%\RetroSync` so Program Files / LocalAppData install folders need not be writable. If settings already exist next to the exe, that location is kept (existing portable users are unchanged).
- Windows **portable** and **setup** packages, plus an experimental **Linux** tarball.
- Offline tests cover the data-directory resolution rules.

## Requirements (Windows)

- Windows 10/11 with .NET Framework 4.8 (included with modern Windows).
- Jailbroken PS5 on the same LAN (FTP, optional ELF loader on port 9021 for pairing / rest mode).

## Requirements (Linux experimental)

- `mono-complete` (WinForms), X11 display.
- Untested on real Linux desktops; FTP sync, backup mirror, and wake/rest are expected to work; PS2 `mymc` may need Wine/Python.

## Licence

MIT for the application. Helper ELF payloads and CRT notes: see `THIRD_PARTY_NOTICES.txt` and the repository README.
