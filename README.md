# Myo firmware collection and patches

Firmware images, research artifacts and a reproducible .NET 8 patcher for the Thalmic Myo
armband (Rev D, hardware revision 2).

## Contents

| Directory | Contents |
|---|---|
| [original](original/) | Eight unmodified firmware releases, from 1.1.5 through 1.5.1970 |
| [patched](patched/) | Prepared 1.5.1971 and 1.5.1972 images |
| [research](research/README.md) | Decoded binaries, retained IDA work |
| [tools/firmware_patch](tools/firmware_patch/README.md) | C# patcher and technical patch notes |

Despite their `.hex` extension, the firmware images contain Motorola S-records, not Intel HEX.
Original images are preserved byte for byte. `SHA256SUMS.txt` identifies the distributed images.

## Patched versions

| Version | Changes relative to stock 1.5.1970 |
|---|---|
| 1.5.1971-histfix | Fixes the classifier history ring index and changes the BLE/USB version |
| 1.5.1972-histfix-nosync | Includes the history fix; leaves classification disabled at boot and disables it after BLE disconnect until a host explicitly enables it |

The history correction replaces `capacity - negativeIndex` with `capacity + negativeIndex`.
The 1972 change prevents autonomous sync and classification when no host requested them.
The current patcher generates 1972 from stock 1970; 1971 is retained as an earlier prepared build.

## Build

From this directory with the .NET 8 SDK installed:

```sh
dotnet build tools/firmware_patch -c Release
dotnet run --project tools/firmware_patch -c Release -- original/myo-firmware-1.5.1970.hex artifacts.hex
```

The patcher requires explicit input and output paths and refuses to overwrite the input. It checks
the original application checksums and expected instruction bytes, applies the patches, recomputes
the application and S-record checksums, and reads the output back to verify the result.
It only supports the stock 1.5.1970 input.

## GitHub releases

Ordinary pushes and pull requests build the patcher and upload a workflow artifact.
Pushing a tag starting with `v` also creates a GitHub Release after the build passes:

```sh
git tag -a v1.5.1972 -m "Firmware 1.5.1972"
git push origin v1.5.1972
```

The release includes both prepared patched firmware images, `SHA256SUMS.txt`, and
`MyoFirmwarePatch.zip`. The patcher ZIP requires the .NET 8 runtime; run it with
`dotnet MyoFirmwarePatch.dll <input.hex> <output.hex>` after extraction.
The checksum list covers the repository firmware images, not the patcher ZIP.

## Installing and recovering

For USB installation, connect the armband, copy the chosen S-record image onto its USB drive,
and wait for `SUCCESS.TXT` before disconnecting. Retain the stock 1970 image for restoration.
Compatible MyoSharp versions also provide BLE OTA installation.
Firmware updates can erase custom classifier slots 1 through 4; save their original model files.

The embedded bootloader and hardware revision must remain intact. The patcher leaves both unchanged.
The resident bootloader provides USB recovery for an invalid application, but this is not a guarantee
against every hardware fault, power interruption or corrupted bootloader. See the patcher notes for
the researched recovery behavior. Do not flash the decoded research `.bin` files as update images.
