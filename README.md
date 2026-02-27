# RT-5D Programming Tool

A cross-platform command-line programming utility for the **RT-5D / JJCC-888DMR** DMR transceiver. The tool communicates with the radio over USB serial, reads and writes the complete codeplug, and stores the configuration as human-readable JSON that can be version-controlled, edited in any text editor, and written back to the radio.

---

## Features

- **Full codeplug read/write** — all 1,024 channels, 4,000 contacts, 32 Rx groups, DTMF, encryption keys, VFO banks, optional functions, and basic info
- **Human-readable JSON** — every field stored by name: frequencies as MHz strings, tones as `"CTCSS 88.5"` or `"D023N"`, enums as symbolic names (`"High"`, `"DmrTierII"`)
- **Automatic verify** — after every write the tool re-reads the radio and confirms the session completed cleanly, with a built-in restart delay
- **Retry and resilience** — 1-second timeout, up to 3 retries per packet, CRC-16/CCITT validation on every frame
- **Cross-platform** — runs on Windows, Linux, and macOS wherever .NET 8 is available
- **Debug mode** — full protocol frame trace available on demand via `--debug`

---

## Requirements

| Requirement | Details |
|---|---|
| Runtime | [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Cable | RT-5D USB programming cable (CP210x or CH340 chipset) |
| Platforms | Windows 10/11, Ubuntu 20.04+, macOS 12+ |
| Permissions | Serial port access (see [Platform Notes](#platform-notes)) |

---

## Building

```bash
git clone https://github.com/your-org/rt5d-programmer.git
cd rt5d-programmer
dotnet build -c Release
```

Publish a self-contained single-file executable:

```bash
# Windows
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Linux
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# macOS
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true
```

---

## Usage

```
RT5D <command> [args] [--debug]

Commands:
  test               Run built-in unit tests and exit.
  ports              List available serial ports.
  info <port>        Connect, display firmware version, and disconnect.
  read <port> [out]  Read full configuration from radio.
                     Saves to out (default: rt5d_config.json).
  write <port> <in>  Write configuration file to radio, then verify.
              [--basic-info]  Also write model name/ID (factory use only).

Options:
  --debug            Show full protocol frame trace (TX/RX log).
```

### Quick start

```bash
# Check which ports are available
RT5D ports

# Identify the radio and display its firmware version
RT5D info COM3

# Back up the radio to a file
RT5D read COM3 my_radio.json

# Edit my_radio.json in any text editor, then write it back
RT5D write COM3 my_radio.json
```

---

## JSON Format

The configuration file is a single JSON object. Each section maps directly to a block in the radio's codeplug. Empty slots are omitted; populated entries carry an explicit 1-based `slot` number.

```jsonc
{
  "radio": {
    "modelName": "DMR",
    "modelId": 1
  },
  "channels": [
    {
      "slot": 1,
      "name": "Repeater 1",
      "type": "DmrTierII",
      "rxFreqMHz": "438.500000",
      "txFreqMHz": "430.500000",
      "rxSubAudio": "OFF",
      "txSubAudio": "CTCSS 88.5",
      "power": "High",
      "scanAdd": true,
      "colorCode": 1,
      "timeSlot": 0,
      "rxGroupIndex": 1,
      "contactIndex": 1
    }
  ],
  "contacts": [
    { "slot": 1, "name": "Calling", "callType": "Group", "callId": 9 }
  ],
  "rxGroups": [ ... ],
  "encryptionKeys": [ ... ],
  "dtmf": { ... },
  "vfo": { "bankA": { ... }, "bankB": { ... } },
  "settings": { ... }
}
```

See [`Samples/sample.json`](Samples/sample.json) for a complete annotated example.

### Field reference

**Frequencies** — 6-decimal MHz string: `"438.500000"`

**Sub-audio** — one of:
- `"OFF"`
- `"CTCSS 88.5"` (frequency in Hz)
- `"D023N"` / `"D023I"` (DCS normal / inverted)

**Channel type** — `"AnalogFm"`, `"DmrTierI"` (simplex), `"DmrTierII"` (repeater)

**TX power** — `"Low"`, `"Medium"`, `"High"`

**Encryption** — `"None"`, `"Basic"`, `"Enhanced"`, `"Aes"`

---

## Platform Notes

### Windows
The programming cable appears as a `COMx` port in Device Manager. No extra steps required.

### Linux
Add your user to the `dialout` group to access serial ports without `sudo`:

```bash
sudo usermod -aG dialout $USER
# Log out and back in for the change to take effect
```

The port typically appears as `/dev/ttyUSB0`.

### macOS
The port typically appears as `/dev/cu.usbserial-XXXX`. The CP210x driver ships with macOS; CH340-based cables may require a driver from the manufacturer.

---

## Architecture

```
Program.cs               CLI, argument parsing, JSON persistence
├── ProtocolV2.cs        Packet framing: SOF/CMD/SEQ/LEN/PAYLOAD/CRC-16
├── SerialLink.cs        Raw async byte transport at 115200 8N1
├── SessionV2.cs         Full 12-step programming sequence (read & write)
├── DataBlocksV2.cs      Block encoders/decoders for all codeplug sections
├── SubAudio.cs          CTCSS and DCS encoding
├── ChannelsPackerV2.cs  1,024-channel flat ↔ packet layout
├── ContactsPackerV2.cs  4,000-contact flat ↔ packet layout
├── RxGroupsPackerV2.cs  32-group flat ↔ packet layout
├── CodeplugJson.cs      Human-readable JSON serialization/deserialization
└── Crc16Ccitt.cs        CRC-16/CCITT (polynomial 0x1021)
```

The protocol layer implements the exact binary framing from `RT5D_Protocol_Analysis.PDF`. The data layer encodes and decodes all fields exactly as specified in `RT5D_Data_Structures.PDF`. All constants reference the relevant PDF section — no magic numbers in source.

---

## Running the Tests

```bash
RT5D test
```

The built-in suite covers all binary encoders and decoders, CRC calculation, sub-audio round-trips (CTCSS and DCS), frequency conversion, FHSS packing, and GB2312 string handling.

---

## Safety

- **Always back up before writing.** Run `RT5D read` first and keep the JSON file.
- Do not disconnect the cable during a read or write operation.
- The tool performs an automatic verify read after every write with a 10-second delay to allow the radio to restart cleanly. If verify fails, investigate before power-cycling.
- `--basic-info` overwrites the model name and ID in the radio. Omit it for all normal programming operations.

---

## Contributing

Pull requests are welcome. Please ensure `RT5D test` passes before submitting. All binary encoding changes must be accompanied by updated or new test cases in `Tests.cs`.

---

## License

MIT License

Copyright (c) 2026 Kelvin J. HIll

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

