#!/bin/bash
# Offline tests for the remote power-on code. Needs mono (mcs) and node; playactor sources for the reference keys:
#   git clone https://github.com/dhleong/playactor /tmp/playactor
set -e
cd "$(dirname "$0")/.."
KEYS=${PLAYACTOR_KEYS:-/tmp/playactor/src/remoteplay/crypto/keys.ts}
REF=$(node tests/playactor_ref.js "$KEYS" 3e7e7a825973adab2f694346bd44dab5 12345678 FWbzJ0XoRBE=)
ACC=$(node -e 'const b=Buffer.alloc(8);b.writeBigUInt64LE(1234567890123456789n);console.log(b.toString("base64"))')
mcs -langversion:5 -sdk:4.5 -out:/tmp/retrosync-waketests.exe -main:RetroSync.WakeTests -nowarn:169,219,414,649 \
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Core.dll -r:System.Web.Extensions.dll -resource:src/retrosync-pair.elf,retrosync-pair.elf -resource:src/retrosync-rest.elf,retrosync-rest.elf src/*.cs tests/WakeTests.cs
mono /tmp/retrosync-waketests.exe $(echo "$REF" | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d["seed"],d["iv"],d["body"],d["reply"])') "$ACC"
bash "$(dirname "$0")/helper_host/run.sh"
