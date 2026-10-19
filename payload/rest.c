/* RetroSync rest-mode helper (MIT licence, see LICENSE in the repository).
 *
 * One-shot homebrew payload for the user's own PS5, sent by RetroSync to the ELF loader on 9021
 * ("Metti a riposo"). It shows a notification, waits 3 s (so the loader connection and the toast
 * finish), asks the system to enter rest mode and exits. Nothing listens, nothing is written.
 * Only rest mode: this helper has no reboot or power-off code.
 *
 * Written from scratch for RetroSync. The system function name is a public fact (exported by
 * libSceSystemService); it is looked up at run time so a firmware without it only gets a notice. */
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <dlfcn.h>

#ifndef DELAY_SECONDS
#define DELAY_SECONDS 3
#endif

typedef struct { char pad[45]; char message[3075]; } notify_request_t;
int sceKernelSendNotificationRequest(int dev, notify_request_t *req, size_t size, int blocking);
int sceSystemServiceGetStatus(void *status);   /* referenced only so libSceSystemService is linked */

static void notify(const char *msg)
{
    notify_request_t req;
    memset(&req, 0, sizeof(req));
    snprintf(req.message, sizeof(req.message), "%s", msg);
    sceKernelSendNotificationRequest(0, &req, sizeof(req), 0);
    printf("[retrosync-rest] %s\n", msg);
}

int main(void)
{
    volatile void *keep = (void *)sceSystemServiceGetStatus; (void)keep;
    void *lib = dlopen("libSceSystemService.sprx", RTLD_NOW | RTLD_GLOBAL);
    int (*enter_standby)(void) = (int (*)(void))dlsym(lib ? lib : RTLD_DEFAULT, "sceSystemStateMgrEnterStandby");
    if (!enter_standby) enter_standby = (int (*)(void))dlsym(RTLD_DEFAULT, "sceSystemStateMgrEnterStandby");
    if (!enter_standby) { notify("RetroSync: rest mode unavailable on this firmware"); return 1; }
    notify("RetroSync: PS5 going to rest mode");
    sleep(DELAY_SECONDS);
    int rc = enter_standby();
    if (rc != 0) {
        char m[96];
        snprintf(m, sizeof(m), "RetroSync: rest mode refused (0x%08X)", (unsigned)rc);
        notify(m);
        return 1;
    }
    return 0;
}
