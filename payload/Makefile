# RetroSync pairing helper - build with the open PS5 payload SDK (https://github.com/ps5-payload-dev/sdk)
#   make PS5_PAYLOAD_SDK=/opt/ps5-payload-sdk
PS5_PAYLOAD_SDK ?= /opt/ps5-payload-sdk
PS5_HOST ?= ps5
PS5_PORT ?= 9021
include $(PS5_PAYLOAD_SDK)/toolchain/prospero.mk

ELF := retrosync-pair.elf
REST := retrosync-rest.elf
CFLAGS := -Wall -Werror -O2
LIBS := -lSceUserService -lSceRegMgr

all: $(ELF) $(REST)

$(REST): rest.c
	$(CC) $(CFLAGS) -o $@ $^ -lSceSystemService

$(ELF): pair.c
	$(CC) $(CFLAGS) -o $@ $^ $(LIBS)

clean:
	rm -f $(ELF) $(REST)

test: $(ELF)
	$(PS5_DEPLOY) -h $(PS5_HOST) -p $(PS5_PORT) $^
