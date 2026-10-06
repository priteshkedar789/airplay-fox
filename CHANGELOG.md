# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0]

First release.

### Added
- Windows tray app that streams system audio to a HomePod over AirPlay 2 (tested on one HomePod mini from Windows 10; other AirPlay 2 speakers untested).
- Adjustable latency from 0.00 to 4.00 s: presets, a custom value, or Auto (measures your Wi-Fi at connect).
- Separate capture and send threads, precise packet pacing and per-packet clock-drift correction.
- Saved speaker volume (default 33 %), never forced to 100 %.
- Automatic reconnect with backoff; follows the Windows default output device.
- Optional "mute this PC's speakers while streaming".
- Headless `--cli` test mode and `tools/analyze_sent.py` click analysis.

### Known limitations
- Windows only; audio is sent as uncompressed PCM (no ALAC).
- Unsigned binaries (SmartScreen warning).

[1.0.0]: https://github.com/priteshkedar789/airplay-fox/releases/tag/v1.0.0
