// Plex for Windows locks its UI scale to whichever monitor it opens on. Opening
// on a 100% monitor and moving to a 200% monitor leaves the UI drawn into a
// quarter of the window; going the other way is fine. So before launching,
// point Plex's saved screen (plex.ini [ui] screen=) at the highest-scaled
// monitor that is currently connected.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

static class PlexLauncher
{
    const string ShortcutName = "Plex (Scaling Fix).lnk";
    const string Title = "Plex Scaling Fix";
    const string Usage =
        "PlexLauncher.exe [options]\n\n" +
        "Without options, opens Plex on the highest-scaled monitor.\n\n" +
        "--install\tAdd \"Plex (Scaling Fix)\" to the Start menu\n" +
        "--uninstall\tRemove it\n" +
        "--plex <path>\tUse this Plex.exe\n" +
        "--ini <path>\tEdit this plex.ini instead of Plex's own\n" +
        "--no-launch\tOnly update plex.ini, don't start Plex";

    static readonly string ExePath = typeof(PlexLauncher).Assembly.Location;
    static readonly string LogPath = Path.Combine(Path.GetDirectoryName(ExePath), "PlexLauncher.log");
    static readonly string ShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

    class Monitor
    {
        public string Name;
        public uint Dpi;
        public bool Primary;
        public long Area;
    }

    [STAThread]
    static void Main(string[] args)
    {
        string iniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Plex\plex.ini");
        string plexOverride = null;
        string command = null;
        bool launch = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--install" || args[i] == "--uninstall")
                command = args[i];
            else if (args[i] == "--plex" && i + 1 < args.Length)
                plexOverride = args[++i];
            else if (args[i] == "--ini" && i + 1 < args.Length)
                iniPath = args[++i];
            else if (args[i] == "--no-launch")
                launch = false;
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

        var log = new List<string>();
        log.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            if (launch && ActivateRunningPlex(log))
                return;

            try
            {
                Monitor target = PickTargetMonitor(log);
                if (target != null)
                    SetSavedScreen(iniPath, target.Name, log);
            }
            catch (Exception e)
            {
                // Never let the workaround stop Plex from opening.
                log.Add("Could not update plex.ini: " + e.Message);
            }

            if (launch)
            {
                var start = new ProcessStartInfo(plexExe);
                start.UseShellExecute = true;
                start.WorkingDirectory = Path.GetDirectoryName(plexExe);
                Process.Start(start);
                log.Add("Started " + plexExe);
            }
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
            link.SetDescription("Open Plex on the highest-scaled monitor so its UI scales correctly");
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
        IntPtr previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
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
                    m.Area = (long)(info.rcMonitor.Right - info.rcMonitor.Left) * (info.rcMonitor.Bottom - info.rcMonitor.Top);
                    monitors.Add(m);
                    log.Add(string.Format("Monitor {0}: {1}% scale, {2} px{3}",
                        m.Name, m.Dpi * 100 / 96, m.Area, m.Primary ? ", primary" : ""));
                }
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }

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

    static void SetSavedScreen(string iniPath, string screenName, List<string> log)
    {
        if (!File.Exists(iniPath))
        {
            log.Add(iniPath + " does not exist yet, leaving it alone.");
            return;
        }

        string text = File.ReadAllText(iniPath);
        // Split on \n and keep any \r on each line so existing line endings survive untouched.
        string cr = text.Contains("\r\n") ? "\r" : "";
        // QSettings escapes backslashes: \\.\DISPLAY1 is stored as \\\\.\\DISPLAY1
        string entry = "screen=" + screenName.Replace(@"\", @"\\");
        var lines = new List<string>(text.Split('\n'));

        int section = lines.FindIndex(l => l.TrimEnd('\r').Trim() == "[ui]");
        if (section < 0)
        {
            if (lines[lines.Count - 1] != "")
            {
                lines[lines.Count - 1] += cr;
                lines.Add("");
            }
            lines.Insert(lines.Count - 1, cr);
            lines.Insert(lines.Count - 1, "[ui]" + cr);
            lines.Insert(lines.Count - 1, entry + cr);
        }
        else
        {
            int end = lines.FindIndex(section + 1, l => l.StartsWith("["));
            if (end < 0)
                end = lines.Count;
            int existing = lines.FindIndex(section + 1, end - section - 1, l => l.StartsWith("screen="));
            if (existing >= 0)
            {
                string current = lines[existing].TrimEnd('\r');
                if (current == entry)
                {
                    log.Add("plex.ini already points at " + screenName);
                    return;
                }
                log.Add("Replacing " + current);
                lines[existing] = entry + (lines[existing].EndsWith("\r") ? "\r" : "");
            }
            else
            {
                lines.Insert(section + 1, entry + cr);
            }
        }

        string tmp = iniPath + ".launcher-tmp";
        File.WriteAllText(tmp, string.Join("\n", lines.ToArray()), new UTF8Encoding(false));
        File.Replace(tmp, iniPath, iniPath + ".launcher-bak");
        log.Add("Wrote " + entry + " to " + iniPath);
    }

    const int SW_RESTORE = 9;
    const int MDT_EFFECTIVE_DPI = 0;
    const uint MONITORINFOF_PRIMARY = 1;
    const uint MB_ICONERROR = 0x10;
    const uint MB_ICONINFORMATION = 0x40;
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

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

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

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
