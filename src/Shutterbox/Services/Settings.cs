using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shutterbox.Services;

public enum ThemeMode { Dark, Light, System }
public enum TileDensity { Compact, Comfortable, Large }
public enum ImportLayout { YearAndDay, YearAndMonth, NoFolders }

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1.0";

    public const string RepoUrl = "https://github.com/adamglassner/shutterbox";

    public static string ReadChangelog()
    {
        try
        {
            using var s = typeof(AppInfo).Assembly.GetManifestResourceStream("CHANGELOG.md");
            if (s == null) return "";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return ""; }
    }
}

/// <summary>Preferences. Saved in %APPDATA%\Shutterbox\settings.json. The library itself lives in library.json beside it.</summary>
public sealed class AppSettings
{
    // ---- Appearance ----
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public string Accent { get; set; } = "Amber";
    public TileDensity Density { get; set; } = TileDensity.Comfortable;
    public bool ShowInfoPanel { get; set; } = true;

    // ---- Library ----
    public bool ScanOnStart { get; set; } = true;
    public bool WatchFolders { get; set; } = true;

    // ---- Import ----
    public string ImportFolder { get; set; } = "";
    public ImportLayout ImportLayout { get; set; } = ImportLayout.YearAndDay;
    public bool ImportSkipDuplicates { get; set; } = true;
    public bool ImportDeleteOriginals { get; set; } = false;
    public string LastImportSource { get; set; } = "";

    // ---- Locked album ----
    /// <summary>Where the Locked album's folder is (empty = %APPDATA%\Shutterbox\Locked). Can be a OneDrive/Dropbox folder or a USB drive.</summary>
    public string LockedFolder { get; set; } = "";
    /// <summary>Identifies this PC in the Locked album's log of wrong guesses.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>Lock it when Shutterbox is minimized, you switch to another app, or Windows locks.</summary>
    public bool LockOnLeave { get; set; } = false;
    /// <summary>Hide "Locked" in the sidebar (open it with Ctrl+Shift+L or by typing the PIN in search).</summary>
    public bool HideLocked { get; set; } = false;
    /// <summary>Keep the window out of screenshots, recordings and screen sharing while the Locked album is unlocked.</summary>
    public bool ProtectScreen { get; set; } = true;
    /// <summary>Ctrl+Alt+L anywhere in Windows locks it and minimizes Shutterbox.</summary>
    public bool PanicHotkey { get; set; } = true;
    /// <summary>Wrong guesses in a row on this PC, and until when to wait before the next.</summary>
    public int WrongInARow { get; set; }
    public DateTime? WaitUntil { get; set; }

    // ---- People and things ----
    /// <summary>Find faces and what's in each photo, on this PC, in the background.</summary>
    public bool FindPeopleAndThings { get; set; } = true;

    // ---- Viewer ----
    public int SlideshowSeconds { get; set; } = 5;

    // ---- Window ----
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = 1360;
    public double WinHeight { get; set; } = 860;
    public bool WinMaximized { get; set; } = false;

    public static string DefaultImportFolder =>
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Shutterbox");
}

public static class SettingsService
{
    /// <summary>%APPDATA%\Shutterbox. The self-test points this at a temporary folder.</summary>
    public static string Folder { get; set; } =
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shutterbox");

    /// <summary>%LOCALAPPDATA%\Shutterbox: thumbnail cache (safe to delete).</summary>
    public static string CacheFolder { get; set; } =
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shutterbox");

    public static string FilePath => IOPath.Combine(Folder, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static event Action Changed;

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (s != null) Current = s;
            }
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            Current = new AppSettings();
        }
        Sanitize(Current);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, true);
        }
        catch { /* best effort */ }
    }

    /// <summary>Save and tell the window to re-apply settings.</summary>
    public static void Commit()
    {
        Sanitize(Current);
        Save();
        Changed?.Invoke();
    }

    private static void Sanitize(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ImportFolder)) s.ImportFolder = AppSettings.DefaultImportFolder;
        s.Accent ??= "Amber";
        s.LastImportSource ??= "";
        s.LockedFolder ??= "";
        if (string.IsNullOrEmpty(s.DeviceId)) s.DeviceId = Guid.NewGuid().ToString("N")[..12];
        s.SlideshowSeconds = Math.Clamp(s.SlideshowSeconds, 2, 60);
        if (double.IsNaN(s.WinWidth) || s.WinWidth < 720) s.WinWidth = 1360;
        if (double.IsNaN(s.WinHeight) || s.WinHeight < 480) s.WinHeight = 860;
    }
}
