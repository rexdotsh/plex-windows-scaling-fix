// Plex for Windows fixes its UI scale at startup, from the monitor under the
// mouse pointer: Qt creates Plex's windows there before Plex moves them into
// place, and the embedded browser never re-reads the scale. Started from a 100%
// monitor, the UI is then drawn into a quarter of the window on a 200% monitor;
// started at 200%, it looks right everywhere. So if the pointer isn't on the
// highest-scaled monitor, park it there until Plex has created its UI window,
// then put it back.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Microsoft.Win32;

static class PlexLauncher
{
    const string ShortcutName = "Plex (Scaling Fix).lnk";
    const string Title = "Plex Scaling Fix";
    const string Usage =
        "PlexLauncher.exe [options]\n\n" +
        "Without options, opens Plex so its UI scales correctly on every monitor.\n\n" +
        "--install\tAdd \"Plex (Scaling Fix)\" to the Start menu\n" +
        "--uninstall\tRemove it\n" +
        "--plex <path>\tUse this Plex.exe";

    // Plex creates two Qt windows at startup: the player cover, then the main
    // window holding the web UI. The UI's scale is fixed once the second exists.
    const int QtWindowsToWaitFor = 2;
    const int MaxPointerHoldMs = 10000;

    static readonly string ExePath = typeof(PlexLauncher).Assembly.Location;
    static readonly string LogPath = Path.Combine(Path.GetDirectoryName(ExePath), "PlexLauncher.log");
    static readonly string ShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

    class Monitor
    {
        public string Name;
        public uint Dpi;
        public bool Primary;
        public RECT Bounds;

        public long Area { get { return (long)(Bounds.Right - Bounds.Left) * (Bounds.Bottom - Bounds.Top); } }

        public bool Contains(POINT p)
        {
            return p.X >= Bounds.Left && p.X < Bounds.Right && p.Y >= Bounds.Top && p.Y < Bounds.Bottom;
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        string plexOverride = null;
        string command = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--install" || args[i] == "--uninstall")
                command = args[i];
            else if (args[i] == "--plex" && i + 1 < args.Length)
                plexOverride = args[++i];
            else
            {
                MessageBoxW(IntPtr.Zero, "Unknown option: " + args[i] + "\n\n" + Usage, Title, MB_ICONERROR);
                return;
            }
        }

        string plexExe = plexOverride ?? FindPlexExe();

        if (command == "--install")
        {
            Install(plexExe);
            return;
        }
        if (command == "--uninstall")
        {
            Uninstall();
            return;
        }

        // Work in physical pixels so pointer and monitor coordinates line up.
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var log = new List<string>();
        log.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            if (ActivateRunningPlex(log))
                return;

            Monitor target = null;
            try
            {
                target = PickTargetMonitor(log);
            }
            catch (Exception e)
            {
                // Never let the workaround stop Plex from opening.
                log.Add("Could not read monitors: " + e.Message);
            }

            POINT original;
            GetCursorPos(out original);
            bool park = target != null && !target.Contains(original);
            POINT parked = new POINT();
            if (park)
            {
                parked.X = (target.Bounds.Left + target.Bounds.Right) / 2;
                parked.Y = (target.Bounds.Top + target.Bounds.Bottom) / 2;
                SetCursorPos(parked.X, parked.Y);
                log.Add(string.Format("Moved the pointer from ({0},{1}) to ({2},{3})", original.X, original.Y, parked.X, parked.Y));
            }
            else if (target != null)
            {
                log.Add("Pointer is already on " + target.Name);
            }

            Process plex;
            try
            {
                var start = new ProcessStartInfo(plexExe);
                start.UseShellExecute = true;
                start.WorkingDirectory = Path.GetDirectoryName(plexExe);
                plex = Process.Start(start);
                log.Add("Started " + plexExe);
            }
            catch
            {
                if (park)
                    SetCursorPos(original.X, original.Y);
                throw;
            }

