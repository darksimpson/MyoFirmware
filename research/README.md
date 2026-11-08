# Research artifacts

These files were produced during static analysis of Myo Connect and Myo firmware. Address suffixes
are load addresses, not file offsets. Files here are analysis inputs and working state, not USB update images.

| File or group | Description |
|---|---|
| `binaries/kinetis_1.5.1970_at_0x10000.bin` | Decoded Kinetis application; file offset = address minus 0x10000 |
| `binaries/bootloader_1.5.1970_at_0x0.bin` | 22652-byte resident bootloader image extracted from application address 0x2B84A; load at 0 |
| `binaries/nrf51_app_1.5.1970_at_0x14000.bin` | 14148-byte embedded nRF51822 BLE relay; load at 0x14000 |
| `ida/*.i64` | Packed IDA databases with disassembly, analysis and research-assigned names |

The original S-record images contain gaps which decoded binaries fill with 0xFF. The embedded nRF
application does not include a complete nRF SoftDevice image. The extracted bootloader is not a dump
of an individual armband's bootloader or factory settings.

Packed databases were originally alongside their input binaries. If IDA asks for a missing input file,
select the corresponding file in `binaries`.
