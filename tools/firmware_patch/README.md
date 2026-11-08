# Myo firmware patcher

Builds a patched Myo firmware image from the stock one: fixes the built-in classifier's history
read-back bug, stops the armband running its classifier until a BLE central asks for it, renumbers
the version, and recomputes the checksums the bootloader verifies before it starts an application.
It produces the 1.5.1972 build at the output path supplied on the command line.

```
dotnet run --project tools/firmware_patch -- original/myo-firmware-1.5.1970.hex rebuilt.hex
```

Run this from the repository root (`Firmware`). Input and output paths are required command-line
arguments. The input cannot also be the output. The patcher uses no machine-specific paths.

## What it changes

| Address | Stock | Patched | Why |
|---|---|---|---|
| 0x1848A | `34 1B` | `34 19` | `SUBS R4, R6, R4` becomes `ADDS R4, R6, R4` |
| 0x15CBE | `FD F7 1F FA` | `00 BF 00 BF` | `fw_main` no longer calls the classifier enable at boot |
| 0x1338A | `FF F7 B9 FE` | `FF F7 57 FF` | the BLE disconnect handler disables the classifier instead of re-enabling it |
| 0x1018C | `B2 07` | `B4 07` | version header: patch 1970 becomes 1972, which is what the version characteristic reports |
| 0x10246 | `"1970"` | `"1972"` | the same number in the INFO.TXT template the USB drive serves |
| 0x7539C | | recomputed | Fletcher32 of 0x10410 + 0x64F80, covering the instructions |
| 0x753AC | | recomputed | Fletcher32 of 0x10000 + 0x190, covering the version header |

Eight S-record lines differ from the stock file.

## The classifier running unasked

The stock firmware calls the classifier enable (0x13100) from `fw_main` at boot, and the BLE
disconnect handler (0x13388) calls it again every time the link drops. So an armband that is merely
worn starts EMG, the feature vector and the IMU, watches for the sync gesture, syncs, and tracks
poses with no host involved. Connecting does not reset any of that: the connect handler only
suppresses the streams until `set_mode` arrives, which is why a freshly connected armband usually
reports itself already synced and starts reporting poses at once.

The two patched call sites leave the classifier off until `set_mode` turns it on, so the armband does
nothing until an application asks. The boot site becomes a pair of NOPs. The disconnect site becomes
a call to the classifier *disable* (0x1323C) rather than a NOP, because a host may have enabled the
classifier before the link dropped and the enabled flag would otherwise stay set. That call clears
the flag before `fw_on_arm_unsynced`, so the sync-state setter at 0x16464 sees the classifier
disabled and skips the unsync vibration; the disconnect stays silent.

Worth knowing: the per-frame classifier entry (0x1324C) postpones the 30 s inactivity sleep only
while a pose is being reported, so a patched armband behaves like an unworn stock one until it is
connected. Put it on and it stays asleep; move it and it wakes and advertises. Once connected,
`myo_connect` sets never-sleep and the question goes away.

## The bug being fixed

The built-in classifier keeps a 25-frame ring of feature frames and gathers five of them, stepping
back five frames at a time, as the network's input. The gather routine at 0x18466 wraps a negative
index with

```
if (index < 0) { index = capacity - index; }      // 0x1848A, SUBS R4, R6, R4
```

where it means

```
if (index < 0) { index = capacity + index; }
```

so on 20 of every 25 frames it reads the wrong slots, including slots that were never written. The
fix is one instruction of the same length, and nothing downstream depends on the flags it sets.

Fixing it is a small, real improvement: slightly faster pose onset and slightly better wave-in
recognition, confirmed on the armband. It is not dramatic, because the network was trained with the
bug possibly present during device-side tuning; its training conditions are unknown. MyoSharp's `BuiltInClassifier` reproduces the firmware bug by default and takes
`EmulateHistoryBug = false` to match a patched armband.

## Why the checksums have to be recomputed

The bootloader will not start an application whose own Fletcher32 records do not verify, and the BLE
update path checks the same records before it swaps banks. The record list is found through the
pointer at 0x101FC; each type 1 record gives an offset, a size and a Fletcher32. The patcher walks
that list exactly as `fw_ota_verify_image` (0x12DD4) and the bootloader do, and rewrites the stored
value for every record whose range it touched.

It refuses to write anything unless the stock image passes those checks first, the bytes at each
patch address are the expected ones, the patched image passes again, every changed byte is covered by
an S-record in the input, and re-reading the file it wrote gives back the same image.

## What must not be patched

The application carries a copy of the resident bootloader at 0x2B84A, 22652 bytes, and compares it
against flash 0x0 at every boot, rewriting flash if they differ. **Never change anything in
0x2B84A..0x310C6**: a wrong bootloader can only be repaired with an SWD debugger. MyoSharp's
`MyoFirmwareImage` refuses any image whose embedded bootloader is not a known one, which is also why
it only accepts 1.4.1670, 1.5.1931 and 1.5.1970 and patched builds of those.

Also leave 0x1018E alone: it is the hardware revision, and the bootloader refuses an image that does
not say 2 (Rev D).

## Installing the result

Either way works, and both wipe custom classifier slots 1 to 4 while slot 0, the device name and the
factory calibration survive.

* **USB**: plug the armband in and copy the `.hex` onto the drive that appears, as Myo Connect does.
  Wait for `SUCCESS.TXT`.
* **BLE**: the OTA tab of the MyoSharp demo, which validates the image first and installs it into the
  spare flash bank. This was used to put stock 1.5.1970 back over the patched build, so it works in
  both directions.

Myo Connect will not offer to "update" a 1971 armband back to 1970, because it only updates when its
bundled version is newer.

## Recovery

Keeping the bootloader intact preserves the researched USB recovery path, but does not guarantee
recovery from every hardware or flash failure. The
bootloader always runs first, stays in USB drive mode whenever USB is connected, and refuses to start
an application whose checksums do not verify, so a bad image means the armband comes up as a USB
drive ready for another file. The application also enables the watchdog early, so a hung build is
reset within seconds.