            if (park)
                WaitThenRestorePointer(plex, parked, original, log);
        }
        catch (Exception e)
        {
            log.Add("Failed: " + e);
            MessageBoxW(IntPtr.Zero, "Could not start Plex (" + plexExe + "):\n" + e.Message, Title, MB_ICONERROR);
        }
        finally
        {
            try { File.WriteAllLines(LogPath, log.ToArray()); }
            catch (Exception) { }
        }
    }

    static void WaitThenRestorePointer(Process plex, POINT parked, POINT original, List<string> log)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            POINT now;
            GetCursorPos(out now);
            if (Math.Abs(now.X - parked.X) > 2 || Math.Abs(now.Y - parked.Y) > 2)
            {
                log.Add("Pointer was moved after " + timer.ElapsedMilliseconds + " ms, leaving it alone");
                return;
            }
            if (plex == null || plex.HasExited)
            {
                log.Add("Plex exited after " + timer.ElapsedMilliseconds + " ms");
                break;
            }
            int qtWindows = CountQtWindows(plex.Id);
            if (qtWindows >= QtWindowsToWaitFor)
            {
                log.Add("Plex created its UI window after " + timer.ElapsedMilliseconds + " ms");
                break;
            }
            if (timer.ElapsedMilliseconds > MaxPointerHoldMs)
            {
                log.Add("Gave up waiting for Plex's UI window after " + timer.ElapsedMilliseconds + " ms (" + qtWindows + " Qt windows)");
                break;
            }
            Thread.Sleep(10);
        }
        SetCursorPos(original.X, original.Y);
        log.Add(string.Format("Moved the pointer back to ({0},{1})", original.X, original.Y));
    }

    static int CountQtWindows(int pid)
    {
        int count = 0;
        EnumWindowsProc visit = null;
        visit = delegate (IntPtr hwnd, IntPtr data)
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != pid)
                return true;
            var cls = new StringBuilder(64);
            GetClassNameW(hwnd, cls, cls.Capacity);
            string name = cls.ToString();
            // Qt window classes look like "Qt5153QWindowOwnDCIcon".
            if (name.StartsWith("Qt") && name.Contains("QWindow"))
                count++;
            return true;
        };
        EnumWindows(delegate (IntPtr hwnd, IntPtr data)
        {
            visit(hwnd, data);
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            // Plex embeds its Qt windows in its own top-level window.
            if (owner == pid)
                EnumChildWindows(hwnd, visit, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return count;
    }

    static string FindPlexExe()
    {
        string standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Plex\Plex\Plex.exe");
        if (File.Exists(standard))
            return standard;

        // The installer records Plex.exe as the uninstall entry's icon.
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Plex"))
                {
                    string icon = key == null ? null : key.GetValue("DisplayIcon") as string;
                    if (string.IsNullOrEmpty(icon))
                        continue;
                    icon = icon.Trim('"');
                    int comma = icon.LastIndexOf(',');
                    if (comma > 0 && !icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        icon = icon.Substring(0, comma);
                    string path = Path.GetFullPath(icon);
                    if (File.Exists(path))
                        return path;
                }
            }
        }
        return standard;
    }

    static void Install(string plexExe)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(ExePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(ExePath));
            link.SetDescription("Open Plex so its UI scales correctly on every monitor");
            link.SetIconLocation(File.Exists(plexExe) ? plexExe : ExePath, 0);
            ((IPersistFile)link).Save(ShortcutPath, false);
            MessageBoxW(IntPtr.Zero,
                "Added \"Plex (Scaling Fix)\" to the Start menu.\n\nOpen Plex from it from now on. It points at:\n" + ExePath +
                "\n\nIf you move this file, run --install again.", Title, MB_ICONINFORMATION);
        }
        catch (Exception e)
        {
            MessageBoxW(IntPtr.Zero, "Could not create " + ShortcutPath + ":\n" + e.Message, Title, MB_ICONERROR);
        }
    }

    static void Uninstall()
    {
        try
        {
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
            MessageBoxW(IntPtr.Zero,
                "Removed \"Plex (Scaling Fix)\" from the Start menu.\n\nYou can delete this folder now:\n" +
                Path.GetDirectoryName(ExePath), Title, MB_ICONINFORMATION);
        }
        catch (Exception e)
        {
            MessageBoxW(IntPtr.Zero, "Could not delete " + ShortcutPath + ":\n" + e.Message, Title, MB_ICONERROR);
        }
    }

    static bool ActivateRunningPlex(List<string> log)
    {
        foreach (Process p in Process.GetProcessesByName("Plex"))
        {
            IntPtr hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                continue;
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            log.Add("Plex is already running (pid " + p.Id + "), brought it to the front.");
            return true;
        }
        return false;
    }

    static Monitor PickTargetMonitor(List<string> log)
    {
        var monitors = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            uint dpiX, dpiY;
            if (GetMonitorInfo(hMonitor, ref info) && GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0)
            {
                var m = new Monitor();
                m.Name = info.szDevice;
                m.Dpi = dpiX;
                m.Primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                m.Bounds = info.rcMonitor;
                monitors.Add(m);
                log.Add(string.Format("Monitor {0}: {1}% scale, ({2},{3})-({4},{5}){6}",
                    m.Name, m.Dpi * 100 / 96, m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom,
                    m.Primary ? ", primary" : ""));
            }
            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
            return null;

        monitors.Sort(delegate (Monitor a, Monitor b)
        {
            if (a.Dpi != b.Dpi) return b.Dpi.CompareTo(a.Dpi);
            if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
            return b.Area.CompareTo(a.Area);
        });
        log.Add("Target: " + monitors[0].Name);
        return monitors[0];
    }

    const int SW_RESTORE = 9;
    const int MDT_EFFECTIVE_DPI = 0;
    const uint MONITORINFOF_PRIMARY = 1;
    const uint MB_ICONERROR = 0x10;
    const uint MB_ICONINFORMATION = 0x40;
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
