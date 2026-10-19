# Remote power-on of a PS5 – research notes (RetroSync 2.0)

Date: 2026-10-19. Sources were read from public GitHub (git clone / raw files), not through any account connector.

## Sources and licences

| Project | What it is | Licence | How RetroSync uses it |
|---|---|---|---|
| **PS5Upload** (`phantomptr/ps5upload`, v7.1.6, commit `6e9aba0`, 2026-10-19 05:25 Europe/Rome) – `engine/crates/ps5upload-core/src/ddp.rs`, `rp_regist.rs`, `rp_session.rs`, `client/src/lib/consolePower.ts`, `wakeState.ts`, FAQ “Can I wake the PS5…” | Full PS5 manager with Wake / Wake & sign in | GPL-3.0 | **Behaviour only.** Read to understand the flow, the console settings checklist and the UX (status, retries). No code copied (RetroSync is MIT). |
| **iharosi/ps5-wake** (descends from ps4-wake by Darryl Sokoloski) | Small DDP wake tool | GPL-3.0 | Behaviour only (packet format of SRCH / WAKEUP). |
| **playactor** (`dhleong/playactor`, v0.4.1, commit `93ee087`) – `src/remoteplay/crypto/*`, `src/discovery/*` | Node PS4/PS5 controller | ISC (declared in `package.json`, author Daniel Leong; the repo has no LICENSE file) | Algorithm reimplemented in C#. The two PS5 key tables (`PS5_INIT_KEY`, `PS5_AERO_KEY`, 2 × 448 bytes) are **data copied** from `keys.ts`, allowed by ISC and credited in `THIRD_PARTY_NOTICES.txt`. Used as the reference implementation in the offline tests. |
| **chiaki / chiaki-ng** | Remote Play client | AGPL-3.0 | Read to confirm behaviour (error codes, headers). Nothing copied. |

PS5Upload says the same in its own sources: its registration is “ported from playactor (ISC), cross-checked against chiaki… never copied”. RetroSync follows the same rule.

## How it works

1. **Discovery – UDP 9302 (DDP, protocol version `00030010`).**
   `SRCH * HTTP/1.1\ndevice-discovery-protocol-version:00030010\n` + NUL.
   The console answers `HTTP/1.1 200 Ok` (awake) or `HTTP/1.1 620 Server Standby` (rest mode), plus `host-id` (MAC), `host-name`, `host-type: PS5`, `system-version` (e.g. `09600004` → 9.60), `running-app-*`. No credential needed. Nothing answers if Remote Play is off or the console is fully off.
2. **Wake – UDP 9302.**
   `WAKEUP * HTTP/1.1\nclient-type:vr\nauth-type:R\nmodel:w\napp-type:r\nuser-credential:<decimal>\ndevice-discovery-protocol-version:00030010\n` + NUL. No acknowledgement: success is seen by polling SRCH (620 → 200) and then the FTP payload port.
   **A PS5 ignores Wake-on-LAN magic packets**, so DDP is the only network path.
3. **The `user-credential`** is derived from the `PS5-RegistKey` of a Remote Play registration: hex string → bytes → ASCII text → parse as hex u64 → print in decimal.
4. **Registration (pairing) – done once.**
   - UDP `SRC3` → `RES3` on 9295 (announce), then
   - TCP 9295: `POST /sie/ps5/rp/sess/rgst HTTP/1.1` with `HOST`, `User-Agent: remoteplay Windows`, `Connection: close`, `Content-Length`, `RP-Version: 1.0`.
   - Body = 480 bytes of preface (fixed `A` bytes with a nonce/seed spliced in at offsets taken from the preface itself: `initKeyOff = padding[0x18D] & 0x1F`, `aeroKeyOff = padding[0] >> 3`), followed by an AES-128-CFB128 record
     `Client-Type: dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f\r\nNp-AccountId: <base64 of 8-byte little-endian PSN account id>\r\n`.
   - Key = from `PS5_INIT_KEY` + the 8-digit **PIN** shown on the PS5 (Settings › System › Remote Play › Link Device); IV = first 16 bytes of HMAC-SHA256(fixed key, nonce ‖ counter).
   - The reply body is decrypted with a fresh cipher and contains `PS5-RegistKey`, `RP-Key`, `PS5-Mac`, `PS5-Nickname`, `AP-Name`. Errors come back as `RP-Application-Reason` (e.g. `80108b09` = wrong/expired PIN).
