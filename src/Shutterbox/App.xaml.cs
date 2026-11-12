using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox;

public partial class App : Application
{
    /// <summary>Set by "--self-test &lt;output.txt&gt; [screenshot folder]" (used by the build to test Shutterbox).</summary>
    public static string SelfTestOutput { get; private set; }
    public static string SelfTestShots { get; private set; }
    public static string SelfTestRoot { get; private set; }
    public static bool IsSelfTest => SelfTestOutput != null;
    internal static string TestName = "(starting)";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        int st = Array.FindIndex(e.Args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        if (st >= 0)
        {
            SelfTestOutput = st + 1 < e.Args.Length ? IOPath.GetFullPath(e.Args[st + 1]) : IOPath.Combine(IOPath.GetTempPath(), "shutterbox-selftest.txt");
            SelfTestShots = st + 2 < e.Args.Length ? IOPath.GetFullPath(e.Args[st + 2]) : null;
            // Never touch the real settings, library or thumbnails while testing.
            SelfTestRoot = IOPath.Combine(IOPath.GetTempPath(), "ShutterboxSelfTest-" + Guid.NewGuid().ToString("N")[..8]);
            SettingsService.Folder = IOPath.Combine(SelfTestRoot, "AppData");
            SettingsService.CacheFolder = IOPath.Combine(SelfTestRoot, "LocalAppData");
            Directory.CreateDirectory(SettingsService.Folder);
        }

        SettingsService.Load();
        if (IsSelfTest) SettingsService.Current.ImportFolder = IOPath.Combine(SelfTestRoot, "Pictures", "Shutterbox");
        ThemeManager.Apply(SettingsService.Current);
        SettingsService.Changed += () => ThemeManager.Apply(SettingsService.Current);

        Thumbnails.Folder = IOPath.Combine(SettingsService.CacheFolder, "thumbs");
        var lib = new LibraryStore(SettingsService.Folder);
        lib.Load();
        LibraryStore.Current = lib;

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LibraryStore.Current?.Save();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is ResourceReferenceKeyNotFoundException rk)
        {
            if (IsSelfTest)
                try { File.AppendAllText(SelfTestOutput, $"FAIL missing resource '{rk.Key}' during \"{TestName}\"\r\n"); } catch { }
            e.Handled = true;
            return;
        }
        if (IsSelfTest)
        {
            try { File.AppendAllText(SelfTestOutput, $"FAIL unhandled exception during \"{TestName}\": {e.Exception}\r\n"); } catch { }
            e.Handled = true;
            Environment.Exit(99);
            return;
        }
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(IOPath.Combine(SettingsService.Folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\r\n\r\n");
        }
        catch { }
        MessageBox.Show(MainWindow, "Shutterbox hit an unexpected error:\n\n" + e.Exception.Message, "Shutterbox",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
