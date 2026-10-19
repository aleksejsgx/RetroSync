# PS5Upload pairing/wake payload — research (2026-10-20)

Source examined: https://github.com/phantomptr/ps5upload (main @ 912fd7c, 2026-10-19 23:45 Europe/Rome, VERSION 7.1.10), cloned at /workspace/ps5upload.

## TL;DR
There are TWO separate codes, and people mix them up:
1. **AVA1 6-digit pairing code**, shown as a PS5 notification. It is a random CSPRNG number. It is the password for a CPace PAKE that pairs the PC with the PS5Upload *helper* (port 9120). It does **not** encode the account ID and is never sent over the wire.
2. **The Remote Play 8-digit PIN**. The payload gets it from `sceRemoteplayGeneratePinCode`. The user never has to type it: the PIN goes to the PC over AVA1, along with the account ID that the payload reads from the **registry (sceRegMgr)**.
The **PC side** (Rust engine) then runs the normal Remote Play registration (TCP 9295, ported from playactor). It gets `PS5-RegistKey` + `RP-Key` back and turns the RegistKey into the DDP wake `user-credential` (UDP 9302).

## 1. Repo and files
- Payload (C, PS5 Payload SDK): `payload/src/remoteplay.c`, `payload/src/rp_pair.c`, `payload/include/rp_keys.h`, `payload/src/sys_registry.c`, `payload/src/notif.c`, `payload/src/mgmt_table.def`, `payload/ava1/ava1_server.c`
- Protocol spec: `protocol/ava1/SPEC.md` (§4.6 pairing PAKE, §5.5)
- PC side: `engine/crates/ps5upload-core/src/rp_regist.rs` (registration + credential), `.../remoteplay.rs` (AVA1 mgmt calls), `engine/crates/ps5upload-engine/src/lib.rs` (`POST /api/ps5/power/pair` → `pair_with_console`), `client/src/lib/wakeState.ts`, `client/src/screens/Connection/WakeSetup.tsx`

