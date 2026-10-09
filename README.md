# plex-windows-scaling-fix

Plex for Windows locks its UI scale to whichever monitor it opens on. Open it on a 100% monitor, drag it to a 200% one, and the UI only fills a quarter of the window.

This is a small launcher that always opens Plex on your highest-scaled monitor, which avoids the bug entirely.

## The bug

The window itself handles monitor changes fine. `Plex.exe` declares per-monitor DPI awareness (`PerMonitorV2`) and the frame resizes correctly. The UI inside it is a QtWebEngine 5.15.3 view, and its device scale factor is read once, when the view is created.

With Plex's web inspector on, open Plex on a 100% monitor and move it to a 200% one. `window.devicePixelRatio` still says `1`. Plex renders a 1x frame and draws it at 2x density, so the UI ends up in one corner. Resizing, minimizing, moving, and reloading the page all leave it at `1`. Only restarting Plex on the other monitor fixes it.

Going the other way is fine. A UI created at 2x and moved to a 1x monitor is just rendered at a higher resolution than it needs.

## What the launcher does

Every time you open Plex through it, `PlexLauncher.exe`:

1. finds the connected monitor with the highest scaling (ties go to the primary, then the larger one);
2. points Plex's saved screen at it (`screen=` under `[ui]` in `%LOCALAPPDATA%\Plex\plex.ini`);
3. starts Plex, which opens there at the right scale. Dragging it to a lower-scaled monitor afterwards looks fine.

If Plex is already running, it brings the existing window to the front instead.

Only that one line of `plex.ini` changes. The previous file is kept as `plex.ini.launcher-bak`, and Plex's install is never touched. If anything goes wrong, Plex still starts.

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
| `--ini <path>` | Edit this `plex.ini` instead of Plex's own |
| `--no-launch` | Only update `plex.ini`, don't start Plex |

Each run writes what it did to `PlexLauncher.log` next to the exe.

## Why a launcher

The proper fix is inside Plex: tell QtWebEngine about the new scale when the window changes screens. Plex is closed source, and nothing reachable from outside the process makes the view re-read its scale. Fixing it in-process would mean injecting a native Qt plugin built against Plex's exact Qt, and that breaks with every Plex update.

Controlling where Plex opens sidesteps all of that, survives updates, and is one C# file.

## Notes

**It only helps when you open Plex through the launcher.** The original Plex shortcut, and Plex restarting itself after an update, skip it.

**It always opens on the highest-scaled monitor.** Drag Plex wherever you want it afterwards.

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
