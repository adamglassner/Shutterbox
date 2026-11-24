using System.IO.Pipes;
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

    private static Mutex _instance;
    private static string PipeName => "Shutterbox-lock-" + Environment.UserName;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // "Lock with Shutterbox" from File Explorer: Shutterbox.exe --lock <file> (one process per file)
        var toLock = new List<string>();
        int li = Array.FindIndex(e.Args, a => a.Equals("--lock", StringComparison.OrdinalIgnoreCase));
        if (li >= 0) toLock.AddRange(e.Args.Skip(li + 1).Where(a => !a.StartsWith("--")));
        bool selfTest = e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        if (!selfTest)
        {
            _instance = new Mutex(true, "Shutterbox-instance-" + Environment.UserName, out bool first);
            if (!first)
            {
                // Shutterbox is already open: hand the files to it (or just bring it forward)
                if (SendToRunning(toLock)) { Shutdown(); return; }
            }
            else StartPipeServer();
        }

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
        if (toLock.Count > 0) win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => win.LockFilesFromExplorer(toLock)));
    }

    /// <summary>Sends files to the Shutterbox that's already running. False when it couldn't be reached.</summary>
    private static bool SendToRunning(List<string> files)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(500);
                using var w = new StreamWriter(pipe);
                w.Write(string.Join("\n", files.Select(f => IOPath.GetFullPath(f))));
                return true;
            }
            catch { Thread.Sleep(300); }
        }
        return false;
    }

    /// <summary>Listens for files from other Shutterbox processes (each Explorer "Lock with Shutterbox" starts one); batches them.</summary>
    private void StartPipeServer()
    {
        var pending = new List<string>();
        DispatcherTimer batch = null;
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync();
                    using var r = new StreamReader(pipe);
                    var text = await r.ReadToEndAsync();
                    var files = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (MainWindow is not Shutterbox.MainWindow win) return;
                        if (files.Count == 0)
                        {
                            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                            win.Activate();
                            return;
                        }
                        lock (pending) pending.AddRange(files);
                        batch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Normal, (_, _) =>
                        {
                            batch.Stop();
                            List<string> all;
                            lock (pending) { all = pending.ToList(); pending.Clear(); }
                            win.LockFilesFromExplorer(all);
                        }, Dispatcher);
                        batch.Stop();
                        batch.Start();
                    });
                }
                catch { await Task.Delay(500); }
            }
        });
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
