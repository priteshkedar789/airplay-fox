# AirplayFox: stream Windows audio to a HomePod (AirPlay 2 sender for Windows)

**AirplayFox is a free, open-source Windows tray app that plays your PC's system audio on a HomePod over AirPlay 2.** Whatever the PC plays (YouTube, Spotify, games, calls) comes out of the speaker, with a latency you set between 0 and 4 seconds.

No iTunes, no Apple software, no virtual audio cable and no driver to install. Tested on one HomePod mini from Windows 10; other AirPlay 2 speakers are untested.

**[Download the latest release for Windows](https://github.com/priteshkedar789/airplay-fox/releases/latest)** (zip, unsigned: Windows SmartScreen will warn, see the install steps below). Free and MIT-licensed.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform: Windows 10](https://img.shields.io/badge/platform-Windows%2010%20(tested)-informational)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
[![Latest release](https://img.shields.io/github/v/release/priteshkedar789/airplay-fox)](https://github.com/priteshkedar789/airplay-fox/releases/latest)

![AirplayFox tray menu on Windows 10 while streaming to a HomePod named Bedroom: Speakers, Disconnect, Latency, Speaker volume, mute PC speakers, Open log, Exit](docs/tray-menu-streaming.png)

## Features: Windows to HomePod audio streaming

- **Tray-only app** — right-click the fox: pick a speaker, set latency, set speaker volume. Double-click to connect/disconnect.
- **Adjustable latency, 0.00 – 4.00 s** — presets (0, 0.10, 0.25, 0.50, 0.75, 1.00 s), a custom box with two-decimal precision, or **Auto**, which measures your Wi-Fi when you connect.
- **Clean audio path** — capture and sending run on separate threads joined by a buffer, packets go out on a precise clock, and clock drift between your PC and the speaker is corrected a single sample at a time. A late chunk from Windows never turns into a click.
- **Sane volume** — the speaker is set to a saved level (default 33 %) when a session starts and is never forced to 100 % afterwards.
- **Self-healing** — if the speaker stops answering (Wi-Fi blip, reboot) it reconnects automatically and re-measures.
- **Optional "mute this PC's speakers while streaming"** — so you don't hear the PC and the speaker half a second apart.
- Follows the Windows default output device and restarts capture if you change it.

Tested against a HomePod mini from a Windows 10 PC. Other AirPlay 2 speakers that accept HomeKit *transient* pairing may work but have not been tested.

## Download and install (Windows, no iTunes)

Tested on Windows 10. Windows 11 has not been tested.

1. Open the [latest release](https://github.com/priteshkedar789/airplay-fox/releases/latest) and download one of:
   - `AirplayFox-…-win-x64-selfcontained.zip` — **easiest**, nothing else to install (larger download).
   - `AirplayFox-…-win-x64.zip` — small download; needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Unzip anywhere and run `AirplayFox.exe`.
3. Windows SmartScreen will warn that the app is unsigned: **More info → Run anyway**.
4. If Windows Firewall asks, allow it on **Private networks** — the speaker sends timing and "please resend" packets back to your PC.

Check the download against `SHA256SUMS.txt` from the release if you like:

```powershell
Get-FileHash .\AirplayFox-*.zip -Algorithm SHA256
```

## How to AirPlay from Windows to a HomePod

1. Start `AirplayFox.exe`. A fox appears in the system tray (click the `^` near the clock if you don't see it; drag it onto the taskbar to keep it visible).
2. Right-click the fox → **Speakers** → choose your HomePod. The fox turns colour when audio is streaming (grey = not connected).
3. Play anything on your PC.

| Not connected | Streaming | Speaker volume menu |
|:---:|:---:|:---:|
| <img src="docs/tray-icon-idle.png" alt="AirplayFox grey fox icon in the Windows 10 system tray with the tooltip 'AirplayFox - not connected'" width="220"> | <img src="docs/tray-icon-connected.png" alt="AirplayFox orange fox icon in the Windows 10 system tray with the tooltip 'AirplayFox - Bedroom, 180 ms'" width="220"> | <img src="docs/tray-menu-volume.png" alt="AirplayFox Speaker volume submenu on Windows 10 listing 15, 25, 33, 50, 70 and 100 percent" width="300"> |

| Menu item | What it does |
|---|---|
| **Speakers** | Pick the speaker; click the ticked one to disconnect. |
| **Latency** | **Auto**, a preset, or **Custom…** (type seconds, e.g. `0.35`). Changing it reconnects so it applies immediately. |
| **Speaker volume** | Sets the speaker's own volume (15–100 %). Keep Windows and YouTube at normal levels and use this to set loudness. |
| **Mute this PC's speakers while streaming** | Off by default. On a few audio drivers muting the PC also silences the capture; if you get silence, turn it off. |
| **Open log** | Opens `%APPDATA%\AirplayFox\airplayfox.log`. |
| **Exit** | Disconnects and quits (restores PC volume if it was muted). |

Settings are saved in `%APPDATA%\AirplayFox\settings.json`.

### Choosing an AirPlay audio latency (0-4 s)

Latency is how long the speaker waits before playing, and it is also its safety margin: if a Wi-Fi packet is lost, the speaker asks for it again, and the resend must arrive before the speaker needs it. Lower = snappier but less margin.

- **0.25 s** worked cleanly with almost no visible sync lag on the author's HomePod mini, even though that link was losing around 15 % of packets (every one was resent in time). Start there for video. **Auto** is deliberately cautious and may pick a longer value than you need, so if Auto feels laggy, pick a fixed value.
- If you hear crackle or dropouts, step up (0.50 → 0.75 → 1.00 s).
- For music only, a higher value is fine and the most robust.
- Your result depends on your Wi-Fi. A speaker close to the router, or the router on a quiet channel, helps more than any setting.

## Troubleshooting: HomePod not listed, no sound, crackling

- **No speaker listed** — PC and speaker must be on the same network/subnet; set your network profile to *Private*; wait ~10 s for discovery.
- **Connected but silent** — make sure something is actually playing on the *default* Windows output device; turn off *Mute this PC's speakers*; check the speaker volume in the menu.
- **Crackle / dropouts** — raise the latency one step; prefer Ethernet on the PC and a strong signal at the speaker.
- **Fox shows but menu says "Reconnecting"** — the speaker stopped answering; it retries with backoff. See the log.
- **Nothing happens when I start it** — it is a tray app and only one copy runs at a time; the fox from the first start is probably already in the tray (check the `^` overflow).
- **Anything else** — attach `%APPDATA%\AirplayFox\airplayfox.log` to an issue.

## FAQ: AirPlay from Windows to a HomePod

### Can Windows AirPlay to a HomePod?
Windows has no system-wide AirPlay audio output. AirplayFox is a small tray app that sends the PC's system audio (anything the PC plays) to a HomePod over AirPlay 2. It was tested on one HomePod mini from Windows 10.

### Do I need iTunes, Bonjour or a virtual audio cable?
No. Discovery uses mDNS built into the app and audio is captured with WASAPI loopback. No driver is installed.

### Does it work with other AirPlay 2 speakers?
Unknown. Only a HomePod mini was tested; the full-size HomePod has not been. Speakers that accept HomeKit transient pairing might work. Reports are welcome through the speaker compatibility issue template.

### Does it work on Windows 11?
Not tested. It was tested on Windows 10 only.

### How do I fix lip-sync when watching video over AirPlay?
Set the latency (0.00-4.00 s) from the tray menu. On the author's HomePod mini, 0.25 s worked cleanly; your result depends on your Wi-Fi.

### Does it work on macOS or Linux, or with ALAC?
No. Windows only, and audio is sent as uncompressed PCM (no ALAC yet).

### Is it safe? Why the SmartScreen warning?
The binaries are unsigned. The source is MIT-licensed and the release includes SHA256SUMS.txt so you can verify the download or build from source.

### Is it affiliated with Apple?
No. See the trademark notice under Credits.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
git clone https://github.com/priteshkedar789/airplay-fox.git
cd airplay-fox
dotnet build src/AirplayFox -c Release
# exe: src/AirplayFox/bin/Release/net8.0-windows/AirplayFox.exe

dotnet test tests/AirplayFox.Tests          # ring buffer, drift rule, tuner, 48 kHz resampling, wire format
```

### Headless test mode

```powershell
AirplayFox.exe --cli --device Bedroom --latency 0.25 --seconds 30 --dump sent.wav
pip install numpy
python tools/analyze_sent.py sent.wav        # with a steady test tone playing: counts clicks in exactly what was sent
```

`AirplayFox.exe` is a windowed app, so `--cli` prints to the log (`%APPDATA%\AirplayFox\airplayfox.log`) rather than the console; redirect its output (`> out.txt`) if you want it on screen.

## How it works (AirPlay 2 / RAOP sender internals)

```
WASAPI loopback ─▶ RateConverter ─▶ PcmRing ─▶ PacketPump ─▶ RTP + ChaCha20 ─▶ speaker
 (device clock,    (e.g. 48k→44.1k,  (absorbs    (wall clock, 352-frame             │
  bursty)           whole chunks)     bursts)     packets, drift trim)   ◀── resend requests
```

- **Discovery:** mDNS `_raop._tcp`.
- **Pairing:** HomeKit *transient* pairing (SRP-6a, fixed PIN), then ChaCha20-Poly1305 on the control and event channels.
- **Audio:** 16-bit stereo PCM at 44.1 kHz, **big-endian on the wire**, one 352-frame RTP packet every 7.98 ms, encrypted, with a retransmit buffer so lost packets can be resent.
- **Timing:** NTP-style timing responses plus periodic sync packets; playout latency is negotiated per session.
- **Keep-alive:** RTSP `/feedback` every 2 s and answering the speaker's event-channel messages — without both, the speaker drops the session after ~30–45 s.
- **Auto latency:** a ~4 s probe streams inaudible silence, measures round-trip time and how many packets the speaker asks to be resent, and picks a latency from that. (The rule is deliberately cautious; fixed values are often lower.)

## Tested setup

| | |
|---|---|
| PC | Windows 10 Pro (build 19045), wired Ethernet |
| Speaker | HomePod mini (model MY5H2HN/A), software 26.6 (23L773), on Wi-Fi |
| Last checked | 2026-10-06, v1.0.0 |

Written and maintained by Pritesh Kedar. Compatibility reports for other speakers and Windows versions are welcome through the [issue templates](https://github.com/priteshkedar789/airplay-fox/issues/new/choose).

## Limitations

- Windows only. Audio is sent as uncompressed PCM (no ALAC yet).
- The audio-quality results above are judged by ear on one HomePod mini and one Wi-Fi network; the automated checks cover what is *sent*, not what is *heard*.
- AirPlay 2 is reverse-engineered; a firmware update can break senders like this one.
- Unsigned binaries (SmartScreen warning).

## Credits

Protocol behaviour was worked out by comparing against [pyatv](https://github.com/postlund/pyatv) (MIT) and by packet captures of a real HomePod session. Built with [NAudio](https://github.com/naudio/NAudio), [Makaretu.Dns.Multicast](https://github.com/richardschneider/net-mdns), [BouncyCastle](https://www.bouncycastle.org/) and [plist-cil](https://github.com/claunia/plist-cil).

Apple, AirPlay and HomePod are trademarks of Apple Inc. This project is not affiliated with or endorsed by Apple.

## License

[MIT](LICENSE) © 2026 Pritesh Kedar

---

## ☕ Buy me a coffee

AirplayFox is free and built in spare time. If it saved you from a cable or a subscription and you'd like to say thanks, you can send a tip:

| Coin | Address |
|---|---|
| **BTC** (Bitcoin) | `bc1q730l0j6xnvfvvhkrgzg9ytjul6eyv8pv6lg76w` |
| **BNB** | `0xd43C5a3662f7B2620161c4600A1dDf26545c4Db9` |
| **SOL** (Solana) | `3wL2Fj7Xmh28khWtHaF5jtSegD1pmkj3yorHWjGbLNEf` |
| **TRX** (Tron) | `TXoumqAU8FASfnoEgWmPedS9zg9K9PNuxd` |

Double-check the address and network before sending. A ⭐ on the repo helps just as much. Thank you!
