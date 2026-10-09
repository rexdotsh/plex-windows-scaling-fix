# plex-windows-scaling-fix

Plex for Windows sets its UI scale once, at startup, from whichever monitor your mouse pointer is on. Start it with the pointer on a 100% monitor, move it to a 200% one, and the UI only fills a quarter of the window.

This is a small launcher that puts the pointer on your highest-scaled monitor for the second it takes Plex to start, so the UI looks right everywhere.

## The bug

Plex's UI is a QtWebEngine 5.15.3 view (Chromium) inside Plex's own native window. At startup, Qt creates Plex's windows on the monitor under the mouse pointer ([`effectiveScreen()` in `qplatformwindow.cpp`](https://github.com/qt/qtbase/blob/v5.15.3-lts-lgpl/src/gui/kernel/qplatformwindow.cpp#L631-L651)), and Plex then moves them into its own window. QtWebEngine takes its scale from that first monitor and never updates it.

With Plex's web inspector on, you can see it directly. Start Plex with the pointer on a 100% monitor and `window.devicePixelRatio` is `1`, even when Plex opens fullscreen on a 200% monitor. Resizing, minimizing, moving, and reloading the page leave it at `1`. Start Plex with the pointer on the 200% monitor and it's `2`, and the UI stays correct when you move the window to a 100% monitor.

Where Plex opens doesn't matter, only where the pointer is.

## What the launcher does

When you open Plex through `PlexLauncher.exe`, it:

1. finds the connected monitor with the highest scaling (ties go to the primary, then the larger one);
2. moves the pointer to the middle of that monitor, if it's somewhere else;
3. starts Plex and waits until Plex has created its main UI window, usually about a second;
4. moves the pointer back, unless you've moved it in the meantime.

If the pointer is already on that monitor, it just starts Plex. If Plex is already running, it brings the existing window to the front instead. It never changes Plex's settings or files.

You get the same result without the launcher by opening Plex with the pointer on your highest-scaled monitor.

## Compatibility

- Windows 10 1607 or later, x64
- Plex for Windows 1.115.0 (tested on 1.115.0.426)

Needs nothing beyond what ships with Windows. Plex HTPC is untested.

## Install

1. Download the ZIP from [Releases](https://github.com/rexdotsh/plex-windows-scaling-fix/releases).
2. Extract `PlexLauncher.exe` somewhere it can stay, for example `%LOCALAPPDATA%\Programs\PlexLauncher`.
3. From a terminal in that folder, run `.\PlexLauncher.exe --install`. This adds **Plex (Scaling Fix)** to the Start menu with Plex's icon.
4. Open Plex from that shortcut from now on. Pin it if you like.

To uninstall, run `.\PlexLauncher.exe --uninstall` and delete the folder.

## Options

| Option | Effect |
|---|---|
| `--install` | Add **Plex (Scaling Fix)** to the Start menu |
| `--uninstall` | Remove it |
| `--plex <path>` | Use this `Plex.exe`. By default it looks in Program Files, then at Plex's uninstall entry. |

Each run writes what it did to `PlexLauncher.log` next to the exe.

## Why a launcher

The proper fix is inside Plex: tell QtWebEngine about the new scale when the window changes screens. Plex is closed source, and nothing reachable from outside the process makes the view re-read its scale. Fixing it in-process would mean injecting a native Qt plugin built against Plex's exact Qt, and that breaks with every Plex update.

The pointer position is the only thing outside Plex that decides the scale, so the launcher sets that. It survives Plex updates and is one C# file.

## Notes

**The pointer jumps for about a second** when you open Plex from another monitor. If you move the mouse during that second, the launcher leaves the pointer where you put it, but Plex may pick up the wrong scale.

**It only helps when you open Plex through the launcher.** The original Plex shortcut, and Plex restarting itself after an update, skip it.

**Monitors on different GPUs.** On laptops where each monitor is driven by a different GPU, moving Plex between them can refresh the scale, so you may not see the bug there.

**Taskbar pins.** If you pin the launcher, the running Plex window shows up as a separate taskbar icon.

**Not signed.** SmartScreen will warn on first launch.

## Build

The .NET Framework C# compiler ships with Windows, so no SDK or Visual Studio is needed:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:PlexLauncher.exe PlexLauncher.cs
```

Tagging a commit `v0.1.0` runs the same build in Actions and publishes a GitHub Release.

## License

MIT. Not affiliated with Plex, Inc.
