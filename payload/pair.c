/* RetroSync pairing helper (MIT licence, see LICENSE in the repository).
 *
 * One-shot homebrew payload for the user's own PS5. It runs once and exits:
 *   1. finds the signed-in user (foreground user, else the first one in the login list) and
 *      reads that user's 8-byte PSN account id from the registry (READ-ONLY);
 *   2. reads (never changes) the "Remote Play enabled" registry flag, to report it;
 *   3. asks the system for a Remote Play PIN with sceRemoteplayGeneratePinCode;
 *   4. shows a console notification and listens on TCP 9303 for up to 120 s;
 *   5. answers ONE client that presents the right one-time token with a JSON line and exits.
 *
 * Token: RetroSync patches 32 random hex digits after the marker "RSYNC-TOKEN-SLOT:" in the
 * ELF before sending it to the ELF loader on port 9021. If the marker is still all zeros (ELF
 * loaded by hand), a random 6-digit code is shown in the notification and is used instead.
 *
 * Protocol (one ASCII line each way):
 *   client -> "RETROSYNC-PAIR 1 <token>\n"
 *   server -> {"ok":true,"account_id":"<base64 8 bytes LE>","pin":"12345678","rp_enabled":1,...}\n
 *   wrong token -> {"ok":false,"error":"auth"}\n, the helper keeps waiting (max 5 attempts).
 *
 * Written from scratch for RetroSync. Registry entry numbers and Sony function names/arity are
 * public facts. It never calls sceRemoteplayConfirmDeviceRegist (confirming before the PC has
 * registered makes the console answer 403) and it never writes the registry. */
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <unistd.h>
#include <stdlib.h>
#include <fcntl.h>
#include <dlfcn.h>
#include <time.h>
#include <errno.h>
#include <sys/socket.h>
#include <sys/select.h>
#include <netinet/in.h>

#define PAIR_PORT    9303
#ifndef WAIT_SECONDS
#define WAIT_SECONDS 120
#endif
#define MAX_TRIES    5

/* Patched by RetroSync. 17-char marker + 32 hex digits + NUL. */
volatile char TOKEN[64] = "RSYNC-TOKEN-SLOT:00000000000000000000000000000000";
#define TOKEN_MARKER_LEN 17
#define TOKEN_LEN        32

/* Registry: per-user entries, user slot 1..16, 0x10000 apart. */
#define REG_USER_BASE_USER_ID    0x07800100u  /* 125829376 */
#define REG_USER_BASE_ACCOUNT_ID 0x07800500u  /* 125830400 */
#define REG_USER_STRIDE          0x10000u
#define REG_REMOTEPLAY_ENABLE    1098973184u
#define RP_ALREADY_INITIALIZED   ((int)0x80FC0003)

int sceUserServiceInitialize(void *params);
int sceUserServiceGetForegroundUser(int *user_id);
int sceUserServiceGetLoginUserIdList(int *ids);   /* 16 entries, unused = -1 */
int sceRegMgrGetInt(uint32_t key, int *val);
int sceRegMgrGetBin(uint32_t key, void *buf, size_t size);

typedef struct { char pad[45]; char message[3075]; } notify_request_t;
int sceKernelSendNotificationRequest(int dev, notify_request_t *req, size_t size, int blocking);

typedef int (*rp_init_fn)(void *opt, size_t opt_size);
typedef int (*rp_pin_fn)(uint32_t *pin);

static void notify(const char *fmt, const char *arg)
{
    notify_request_t req;
    memset(&req, 0, sizeof(req));
    snprintf(req.message, sizeof(req.message), fmt, arg);
    sceKernelSendNotificationRequest(0, &req, sizeof(req), 0);
    printf("[retrosync-pair] %s\n", req.message);
}

static void random_bytes(void *buf, size_t n)
{
    int fd = open("/dev/urandom", O_RDONLY);
    size_t got = 0;
    if (fd >= 0) {
        while (got < n) { ssize_t r = read(fd, (char *)buf + got, n - got); if (r <= 0) break; got += (size_t)r; }
        close(fd);
    }
    if (got < n) {   /* fallback: not cryptographic, only used if /dev/urandom is missing */
        srand((unsigned)time(NULL) ^ (unsigned)getpid());
        for (size_t i = got; i < n; i++) ((unsigned char *)buf)[i] = (unsigned char)rand();
    }
}