5. **Wake & sign in** (PS5Upload `rp_session.rs`): an extra Remote Play session using `RP-Key` that signs the user in straight to the home screen. **Not implemented in 2.0** (much bigger protocol, and FTP only needs the console awake with the payload loaded). RetroSync stores `RpKey` so it can be added later.

### What the PS5 needs (console settings)

- Settings › System › Remote Play › **Enable Remote Play**
- Settings › System › Power Saving › Features Available in Rest Mode › **Stay Connected to the Internet**
- same menu › **Enable Turning On PS5 from Network**

Without any of them the wake fails silently. The app shows this checklist.

### The PSN account ID

Registration needs the numeric PSN account ID. PS5Upload reads it out of the console registry through its own payload; RetroSync has no code on the console (only the FTP payload), so **the user types it once**. Accepted formats: decimal (e.g. `1234567890123456789`) or the base64 form (`FYHpffQQIhE=`) printed by Remote Play tools. Alternative: paste a `user-credential` / `PS5-RegistKey` obtained from another Remote Play tool (chiaki-ng, PS5Upload) and skip pairing.

## What RetroSync 2.0 implements (`src/RemoteWake.cs`)

- `Ddp.Probe` / `Ddp.Wake` (status chips, “Accendi PS5”).
- `RemotePlay.Register(host, accountId, pin)` → `RpRegistration` (Impostazioni › Accensione remota › Associa). Reads the reply as soon as `Content-Length` bytes arrived (does not wait for the socket close); binds UDP 9295 locally like the official client, falls back to an ephemeral port.
- `RemotePlay.CredentialFromRegistKey`.
- `Ps5Probe.WakeAndWait`: WAKEUP ×3, then polls FTP (and SRCH for status text) every 1.5 s up to `WaitSeconds` (default 180), resends WAKEUP every 20 s while still in rest mode, then waits `SettleSeconds` (default 8) for the payload. Cancelable from the GUI.
- “Accendi e sincronizza” = WakeAndWait, then the normal v1 preview → confirm → apply flow (scope from `Wake.AfterWake`: saves / config / all / none).
- CLI `--wake` (e.g. scheduled `RetroSync.exe --backup-ps5 --wake`), exit code 3 if the PS5 does not come up.

## Verification done on the box (no PS5 reachable)

`tests/run_tests.sh` – 21 offline checks, all passing:
- seed and IV derivation, and the full encrypted registration body are **byte-identical** to playactor's own code run under Node with the same nonce / PIN / account (`tests/playactor_ref.js`);
- reply decryption, credential conversion, account-id encoding, WAKEUP/SRCH packet format, DDP reply parsing;
- a fake console on 127.0.0.2 (UDP 9302 + 9295, TCP 9295): probe in rest mode, WAKEUP received with the credential, awake after wake, full registration (console decrypts the account id, keys parsed, wrong PIN → readable Italian error), wake-and-wait detects the FTP port coming up, cancel works.

## Open points / risks

- Not yet tried against a real PS5. The first real test should be: Verifica stato (SRCH) → Associa (PIN) → put PS5 in rest mode → Prova accensione.
- The FTP payload must start again after rest mode. If the jailbreak/payload does not survive rest mode, the console wakes but FTP never answers (RetroSync then shows “Il PS5 non risponde – nessuna risposta FTP entro N s”, and the status chip says “acceso ma il server FTP non risponde”).
- Keys (`RegistKey`, `RpKey`) are stored in plain text in `RetroSync.settings.json`, like the rest of the settings.
