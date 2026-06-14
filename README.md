# AirLink Tray

An offline Windows tray app (and CLI) to control the **FiiO Air Link** USB Bluetooth
transmitter — codecs, codec sub-modes, indicator brightness, and Bluetooth pairing —
without the official FiiO Control app or its web interface.

> **Unofficial.** The control protocol was reverse-engineered by observing the FiiO
> Control web app's WebHID traffic. It is not endorsed by FiiO. Use at your own risk.
> Firmware updates are intentionally **not** implemented (too risky over an
> unofficial protocol). See [`FiiO-AirLink-Protocol.md`](FiiO-AirLink-Protocol.md) for the
> full protocol writeup.

## Features

- **Codecs** — enable/disable LDAC, aptX Adaptive, aptX HD, aptX, aptX LL.
- **Codec sub-modes** — LDAC bitrate (990/660/330) and aptX Adaptive mode
  (Low Latency / High Quality / aptX Lossless).
- **Indicator brightness** — 8 levels.
- **Pairing** — list paired devices, connect / disconnect / forget, and a live
  "Connect a device" window that shows nearby devices as they're discovered.
- Tray icon with live connect/disconnect status; optional start-with-Windows.

## Requirements

- Windows 10/11
- A FiiO Air Link (USB VID `0x2972`, PID `0x0158`)
- [.NET 9 SDK](https://dotnet.microsoft.com/download) to build

## Projects

| Project | What it is |
|---------|-----------|
| `AirLink.Core` | Device layer — HID transport, frame codec, protocol commands, events |
| `AirLink.Cli`  | Command-line tool (great for scripting and protocol poking) |
| `AirLink.Tray` | The WinForms system-tray app (the product) |

## Build & run

```powershell
dotnet build -c Release

# Tray app
.\AirLink.Tray\bin\Release\net9.0-windows\AirLinkTray.exe

# CLI
dotnet run -c Release --project AirLink.Cli -- info
```

### CLI commands

```
airlink list                       enumerate matching HID interfaces
airlink info                       name, brightness, modes, enabled/supported codecs
airlink codec <name> on|off        ldac | aptx-adaptive | aptx-hd | aptx | aptx-ll
airlink brightness [0..7]          get/set indicator brightness
airlink mode ldac [0..2]           LDAC quality (0=990 1=660 2=330)
airlink mode adaptive [ll|hq|lossless]
airlink devices                    list paired devices (● connected)
airlink connect|disconnect|forget <mac>
airlink pair auto|manual|close     set Bluetooth pairing mode
airlink scan [secs]                discover nearby devices (manual pairing)
airlink raw <cmd> [bytes...]       GET/SET a raw protocol frame
```

## Known dongle quirks

These are **firmware behaviors** of the Air Link itself, not bugs in this app (the official
app is subject to them too) — see the protocol doc for detail:

- It **auto-enters pairing/search** after sitting idle with no sink connected.
- In that state it tends to **auto-connect the strongest nearby advertiser**, which can
  hijack an attempt to pair a specific (weaker) device — power off louder sinks first.
- An in-progress connection attempt can't be aborted in software; unplug/replug to reset.
- Multipoint (two sinks) forces SBC, by design.

## Releases

Pushing a version tag builds a self-contained single-file `AirLinkTray.exe` (no .NET
install needed) and attaches it to a GitHub Release via Actions:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## License

[MIT](LICENSE)