## 2. How it works
### Account ID (payload/src/remoteplay.c)
It calls `sceUserServiceGetForegroundUser`. If that returns no user, it falls back to `sceUserServiceGetLoginUserIdList`. It then finds the registry slot (1..16) whose user-id key matches, and reads the 8-byte account ID from the registry with `sceRegMgr` (dlsym'd, read-only):
```c
static uint32_t rp_key_user_id(uint32_t slot)    { return rp_regmgr_ent_num(slot,16,65536,125829376,127140096); }
static uint32_t rp_key_account_id(uint32_t slot) { return rp_regmgr_ent_num(slot,16,65536,125830400,127141120); }
...
if (sys_registry_get_bin(rp_key_account_id((uint32_t)slot), raw, sizeof(raw), NULL) != 0) return;
rp_base64_encode(raw, sizeof(raw), b64);   // standard base64 with '=' padding
```
(Entry number = (slot-1)*65536 + base. The bases come from ps5-payload-dev/regdump. The code is ported from elf-arsenal, with its base64 padding bug fixed.) An all-zero ID means the account is local-only and not activated, so it cannot pair.

### PIN (payload/src/rp_pair.c + remoteplay.c)
The payload dlsyms `sceRemoteplayInitialize`, `sceRemoteplayGeneratePinCode`, `sceRemoteplayConfirmDeviceRegist` and `sceRemoteplayNotifyPinCodeError`:
```c
snprintf(p->pin, sizeof(p->pin), "%08u", (unsigned)(pin % 100000000u));
snprintf(msg, sizeof(msg), "[ps5upload] Remote Play PIN: %s", p->pin);
notify(ops, msg, 0);   // also shown as a console notification
```
The AVA1 reply to `RP_REQUEST` is JSON: `{"state":..,"pin":"12345678","account_id":"<b64>",...}`. There is also `RP_READINESS`, which includes `account_id_b64`, `account_id_raw`, the service and per-user flags, and the activated state. `RP_ENABLE` writes the Remote Play toggles to the registry (system-wide and, on FW ≥10.00, per-user). See `mgmt_table.def` lines 126-131.
Gotcha (rp_regist.rs): do NOT poll status before registering. Polling calls ConfirmDeviceRegist, which finalises the pending registration, and the console then answers HTTP 403 with reason 80108b03.

### Transport
AVA1 runs on TCP 9120. It uses a Noise handshake, a custom binary framing with generated codecs, and up to 8 lanes. The PC is trusted through the 6-digit CPace pairing (SPEC §4.6): `d = BLAKE2b-256("AVA1 CPace"‖h‖code)`, Elligator2 → X25519, then MAC confirmation. If the app sent the helper itself through the ELF loader on 9021, it "pairs by itself" with no code shown. A helper loaded any other way shows the 6-digit code (`ava1_server.c:1207,1268`).

### Registration + credential (PC, rp_regist.rs)
`pair_with_console()` runs: readiness → enable RP if needed → cancel → `remoteplay_request` (PIN + account) → `register(host, account_id_b64, pin)`. That is one HTTP POST to 9295: 480 bytes of padding followed by an AES-128-CFB record keyed from the PIN (keys PS5_AERO_KEY/PS5_INIT_KEY, from playactor, ISC). The reply is parsed for `PS5-RegistKey` and `RP-Key`. Then:
`credential = credential_from_regist_key(regist_key)`: the RegistKey hex decodes to ASCII text, which is itself hex; parsed as a u64, that number is the DDP `user-credential` (same logic as `client/src/lib/wakeState.ts:137`).

## 3. Standalone or tied to PS5Upload?
It is tied to PS5Upload. The RP code is one module inside the monolithic helper `ps5upload.elf`, which also does transfers, mounts, cheats, fan control, a web UI and more. It is reachable only through AVA1 (Noise + CPace + generated codecs). There is no plain-text or JSON port. Another app would have to implement the AVA1 client (spec + Rust crate `engine/crates/ava1`) and complete the pairing.

## 4. Licences
The whole repo (payload, engine and client) is **GPL-3.0-or-later** (`LICENSE`, `engine/Cargo.toml` `license = "GPL-3.0-or-later"`). The extra licences in `LICENSES/` cover UnRAR only. The registration code is credited to playactor (ISC); chiaki (AGPL) was read but "never copied". The registry-reading code is credited to elf-arsenal (git.etawen.dev/soniciso/elf-arsenal; its licence was not checked).

## 5. Lifetime
The helper is **resident** until the PS5 reboots or goes to rest ("The helper stays until the PS5 reboots or enters rest mode"). But wake pairing only needs it once, while the console is awake: the credential then lives on the PC and waking needs no payload ("Needs no credential and no payload" applies to DDP status, and wake uses only the stored credential). In practice, pairing is a one-shot step.

## 6. What gets stored (client)
- `credential`: decimal u64 from the RegistKey. Used for the DDP `WAKEUP` `user-credential` on UDP 9302. This is enough for a wake that lands on user-select.
- `regist_key` + `rp_key`: hex, padded to 16 bytes. Optional; used for "Wake & sign in", which runs a Remote Play control session after boot (`rp_session.rs`).
- The account ID itself is not needed after registration.

## Options for RetroSync
### (a) Reuse ps5upload.elf as-is
- RetroSync would have to send the ELF to 9021 and then speak AVA1 from C#: Noise handshake, CPace with Elligator2 and BLAKE2b, AVA1 framing, and the mgmt calls RP_READINESS/RP_ENABLE/RP_REQUEST/RP_CANCEL. That is a large port with nothing ready-made in .NET.
- Licence: running their unmodified binary as a separate program over a network protocol is generally "mere aggregation". Bundling and redistributing the ELF is allowed if we ship GPL notices and offer the source. Porting their C/Rust code (AVA1, rp_regist.rs) into RetroSync would make RetroSync GPL.
- Trust: the helper is a large, privileged, resident payload (kernel R/W, registry writes, file ops). Users would be running much more than RetroSync needs. It also changes often (the 6.0 app cannot talk to a 5.x helper), so we would be tied to its versions.
- Verdict: not recommended, except as an optional "if ps5upload helper is already running" path.

### (b) Our own minimal payload (recommended)
A ~300-line C ELF using ps5-payload-dev/sdk, sent to the ELF loader on 9021. It is one-shot:
1. `sceUserServiceInitialize`, then foreground user or login list → slot (registry key user-id base 125829376) → 8-byte account ID (base 125830400) through `sceRegMgrGetBin`. Re-implement this from the public regdump/regmgr constants rather than copying GPL code.
2. Optionally check or enable Remote Play (key 1098973184; per-user 125859841 on FW ≥10.00), or just report its state and let the user enable it.
3. `sceRemoteplayInitialize` + `sceRemoteplayGeneratePinCode` (libSceRemoteplay via dlsym).
4. Generate a random 6-digit code and show it with `sceKernelSendNotificationRequest` ("RetroSync code: 123456").
5. Listen once on a TCP port (e.g. 9303) for up to 120 s. The PC sends the code; the payload replies with JSON `{account_id_b64, pin}`, then exits. Protect it with something simple: e.g. HMAC-SHA256 challenge-response keyed by the code, then AES-GCM with a key derived from the code (HKDF). The values travel over a LAN only once, and the PIN is single-use and expires in about 5 minutes. A plain code check plus encryption keyed from the code is acceptable. Do not poll ConfirmDeviceRegist.
6. RetroSync (C#) then does the registration on 9295 itself (implement from playactor (ISC) or our existing RESEARCH_wake.md notes), derives the credential from the RegistKey exactly as above, and stores credential (+ RegistKey/RP-Key if we want sign-in).
Flow: the user loads the jailbreak and ELF loader → RetroSync "Pair" sends the ELF → the console shows the code → the user types the code → done. The payload exits and nothing stays resident. Licence is our choice (e.g. MIT) as long as we don't copy GPL code. The constants and API names are facts.
Simpler variant: skip the code entirely. Since RetroSync sends the ELF itself, embed a random per-session token in the ELF's data/args before sending, the way ps5upload "pairs by itself". The code is then only needed when the user loads the ELF by other means.
