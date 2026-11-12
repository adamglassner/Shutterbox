using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Shutterbox.Services;

/// <summary>Small helpers that hand things over to Windows: Recycle Bin, File Explorer, other apps, the clipboard.</summary>
public static class Shell
{
    // ---------------- Recycle Bin ----------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    /// <summary>Moves a file to the Recycle Bin (so it can still be restored from there). Returns true if it's gone.</summary>
    public static bool SendToRecycleBin(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
            };
            int r = SHFileOperation(ref op);
            return r == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
        }
        catch { return false; }
    }

    // ---------------- Explorer and other apps ----------------
    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else if (Directory.Exists(IOPath.GetDirectoryName(path))) Process.Start("explorer.exe", $"\"{IOPath.GetDirectoryName(path)}\"");
        }
        catch { }
    }

    public static bool Open(string pathOrUrl, string verb = null)
    {
        try
        {
            var psi = new ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
            if (verb != null) psi.Verb = verb;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Opens the photo in the app Windows uses for editing it (Photos, Paint…), or for opening it.</summary>
    public static void Edit(string path)
    {
        if (!Open(path, "edit")) Open(path);
    }

    public static void OpenWith(string path)
    {
        try { Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}"); } catch { }
    }

    public static void OpenMap(double lat, double lon)
    {
        var la = lat.ToString("0.######", CultureInfo.InvariantCulture);
        var lo = lon.ToString("0.######", CultureInfo.InvariantCulture);
        Open($"https://www.openstreetmap.org/?mlat={la}&mlon={lo}#map=15/{la}/{lo}");
    }

    // ---------------- desktop background ----------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, string vparam, uint winIni);

    public static bool SetWallpaper(string path)
    {
        try { return SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, path, 0x01 | 0x02); }
        catch { return false; }
    }

    // ---------------- clipboard ----------------
    public static bool CopyFiles(IEnumerable<string> paths)
    {
        try
        {
            var list = new System.Collections.Specialized.StringCollection();
            foreach (var p in paths) list.Add(p);
            if (list.Count == 0) return false;
            Clipboard.SetFileDropList(list);
            return true;
        }
        catch { return false; }
    }

    public static bool CopyImage(BitmapSource image)
    {
        try { Clipboard.SetImage(image); return true; }
        catch { return false; }
    }
}
