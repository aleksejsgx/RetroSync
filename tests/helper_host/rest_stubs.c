#include <stdio.h>
#include <string.h>
#include <stdlib.h>
typedef struct { char pad[45]; char message[3075]; } notify_request_t;
int sceKernelSendNotificationRequest(int d, notify_request_t *r, size_t s, int b) { (void)d;(void)s;(void)b; fprintf(stderr,"NOTIFY: %s\n", r->message); return 0; }
int sceSystemServiceGetStatus(void *p) { (void)p; return 0; }
static int stub_standby(void) { fprintf(stderr,"STUB: EnterStandby\n"); return getenv("STUB_FAIL") ? 0x80100001 : 0; }
void *dlopen(const char *f, int m) { (void)f;(void)m; return (void*)1; }
void *dlsym(void *h, const char *n) { (void)h; return strcmp(n,"sceSystemStateMgrEnterStandby")==0 ? (void*)stub_standby : NULL; }
