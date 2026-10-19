/* Host (Linux) stubs of the Sony calls used by payload/pair.c, to test its protocol on the box. */
#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
typedef struct { char pad[45]; char message[3075]; } notify_request_t;
int sceUserServiceInitialize(void *p) { (void)p; return 0; }
int sceUserServiceGetForegroundUser(int *u) { *u = getenv("STUB_NOUSER") ? -1 : 0x1234; return 0; }
int sceUserServiceGetLoginUserIdList(int *ids) { for (int i = 0; i < 16; i++) ids[i] = -1; return 0; }
int sceRegMgrGetInt(uint32_t key, int *v) {
    if (key == 0x07800100u + 2 * 0x10000u) { *v = 0x1234; return 0; }        /* user lives in slot 3 */
    if (key == 1098973184u) { *v = 1; return 0; }
    *v = 0; return 0;
}
int sceRegMgrGetBin(uint32_t key, void *buf, size_t n) {
    if (key != 0x07800500u + 2 * 0x10000u || n != 8) return -1;
    uint64_t acc = 1234567890123456789ULL; memcpy(buf, &acc, 8); return 0;
}
int sceKernelSendNotificationRequest(int d, notify_request_t *r, size_t s, int b) { (void)d; (void)s; (void)b; fprintf(stderr, "NOTIFY: %s\n", r->message); return 0; }
static int stub_init(void *o, size_t s) { (void)o; (void)s; return 0; }
static int stub_pin(uint32_t *p) { *p = 2468135u; return 0; }   /* -> "02468135" */
void *dlopen(const char *f, int m) { (void)f; (void)m; return (void *)1; }
void *dlsym(void *h, const char *n) { (void)h; return !strcmp(n, "sceRemoteplayInitialize") ? (void *)stub_init : !strcmp(n, "sceRemoteplayGeneratePinCode") ? (void *)stub_pin : NULL; }
