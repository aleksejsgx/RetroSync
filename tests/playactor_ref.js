// Reference implementation of the PS5 registration body, following playactor (ISC) base.ts/modern.ts,
// using Node's own crypto (aes-128-cfb, hmac-sha256). Used only to cross-check RetroSync's C# code.
const fs = require("fs"), crypto = require("crypto");
const src = fs.readFileSync(process.argv[2], "utf8");
function table(name) {
  const start = src.indexOf("export const " + name);
  const ps5 = src.indexOf("[DeviceType.PS5]: [", start);
  const end = src.indexOf("]", ps5 + 20);
  return Buffer.from(src.slice(ps5 + 19, end).match(/0x[0-9a-fA-F]{2}/g).map(x => parseInt(x, 16)));
}
const INIT = table("INIT_KEYS"), AERO = table("AERO_KEYS");
const nonce = Buffer.from(process.argv[3], "hex"), pin = parseInt(process.argv[4], 10), account = process.argv[5];
const preface = Buffer.alloc(480).fill("A");
const counter = Buffer.alloc(8); counter.writeBigUInt64BE(0n);
const iv = crypto.createHmac("sha256", Buffer.from("464687b349ca8ce859c5270f5d7a69d6", "hex")).update(nonce).update(counter).digest().slice(0, 16);
const off = preface[0x18D] & 0x1F, seed = Buffer.alloc(16);
for (let i = 0; i < 16; i++) seed[i] = INIT[(i * 0x20 + off) % INIT.length];
seed[0xc] ^= (pin >> 24) & 0xff; seed[0xd] ^= (pin >> 16) & 0xff; seed[0xe] ^= (pin >> 8) & 0xff; seed[0xf] ^= pin & 0xff;
const aoff = preface[0] >> 3, aero = Buffer.alloc(16);
for (let i = 0; i < 16; i++) aero[i] = (nonce[i] ^ AERO[i * 0x20 + aoff]) + (-0x2d & 0xff) + i;
aero.copy(preface, 0xc7, 8, 16); aero.copy(preface, 0x191, 0, 8);
const rec = Buffer.from("Client-Type: dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f\r\nNp-AccountId: " + account + "\r\n");
const c = crypto.createCipheriv("aes-128-cfb", seed, iv); c.setAutoPadding(false);
const body = Buffer.concat([preface, c.update(rec)]);
// also: encrypt a fake console reply with a fresh cipher (what the console sends back)
const reply = Buffer.from("AP-Ssid: x\r\nPS5-Mac: a1b2c3d4e5f6\r\nPS5-RegistKey: 3132333435363738\r\nRP-Key: 00112233445566778899aabbccddeeff\r\nPS5-Nickname: PS5-TEST\r\n");
const c2 = crypto.createCipheriv("aes-128-cfb", seed, iv); c2.setAutoPadding(false);
console.log(JSON.stringify({ seed: seed.toString("hex"), iv: iv.toString("hex"), body: body.toString("hex"), reply: c2.update(reply).toString("hex") }));
