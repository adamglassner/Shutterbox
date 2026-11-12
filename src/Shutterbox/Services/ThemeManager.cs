using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Shutterbox.Services;

/// <summary>
/// Builds the palette in code and swaps it into Application.Resources (every control uses DynamicResource,
/// so switching is instant). Dark is the default "darkroom" look so photos stand out; Light is a warm paper look.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    public static readonly (string Name, Color Light, Color Dark)[] Accents =
    {
        ("Amber", C("#A35E0A"), C("#E8A33D")),
        ("Sky",   C("#0B6FB8"), C("#5FB3E8")),
        ("Sage",  C("#2E7D45"), C("#7FCB8E")),
        ("Coral", C("#B3412C"), C("#E57B6B")),
        ("Iris",  C("#6D4AD6"), C("#B3A1FA")),
    };

    public static event Action ThemeChanged;

    private static ResourceDictionary _current;

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { }
        return false;
    }

    public static void Apply(AppSettings s)
    {
        IsDark = s.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemUsesDark(),
        };

        var d = new ResourceDictionary();
        void B(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); d[key] = b; d[key + "Color"] = c; }

        var accent = Pick(s.Accent);
        Color acc;
        if (IsDark)
        {
            acc = accent.Dark;
            B("WindowBg", C("#111213"));
            B("ChromeBg", C("#111213"));
            B("SidebarBg", C("#17191B"));
            B("RibbonBg", C("#17191B"));
            B("RibbonBorder", C("#2C3033"));
            B("Text", C("#ECEAE6"));
            B("TextSecondary", C("#A4A8AC"));
            B("TextTertiary", C("#8D9196"));
            B("TextDisabled", C("#5E6266"));
            B("ControlBg", C("#00FFFFFF"));
            B("ControlHoverBg", C("#1AFFFFFF"));
            B("ControlPressedBg", C("#26FFFFFF"));
            B("ControlBorder", C("#2C3033"));
            B("InputBg", C("#1C1E20"));
            B("InputBorder", C("#34383C"));
            B("ButtonBg", C("#26292C"));
            B("ButtonHoverBg", C("#30343A"));
            B("Divider", C("#26292C"));
            B("MenuBg", C("#1F2224"));
            B("MenuHover", C("#2C3033"));
            B("ToolTipBg", C("#1F2224"));
            B("ScrollThumb", C("#4A4F54"));
            B("ScrollThumbHover", C("#6B7076"));
            B("DialogBg", C("#17191B"));
            B("CardBg", C("#1F2224"));
            B("PanelBg", C("#151618"));
            B("TileBg", C("#1C1E20"));
            B("ChipBg", C("#26292C"));
            B("NavSelectedBg", C("#26292C"));
            B("DangerText", C("#FF8F86"));
            B("Success", C("#5FCB8B"));
        }
        else
        {
            acc = accent.Light;
            B("WindowBg", C("#F6F4F0"));
            B("ChromeBg", C("#F6F4F0"));
            B("SidebarBg", C("#EDEAE4"));
            B("RibbonBg", C("#EDEAE4"));
            B("RibbonBorder", C("#DAD5CC"));
            B("Text", C("#1D1C1A"));
            B("TextSecondary", C("#5B5852"));
            B("TextTertiary", C("#6E6A63"));
            B("TextDisabled", C("#A8A39A"));
            B("ControlBg", C("#00FFFFFF"));
            B("ControlHoverBg", C("#12000000"));
            B("ControlPressedBg", C("#1F000000"));
            B("ControlBorder", C("#D6D1C7"));
            B("InputBg", C("#FFFFFF"));
            B("InputBorder", C("#CFC9BE"));
            B("ButtonBg", C("#FFFFFF"));
            B("ButtonHoverBg", C("#F3F0EA"));
            B("Divider", C("#E2DDD4"));
            B("MenuBg", C("#FFFFFF"));
            B("MenuHover", C("#EFEBE4"));
            B("ToolTipBg", C("#FFFFFF"));
            B("ScrollThumb", C("#B8B2A7"));
            B("ScrollThumbHover", C("#8E877C"));
            B("DialogBg", C("#F6F4F0"));
            B("CardBg", C("#FFFFFF"));
            B("PanelBg", C("#FBFAF7"));
            B("TileBg", C("#E6E2DA"));
            B("ChipBg", C("#ECE8E1"));
            B("NavSelectedBg", C("#E0DBD1"));
            B("DangerText", C("#B3261E"));
            B("Success", C("#2E8B57"));
        }

        B("Accent", acc);
        B("AccentHover", Blend(acc, IsDark ? Colors.White : Colors.Black, 0.12));
        B("AccentText", IsDark ? C("#1A1408") : Colors.White);
        B("AccentSoft", Color.FromArgb((byte)(IsDark ? 0x40 : 0x26), acc.R, acc.G, acc.B));
        B("AccentSoftHover", Color.FromArgb((byte)(IsDark ? 0x55 : 0x38), acc.R, acc.G, acc.B));
        B("Selection", Color.FromArgb(0x66, acc.R, acc.G, acc.B));
        B("FocusRing", acc);
        // The viewer is always dark, whatever the theme
        B("ViewerBg", C("#F2080809"));
        B("ViewerText", C("#ECEAE6"));
        B("ViewerTextSecondary", C("#A4A8AC"));
        B("ViewerButtonHover", C("#26FFFFFF"));

        var app = Application.Current;
        if (_current != null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Insert(0, d);
        _current = d;
        app.Resources["UiFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI");
        app.Resources["DisplayFont"] = new FontFamily("Segoe UI Variable Display, Segoe UI");
        app.Resources["UiFontSize"] = 14.0;

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke();
    }

    public static (string Name, Color Light, Color Dark) Pick(string name)
    {
        var a = Accents.FirstOrDefault(x => x.Name == name);
        return a.Name == null ? Accents[0] : a;
    }

    // ---------- native dark title bar ----------
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        // Windows 11: caption colour matches the window background
        var bg = IsDark ? C("#111213") : C("#F6F4F0");
        int colorRef = bg.R | (bg.G << 8) | (bg.B << 16);
        DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int));
        if (w.IsVisible && Environment.OSVersion.Version.Build < 22000 && w.WindowState == WindowState.Normal)
        {
            w.Width += 1; w.Width -= 1;
        }
    }

    public static void Attach(Window w) => w.SourceInitialized += (_, _) => ApplyTitleBar(w);

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