static void b64_encode(const unsigned char *in, size_t n, char *out)
{
    static const char t[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t o = 0;
    for (size_t i = 0; i < n; i += 3) {
        uint32_t v = (uint32_t)in[i] << 16 | (i + 1 < n ? (uint32_t)in[i + 1] << 8 : 0) | (i + 2 < n ? in[i + 2] : 0);
        out[o++] = t[(v >> 18) & 63];
        out[o++] = t[(v >> 12) & 63];
        out[o++] = i + 1 < n ? t[(v >> 6) & 63] : '=';
        out[o++] = i + 2 < n ? t[v & 63] : '=';
    }
    out[o] = 0;
}

/* Returns the registry slot (1..16) of the signed-in user, or -1. */
static int find_user_slot(int *user_id_out)
{
    int ids[16], fg = -1, n = 0;
    sceUserServiceInitialize(NULL);
    if (sceUserServiceGetForegroundUser(&fg) == 0 && fg != -1 && fg != 0) ids[n++] = fg;
    int list[16];
    for (int i = 0; i < 16; i++) list[i] = -1;
    if (sceUserServiceGetLoginUserIdList(list) == 0)
        for (int i = 0; i < 16 && n < 16; i++) if (list[i] != -1 && list[i] != 0 && list[i] != fg) ids[n++] = list[i];
    for (int k = 0; k < n; k++)
        for (uint32_t slot = 1; slot <= 16; slot++) {
            int v = 0;
            if (sceRegMgrGetInt(REG_USER_BASE_USER_ID + (slot - 1) * REG_USER_STRIDE, &v) == 0 && v == ids[k]) {
                *user_id_out = ids[k];
                return (int)slot;
            }
        }
    return -1;
}

static int get_pin(uint32_t *pin, char *err, size_t errcap)
{
    static const char *paths[] = { "/system/common/lib/libSceRemoteplay.sprx",
                                   "/system_ex/common/lib/libSceRemoteplay.sprx",
                                   "/system/priv/lib/libSceRemoteplay.sprx" };
    void *lib = NULL;
    for (size_t i = 0; i < sizeof(paths) / sizeof(paths[0]) && !lib; i++) lib = dlopen(paths[i], RTLD_NOW | RTLD_GLOBAL);
    if (!lib) lib = RTLD_DEFAULT;
    rp_init_fn init = (rp_init_fn)dlsym(lib, "sceRemoteplayInitialize");
    rp_pin_fn gen = (rp_pin_fn)dlsym(lib, "sceRemoteplayGeneratePinCode");
    if (!init || !gen) { snprintf(err, errcap, "libSceRemoteplay not available"); return -1; }
    int rc = init(NULL, 0);
    if (rc != 0 && rc != RP_ALREADY_INITIALIZED) { snprintf(err, errcap, "sceRemoteplayInitialize 0x%08X", (unsigned)rc); return -1; }
    rc = gen(pin);
    if (rc != 0) { snprintf(err, errcap, "sceRemoteplayGeneratePinCode 0x%08X", (unsigned)rc); return -1; }
    return 0;
}

static int token_ok(const char *given, const char *expected)
{
    size_t a = strlen(given), b = strlen(expected);
    unsigned diff = (unsigned)(a ^ b);
    for (size_t i = 0; i < b; i++) diff |= (unsigned char)given[i < a ? i : 0] ^ (unsigned char)expected[i];
    return diff == 0 && b > 0;
}

static void send_all(int fd, const char *s)
{
    size_t n = strlen(s), off = 0;
    while (off < n) { ssize_t w = send(fd, s + off, n - off, 0); if (w <= 0) break; off += (size_t)w; }
}

/* Reads one line (max cap-1 chars) with a 10 s limit. */
static int read_line(int fd, char *buf, size_t cap)
{
    size_t n = 0;
    time_t end = time(NULL) + 10;
    while (n + 1 < cap && time(NULL) < end) {
        fd_set rs; FD_ZERO(&rs); FD_SET(fd, &rs);
        struct timeval tv = { 1, 0 };
        if (select(fd + 1, &rs, NULL, NULL, &tv) <= 0) continue;
        char c;
        if (recv(fd, &c, 1, 0) != 1) break;
        if (c == '\n') break;
        if (c != '\r') buf[n++] = c;
    }
    buf[n] = 0;
    return (int)n;
}

int main(void)
{
    char expected[TOKEN_LEN + 1], code[8] = "";
    for (int i = 0; i < TOKEN_LEN; i++) expected[i] = TOKEN[TOKEN_MARKER_LEN + i];
    expected[TOKEN_LEN] = 0;
    if (strspn(expected, "0") == TOKEN_LEN) {          /* not patched: use a code shown on screen */
        uint32_t r; random_bytes(&r, sizeof(r));
        snprintf(code, sizeof(code), "%06u", (unsigned)(r % 1000000u));
        strcpy(expected, code);
    }

    /* Collect the data first (read-only). Errors are reported to the client. */
    char reply[512], err[96] = "", acc_b64[16] = "", pin_txt[12] = "";
    int user_id = -1, rp_enabled = -1;
    int slot = find_user_slot(&user_id);
    if (slot < 0) snprintf(err, sizeof(err), "no signed-in user");
    else {
        unsigned char acc[8] = {0};
        int rc = sceRegMgrGetBin(REG_USER_BASE_ACCOUNT_ID + (uint32_t)(slot - 1) * REG_USER_STRIDE, acc, sizeof(acc));
        int nz = 0; for (int i = 0; i < 8; i++) nz |= acc[i];
        if (rc != 0 || !nz) snprintf(err, sizeof(err), "account id not readable (0x%08X)", (unsigned)rc);
        else b64_encode(acc, 8, acc_b64);
    }
    if (sceRegMgrGetInt(REG_REMOTEPLAY_ENABLE, &rp_enabled) != 0) rp_enabled = -1;
    if (!err[0]) {
        uint32_t pin = 0;
        if (get_pin(&pin, err, sizeof(err)) == 0) snprintf(pin_txt, sizeof(pin_txt), "%08u", (unsigned)(pin % 100000000u));
    }
    if (err[0])
        snprintf(reply, sizeof(reply), "{\"ok\":false,\"error\":\"%s\",\"rp_enabled\":%d}\n", err, rp_enabled);
    else
        snprintf(reply, sizeof(reply), "{\"ok\":true,\"account_id\":\"%s\",\"pin\":\"%s\",\"rp_enabled\":%d,\"user_slot\":%d}\n",
                 acc_b64, pin_txt, rp_enabled, slot);

    int ls = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    setsockopt(ls, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    struct sockaddr_in a; memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET; a.sin_port = htons(PAIR_PORT); a.sin_addr.s_addr = htonl(INADDR_ANY);
    if (ls < 0 || bind(ls, (struct sockaddr *)&a, sizeof(a)) != 0 || listen(ls, 2) != 0) {
        notify("RetroSync: port %s busy, pairing cancelled", "9303");
        return 1;
    }
    if (code[0]) notify("RetroSync: pairing code %s", code);
    else notify("RetroSync: pairing in progress%s", err[0] ? " (error, see PC)" : "...");

    time_t end = time(NULL) + WAIT_SECONDS;
    int tries = 0, done = 0;
    while (!done && tries < MAX_TRIES && time(NULL) < end) {
        fd_set rs; FD_ZERO(&rs); FD_SET(ls, &rs);
        struct timeval tv = { 1, 0 };
        if (select(ls + 1, &rs, NULL, NULL, &tv) <= 0) continue;
        int c = accept(ls, NULL, NULL);
        if (c < 0) continue;
        char line[128];
        read_line(c, line, sizeof(line));
        const char *pfx = "RETROSYNC-PAIR 1 ";
        if (strncmp(line, pfx, strlen(pfx)) == 0 && token_ok(line + strlen(pfx), expected)) {
            send_all(c, reply);
            done = 1;
        } else {
            send_all(c, "{\"ok\":false,\"error\":\"auth\"}\n");
            tries++;
        }
        close(c);
    }
    close(ls);
    if (done) notify(err[0] ? "RetroSync: pairing failed (%s)" : "RetroSync: data sent to PC%s", err[0] ? err : "");
    else notify("RetroSync: pairing timed out%s", "");
    return done && !err[0] ? 0 : 1;
}
