#!/bin/bash
# Builds payload/pair.c for Linux against stubs and checks the real helper's network protocol.
set -e
cd "$(dirname "$0")"
cc -Wall -O1 -o /tmp/pair-host ../../payload/pair.c stubs.c
cc -Wall -O1 -DWAIT_SECONDS=3 -o /tmp/pair-host-t ../../payload/pair.c stubs.c
python3 - <<'PY'
import socket, subprocess, time, json, os, re
data = open('/tmp/pair-host','rb').read()
tok = os.urandom(16).hex().encode()
i = data.index(b'RSYNC-TOKEN-SLOT:'); assert data.count(b'RSYNC-TOKEN-SLOT:') == 1
open('/tmp/pair-host-p','wb').write(data[:i+17] + tok + data[i+17+32:]); os.chmod('/tmp/pair-host-p', 0o755)
ok = True
def check(n, c, info=''):
    global ok; print(('PASS  ' if c else 'FAIL  ') + n, info); ok &= bool(c)
def talk(line):
    for _ in range(50):
        try: s = socket.create_connection(('127.0.0.1', 9303), 1); break
        except OSError: time.sleep(0.1)
    s.sendall(line); r = s.makefile().readline(); s.close(); return r
p = subprocess.Popen(['/tmp/pair-host-p'], stderr=subprocess.PIPE, text=True)
r1 = talk(b'RETROSYNC-PAIR 1 ' + b'0'*32 + b'\n')
check('helper(C): wrong token refused', json.loads(r1) == {'ok': False, 'error': 'auth'}, r1.strip())
r2 = json.loads(talk(b'RETROSYNC-PAIR 1 ' + tok + b'\n'))
check('helper(C): account id base64 LE + PIN', r2.get('ok') is True and r2['account_id'] == 'FYHpffQQIhE=' and r2['pin'] == '02468135' and r2['user_slot'] == 3 and r2['rp_enabled'] == 1, r2)
rc = p.wait(5); err = p.stderr.read()
check('helper(C): exits after the reply (rc 0)', rc == 0, rc)
check('helper(C): notifications shown', 'pairing in progress' in err and 'data sent' in err, err.replace('\n', ' / '))
# unpatched ELF -> 6-digit code shown, accepted
p = subprocess.Popen(['/tmp/pair-host'], stderr=subprocess.PIPE, text=True)
time.sleep(0.3)
line = ''
while 'pairing code' not in line and 'codice' not in line: line = p.stderr.readline()
code = re.search(r'(\d{6})', line).group(1)
r3 = json.loads(talk(b'RETROSYNC-PAIR 1 ' + code.encode() + b'\n'))
check('helper(C): hand-loaded ELF uses on-screen code', r3.get('ok') is True and p.wait(5) == 0, code)
# no user -> error JSON
env = dict(os.environ, STUB_NOUSER='1')
p = subprocess.Popen(['/tmp/pair-host-p'], stderr=subprocess.DEVNULL, env=env)
r4 = json.loads(talk(b'RETROSYNC-PAIR 1 ' + tok + b'\n')); p.wait(5)
check('helper(C): no signed-in user -> error', r4 == {'ok': False, 'error': 'no signed-in user', 'rp_enabled': 1}, r4)
t0=time.time(); rc=subprocess.run(['/tmp/pair-host-t'],stderr=subprocess.PIPE,text=True,timeout=20)
check('helper(C): exits on timeout without client (rc 1)', rc.returncode == 1 and 'timed out' in rc.stderr and time.time()-t0 < 6, round(time.time()-t0,1))
print('HELPER OK' if ok else 'HELPER FAILED'); exit(0 if ok else 1)
PY
# rest-mode helper
cc -Wall -O1 -DDELAY_SECONDS=0 -o /tmp/rest-host ../../payload/rest.c rest_stubs.c
python3 - <<'PY2'
import subprocess, os, time
ok=True
def check(n,c,info=""):
    global ok; print(("PASS  " if c else "FAIL  ")+n, info); ok &= bool(c)
r=subprocess.run(["/tmp/rest-host"],stderr=subprocess.PIPE,text=True,timeout=5)
check("rest(C): EnterStandby called after notify", r.returncode==0 and ("rest mode" in r.stderr or "modalit" in r.stderr) and "EnterStandby" in r.stderr, r.stderr.replace("\n"," / "))
env=dict(os.environ, STUB_FAIL="1")
r=subprocess.run(["/tmp/rest-host"],stderr=subprocess.PIPE,text=True,timeout=5,env=env)
check("rest(C): standby failure reported", r.returncode==1 and "refused" in r.stderr, r.stderr.replace("\n"," / "))
print("REST OK" if ok else "REST FAILED"); raise SystemExit(0 if ok else 1)
PY2
