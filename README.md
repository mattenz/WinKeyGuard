# WinKeyGuard

A Windows tray app that disables the Windows keys on a Keychron Q6 Max while a game has focus, and restores them when it loses focus.

It does this on the keyboard itself: it remaps the `KC_LGUI` / `KC_RGUI` keymap entries to `KC_NO` over the VIA raw HID protocol, so no low-level keyboard hook is involved (which some anti-cheat systems dislike).

## Features

- Watches the foreground window and disables the Win keys when one of the configured programs (default `TslGame.exe`) has focus.
- Choose which keys to disable: left Win, right Win, or both.
- Adjustable delays before disabling and before restoring. The restore delay absorbs focus flicker when alt-tabbing.
- Stateless and self-repairing: every apply compares the live keymap with a saved backup, so a keyboard left disabled after a crash gets fixed on the next run.
- Tray icon shows whether the keys are enabled, disabled, or something needs attention.
- Optional "Start with Windows".

Settings live in `%APPDATA%\WinKeyGuard\` (`config.json`, `keymap-backup.json`, `log.txt`). The keymap backup is taken automatically from the keyboard on first run. Take it while the Win keys work.

## Building

Requires the .NET 10 SDK.

```powershell
.\publish.ps1
```

This produces `publish\WinKeyGuard.exe`, a single framework-dependent EXE (about 600 KB) that needs the .NET 10 Desktop Runtime installed.

## Optional: red Win key LEDs (custom firmware)

`firmware/keymap.c` is the Keychron Q6 Max ANSI (`keychron` keymap) with an RGB matrix indicator added. Whenever a Win key resolves to `KC_NO` on the active layers, its LED turns red. This works with any lighting effect, including Mix RGB, and while the backlight is off.

`firmware/build_fw.sh` builds it under WSL (no sudo). The script:

1. Downloads the Arm GNU toolchain.
2. Clones [Keychron/qmk_firmware](https://github.com/Keychron/qmk_firmware) branch `2025q3`.
3. Builds the firmware and copies `keychron_q6_max_ansi_encoder_keychron.bin` into `firmware/`.

Run it with:

```bash
bash firmware/build_fw.sh
```

Flash the `.bin` with QMK Toolbox, or with `dfu-util -a 0 -s 0x08000000:leave -D <file>.bin`. Put the keyboard in DFU mode with the reset button under the space bar. Holding Esc while plugging in also enters DFU mode, but it wipes your settings.

Flashing resets the keyboard's settings (keymap, macros, lighting), so back up your layout in Keychron Launcher first. It's also worth taking a full flash dump of the stock firmware so you can go back:

```
dfu-util -a 0 -s 0x08000000:262144 -U backup.bin
```

`firmware/keymap.c` is derived from QMK/Keychron firmware and is licensed under GPL-2.0-or-later.
