# FiiO Air Link — HID Control Protocol

Reverse-engineered from the FiiO Control web app ([fiiocontrol.fiio.com](https://fiiocontrol.fiio.com/))
by intercepting `navigator.hid` traffic. **Unofficial / incomplete** — based on captured
behaviour, not vendor documentation. Verify before relying on any command, and never
attempt firmware writes with this.

Device under test: **"FIIO Air Link"**, firmware **1.4.0**.
**Status:** §4 (codecs) and §5 (brightness) are validated end-to-end on real hardware —
reads, writes, and FiiO's own UI all agree. See §8.

---

## 1. Transport

- **API:** WebHID (`navigator.hid`) over USB. The dongle exposes several vendor HID
  collections alongside its USB-audio interface.
- **Vendor ID:** `0x2972` (FiiO). **Product ID:** `0x0158`. (confirmed)
- **Control interface:** the collection at `mi_01 & col02`, which reports **447-byte** input
  and output reports. The device exposes three HID collections; the other two
  (`mi_00`, `mi_01 col01`) do **not** answer the control protocol. Don't pick by report
  size — open each candidate and keep the one that answers a GET (see `AirLinkDevice.Open`).
- **Output report ID:** `0x07` (host → device). **Input report ID:** `0x08` (device → host).
  These are the first byte of each HID report, before the frame.
- Reports are **fixed length, zero-padded** after the frame (pad to the collection's
  `MaxOutputReportLength`, here 447).
- Communication is **request → reply**; a host command produces a reply echoing the same
  `CMD`. **However** the control collection also emits async/unsolicited frames, so a reader
  must **drain pending input reports before each request** and match replies by `CMD`,
  or it will occasionally read a stale frame (this bit us — see `AirLinkDevice.DrainInput`).
- **No checksum** observed.

---

## 2. Frame format

Both directions share one frame, carried as the report payload (after the report ID):

```
 offset  bytes      meaning
 ------  ---------  ---------------------------------------------------
   0     FF 03      Magic / start-of-frame
   2     00 LL      Payload length, 16-bit big-endian (LL = # bytes after CMD)
   4     00 1D      Constant (unknown; same in every frame — protocol/channel id?)
   6     TYPE       Message type:  30 = request (host→device)
                                   31 = standard reply (device→host)
                                   01 = alternate reply class (seen carrying the firmware
                                        string in the capture; not 0x31). Treat anything
                                        != 0x30 as a reply.
   7     CMD        Command / property id (see table)
   8..   payload    LL bytes
   ...   00 ...     zero padding to the fixed report length
```

### Getter / setter convention

`CMD` ids come in pairs: **even = GET, odd = SET** (GET = even, its SET = even+1).

- **GET:** host sends the frame with `LL = 0` (no payload). Device responds with
  `DIR=31`, same `CMD`, and `LL` payload bytes carrying the value.
- **SET:** host sends `DIR=30`, the `CMD`, and the new value as payload. Device responds
  with an **ACK**: `DIR=31`, same `CMD`, `LL = 0` (empty payload).

---

## 3. Command table

| CMD  | Pair | Name                      | Dir | Payload                                   | Notes |
|------|------|---------------------------|-----|-------------------------------------------|-------|
| `00` | get  | Device name               | R   | ASCII, e.g. `FIIO Air Link`               | |
| `05` | get  | **Supported codecs**      | R   | capability list `08 07 06 04 05 03 01 00` | read-only; superset of `06` (see §4) |
| `06` | get  | **Enabled codec list**    | R   | ordered list of codec ids (see §4)        | GET of `07` |
| `07` | set  | **Set enabled codec list**| W   | full ordered list of enabled codec ids    | **v1** — see §4 |
| `0A` | get? | Refresh paired devices    | R   | `00`                                       | triggers device-list refresh |
| `0B` | set  | (paired-device action)    | W   | `00`                                       | seen during pairing flows |
| `0E` | get  | Paired-device table       | R   | count + per-device records (MAC, flags)   | pairing — out of v1 scope |
| `0F` | get  | Paired-device name by MAC | R   | req: `00`+6-byte MAC+`00`; resp: ASCII name | pairing — out of v1 scope |
| `40` | get  | **aptX Adaptive mode**    | R   | 1 byte (see §4.1)                          | GET of `41` |
| `41` | set  | **Set aptX Adaptive mode**| W   | 1 byte: `02`/`03`/`13`                      | Low Latency / High Quality / Lossless |
| `42` | get  | **LDAC quality mode**     | R   | 1 byte (see §4.1)                          | GET of `43` |
| `43` | set  | **Set LDAC quality mode** | W   | 1 byte: `00`/`01`/`02`                      | 990 / 660 / 330 kbps |
| `52` | get  | **Indicator brightness**  | R   | 1 byte level (see §5)                      | GET of `53` |
| `53` | set  | **Set indicator brightness** | W | 1 byte level `00`–`07`                     | **v1** — see §5 |

These are the per-codec **quality/latency sub-modes** (the LDAC bitrate selector and the
aptX Adaptive Low-Latency/High-Quality/Lossless selector) — see §4.1. The web app writes
*both* `41` and `43` (and re-sends the `07` list) on every "save"; only the one being
changed actually differs. A mode change does **not** require re-sending the `07` list.

---

## 4. Codec list (CMD `06` / `07`)

The payload is the **full, ordered list of currently-enabled codec ids**. To change
which codecs are enabled, the app rebuilds the whole list and sends it via `07` — there
is no per-codec toggle opcode.

### Codec ids

| Id   | Codec                | User-toggleable? | Evidence |
|------|----------------------|------------------|----------|
| `08` | LDAC                 | yes              | removed from list when LDAC toggled off |
| `07` | aptX Adaptive        | yes              | removed when aptX Adaptive toggled off |
| `06` | aptX HD              | yes              | removed when aptX HD toggled off |
| `05` | aptX Low Latency     | yes              | removed when aptX LL toggled off |
| `04` | aptX Lossless (likely)| no (capability only) | in supported list (`05`) but **not** in enabled list (`06`); never toggled |
| `03` | aptX                 | yes              | removed when aptX toggled off |
| `01` | baseline (`01`)      | no (always present) | one of `00`/`01` is SBC; the dongle has **no AAC** |
| `00` | baseline (`00`)      | no (always present) | one of `00`/`01` is SBC |

> **Supported (`05`) vs enabled (`06`):**
> supported = `08 07 06 04 05 03 01 00` (8 ids), enabled-by-default = `08 07 06 03 05 01 00`
> (7 ids). The difference is `04`, which is supported but not in the enabled set and is not a
> UI toggle — consistent with aptX **Lossless** being negotiated as part of aptX Adaptive
> rather than enabled separately.
> The web UI exposes exactly **5 toggles** (LDAC, aptX Adaptive, aptX HD, aptX, aptX LL);
> SBC is not user-toggleable.

### Canonical order

Full enabled list (everything on):

```
08 07 06 03 05 01 00
```

i.e. `LDAC, aptX Adaptive, aptX HD, aptX, aptX LL, (01), (00=SBC)`.

**Rule for the app:** keep this canonical order as a master list. To toggle a codec,
filter the master list to the enabled set (preserving order) and send via `07`.
Baseline ids `01`/`00` are always included. Read the current set at startup with `06`.

### Examples

```
GET codecs:           FF 03 00 00 00 1D 30 06
  response (all on):  FF 03 00 07 00 1D 31 06  08 07 06 03 05 01 00

SET LDAC off:         FF 03 00 06 00 1D 30 07  07 06 03 05 01 00      (08 removed)
  ack:                FF 03 00 00 00 1D 31 07

SET LDAC on again:    FF 03 00 07 00 1D 30 07  08 07 06 03 05 01 00
  ack:                FF 03 00 00 00 1D 31 07

SET aptX off:         FF 03 00 06 00 1D 30 07  08 07 06 05 01 00      (03 removed)
SET aptX HD off:      FF 03 00 06 00 1D 30 07  08 07 03 05 01 00      (06 removed)
SET aptX LL off:      FF 03 00 06 00 1D 30 07  08 07 06 03 01 00      (05 removed)
SET aptX Adaptive off:FF 03 00 06 00 1D 30 07  08 06 03 05 01 00      (07 removed)
```

### 4.1 Codec sub-modes

Two codecs have a quality/latency selector, each a single-byte enum with its own GET/SET pair.
These are independent of the enable list (`06`/`07`).

**LDAC quality** — GET `42` / SET `43`:

| Value  | Meaning |
|--------|---------|
| `0x00` | 990 kbps — Optimized for Audio Quality |
| `0x01` | 660 kbps — Balanced |
| `0x02` | 330 kbps — Optimized for Connection Quality |

**aptX Adaptive mode** — GET `40` / SET `41`:

| Value  | Meaning |
|--------|---------|
| `0x02` | Low Latency |
| `0x03` | High Quality |
| `0x13` | aptX Lossless |

`0x13 = 0x10 | 0x03`: aptX Lossless is the High-Quality mode with a "lossless" bit set.
This is why capability codec `04` (aptX Lossless, §4) is never present in the `07` enable
list — lossless is controlled here, not as a list entry.

Examples:
```
SET LDAC = 660 Balanced:    FF 03 00 01 00 1D 30 43  01     -> ack
SET aptX Adaptive Lossless: FF 03 00 01 00 1D 30 41  13     -> ack
GET LDAC quality:           FF 03 00 00 00 1D 30 42  -> reply 00 (990)
```

---

## 5. Indicator brightness (CMD `52` / `53`)

Single byte, `0x00`–`0x07` (8 levels). `0x00` = off/dimmest, `0x07` = brightest
(power-on default observed = `0x07`).

```
GET brightness:       FF 03 00 00 00 1D 30 52
  response (max):     FF 03 00 01 00 1D 31 52  07

SET brightness = 3:   FF 03 00 01 00 1D 30 53  03
  ack:                FF 03 00 00 00 1D 31 53
```

---

## 6. Pairing & device management

### Paired-device list

1. **Refresh** — GET `0A` (no payload), reply `00` = ok. Tells the dongle to rebuild its list.
2. **Table** — GET `0E`. Reply = `count(1 byte)` followed by `count` × **12-byte records**:

   ```
   offset  bytes        meaning
   ------  -----------  -----------------------------------------
     0     00           prefix (matches the MAC-frame prefix)
     1..6  MAC[6]       Bluetooth address, as displayed
     7     flag         0x80 = connected/active, 0x00 = paired/idle
     8     06           constant (device type? — unconfirmed)
     9..11 00 00 00     reserved
   ```

3. **Name** — GET `0F` with request payload = the **MAC frame** `00 + MAC + 00` (8 bytes);
   reply = ASCII name (e.g. `My Headphones`, `Earbuds`).

### MAC frame

All per-device commands take the address as `00 + MAC(6) + 00` (8 bytes).

| CMD  | Action      | Payload    | Notes |
|------|-------------|------------|-------|
| `10` | Connect     | MAC frame  | **ACK comes back as cmd `0x90`** (= `0x10 \| 0x80`), payload 1 byte (e.g. `06`) — not cmd `0x10`. Reader must accept `cmd \| 0x80` as the ack. |
| `11` | Disconnect  | MAC frame  | acks as cmd `11` |
| `12` | Pair / bond | MAC frame  | acks as cmd `12`; send only after the target's `0x81` discovery |
| `13` | Forget      | MAC frame  | unpair / remove from list |

The Air Link supports **multipoint** — up to **two** sinks connected at once (per FiiO docs).
The record flag `0x80` marks each *connected* device, so both can be `0x80`. In dual-device
mode the link **falls back to SBC** (bandwidth can't carry two high-bitrate streams).
(In our single-device tests only one was ever `0x80`, which is why we first assumed a single
link.)

### Pairing mode — CMD `0B` and the scan engine

Pairing involves **two independent things**: the `0x0B` mode flag, and a **scan engine**
driven by `type=0x00` frames. `0x0B` alone does **not** start or stop scanning.

| Frame | Meaning |
|-------|---------|
| `type=00 cmd=07 payload=18` | **Start** scan engine |
| `type=00 cmd=08 payload=18` | **Stop** scan engine |
| `0B 00` | Mode flag: close |
| `0B 01` | Mode flag: auto pairing |
| `0B 02` | Mode flag: manual pairing |

Verified sequences (each `type=00` frame is fire-and-forget; its reply is a `type=0x01`
frame):

```
Enter manual pairing:  0A ;  (type00) 07 18 ;  0B 00 ;  0B 02
Close pairing:         0A ;  (type00) 08 18 ;  0B 00
```

**Sending `0B 00` without `08 18` does not stop scanning** — the LED keeps flashing. This bit
us: our first close sent only `0B 00` and the dongle stayed in pairing.

### Auto (`0B 01`) vs Manual (`0B 02`)

Both flags start the same scan engine and emit `0x81` results; the difference is intent
(per FiiO docs + observed behavior):

- **Auto pairing** — the dongle *drives*: it auto-reconnects previously-paired sinks and, if
  that fails, auto-connects whatever discoverable sink it finds (tends to pick the
  strongest RSSI). The app sends no `12`/`10`.
- **Manual pairing** — the *app/user drives*: the dongle surfaces discovered devices via
  `0x81` and the app picks one with `12` (pair) + `10` (connect). This is the mode to "add a
  specific device".

> The catch: manual mode does **not** suppress the dongle's own auto-connect urge. Its scan
> engine still tries to grab the strongest discoverable sink, which races your explicit
> selection — so pairing a specific weaker device is unreliable until louder sinks are off.
> (The physical 10-second button press is a third thing: "forced pairing" that *clears all*
> bonds first — the app's `0B 02` does **not** clear bonds.)

### ⚠ Firmware behavior (not a protocol issue)

- The dongle **auto-enters pairing/search mode on its own** after sitting idle with no sink
  connected (LED flashes red/blue). This competes with explicit pairing.
- In that state it tends to **auto-connect the strongest-RSSI advertiser**, so pairing a
  specific weaker device can fail until louder advertisers are powered off.
- An **in-progress connection attempt cannot be aborted** by the stop/close sequence; it
  must time out or the dongle be re-plugged.
- Pairing a new device requires the target's `0x81` discovery first, then `12`+`10`. Even
  with identical bytes to a known-good capture, the bond only persists if the RF link
  actually completes — a **stale bond on the peer** (from prior half-attempts) can block it.

### Async events (device → host)

During pairing the dongle pushes **unsolicited event frames** with type byte `0x30` (same
value as a request) and command `>= 0x80`. Our reader distinguishes them from replies by
`cmd >= 0x80`. The client opens a persistent background reader to receive these.

| CMD  | Event | Payload |
|------|-------|---------|
| `81` | Scan result (device found) | scan header + device record (below) |
| `83` | Paired-list changed | same layout as the `0E` table (count + 12-byte records) |
| `84`/`85`/`86` | Scan/connection progress | 9-byte `00 + addr + 00 + counter` (ignored) |

**Scan-result (`0x81`) payload:**

```
offset  bytes        meaning
------  -----------  ---------------------------------------------
 0..8   <9 bytes>    scan header (constant within a session; ignored)
 9..14  MAC[6]       discovered device address
 15     00
 16     RSSI         signal level (1 byte)
 17     00
 18     nameLen      length of name incl trailing NUL (0 = not resolved yet)
 19     00
 20..   name         ASCII, NUL-terminated
```

A device is reported repeatedly as info arrives (first with `nameLen = 0`, later with the
name); dedupe by MAC. To pair a discovered device, while manual pairing is active: send
`12` (pair) + `10` (connect) with its MAC **immediately after** its `0x81` event.

> Validated on hardware: a manual-pairing scan reported two nearby sinks
> (e.g. `Living-Room TV` / `AA:BB:CC:00:00:01` and `Office Monitor` / `AA:BB:CC:00:00:02`)
> with RSSI and names via `0x81`.

---

## 7. Open questions / TODO

- [x] ~~USB Product ID~~ → `0x0158`. ~~Report length~~ → 447 (control collection `mi_01 col02`).
- [x] ~~Codec ids `00`/`01`~~ → baseline, not user-toggleable; one is SBC; no AAC on this dongle.
      `04` = capability-only (likely aptX Lossless).
- [ ] **Firmware version:** the capture shows `1.4.0` returned with message-type `0x01`
      (frame `ff 03 00 05 00 1d 01 05 31 2e 34 2e 30`), but a live GET `05` returns the
      *supported-codec* list with type `0x31`. So firmware is delivered by a separate
      `type=0x01` exchange (or an init-time push), not by GET `05`. Decode if we want to
      show firmware in the app — **not needed for v1**.
- [x] ~~Decode `40/41` and `42/43`~~ → aptX Adaptive mode and LDAC quality (see §4.1).
- [x] ~~Pairing commands `0A/0B/0E/0F`~~ → decoded (see §6); `10`–`13` are connect/disconnect/
      pair/forget; async `0x81`/`0x83` events decoded (scan results / paired-list push).
- [ ] Confirm device-record byte `8` (`06`) meaning and whether multipoint is supported.
- [x] ~~Decode the `type=0x00` `07 18` frame~~ → scan-engine **start**; `08 18` is **stop** (§6).
- [ ] Confirm whether `00 1D` is ever non-constant (sequence number? handler id?).

---

## 8. Hardware validation

Validated against the real dongle via `AirLink.Cli` (FiiO firmware 1.4.0):

| Action | Command | Result |
|--------|---------|--------|
| Read name | GET `00` | `FIIO Air Link` ✓ |
| Read brightness | GET `52` | `7` ✓ |
| Read enabled codecs | GET `06` | `08 07 06 03 05 01 00` ✓ |
| Read supported codecs | GET `05` | `08 07 06 04 05 03 01 00` ✓ |
| Set brightness `7→2` | SET `53 02` | LED visibly dimmed; confirmed in FiiO UI ✓ |
| Disable LDAC | SET `07` (drop `08`) | LDAC removed; confirmed unchecked in FiiO UI ✓ |
| Set LDAC quality 990→660 | SET `43 01` | confirmed "Balanced 660" in FiiO UI ✓ |
| Set aptX Adaptive → Lossless | SET `41 13` | confirmed "aptX Lossless" in FiiO UI ✓ |
| Re-enable LDAC / restore all | SET `07`, `53 07`, `43 00`, `41 02` | back to original state ✓ |
| List paired devices | `0A`+`0E`+`0F` | `○ Earbuds`, `● My Headphones` — matches FiiO UI ✓ |
| Scan for devices | `0B 02` + listen `0x81` | found two nearby sinks with names & RSSI ✓ |

---

## 9. Capture provenance

Logs in this directory, captured via the `navigator.hid` interceptor (each event appears
twice — two JS listener contexts; dedupe on identical frames):

| File | Contents |
|------|----------|
| `initial_connection.log` | Startup handshake: GET name/fw/codecs/brightness/modes + paired devices |
| `ldac_toggle.log`, `aptx_toggle.log`, `aptx_hd_toggle.log`, `aptx_ll_toggle.log`, `aptx_adaptive_toggle.log` | Per-codec enable/disable via CMD `07` |
| `indicator_brightness.log` | Brightness sweep `06`→`00` via CMD `53` |
| `ldac_codec_priority.log`, `aptx_adaptive_codec_priority.log` | Codec-priority changes (CMD `41`/`43`) |
| `refresh_device_list.log`, `*_pair*.log`, `disconnect_active.log`, `delete_paired_device.log` | Pairing / device management (deferred) |
