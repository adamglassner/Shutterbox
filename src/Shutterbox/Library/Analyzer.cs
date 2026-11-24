using Shutterbox.Services;

namespace Shutterbox.Library;

/// <summary>
/// Looks through the library in the background (newest photos first), finding faces and what's in each photo with
/// <see cref="Vision"/>, and keeps <see cref="PeopleStore"/> up to date. One low-priority thread; it waits while
/// "Find people and things" is off and wakes up when photos are added.
/// </summary>
public static class Analyzer
{
    private static Thread _thread;
    private static readonly AutoResetEvent Wake = new(false);
    private static Dispatcher _ui;

    public static int Done { get; private set; }
    public static int Total { get; private set; }
    public static bool Busy { get; private set; }
    public static string Error { get; private set; }

    /// <summary>Raised on the UI thread every few photos and when a batch is done.</summary>
    public static event Action Progress;

    public static void Start(Dispatcher ui)
    {
        if (_thread != null) return;
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "Analyzer", Priority = ThreadPriority.Lowest };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>New photos or the setting changed: look again now.</summary>
    public static void Poke() => Wake.Set();

    private static bool Enabled => SettingsService.Current.FindPeopleAndThings;

    private static void Raise(bool save)
    {
        if (save) PeopleStore.Current?.Save();
        _ui?.BeginInvoke(DispatcherPriority.Background, new Action(() => { PeopleStore.Current?.RaiseChanged(); Progress?.Invoke(); }));
    }

    public static bool Wanted(Photo p) => !p.IsTrashed && !p.IsVideo && !p.Unreadable && !p.IsLocked;

    private static void Run()
    {
        Thread.Sleep(4000); // let the window and thumbnails go first
        while (true)
        {
            try
            {
                var people = PeopleStore.Current;
                var lib = LibraryStore.Current;
                if (!Enabled || people == null || lib == null) { Busy = false; Raise(false); Wake.WaitOne(TimeSpan.FromMinutes(5)); continue; }
                List<Photo> todo = null;
                _ui.Invoke(() =>
                {
                    var all = lib.Photos.Where(Wanted).ToList();
                    todo = all.Where(p => !people.IsAnalyzed(p)).OrderByDescending(p => p.Taken).Take(400).ToList();
                    Total = all.Count;
                    Done = all.Count - all.Count(p => !people.IsAnalyzed(p));
                    people.Prune(id => lib.Get(id) is Photo p && !p.IsTrashed);
                });
                if (todo.Count == 0) { Busy = false; Raise(true); Wake.WaitOne(TimeSpan.FromMinutes(10)); continue; }
                if (!Vision.EnsureLoaded()) { Error = Vision.LoadError; Busy = false; Raise(false); Wake.WaitOne(TimeSpan.FromHours(1)); continue; }
                Busy = true;
                int n = 0;
                foreach (var p in todo)
                {
                    if (!Enabled) break;
                    try
                    {
                        var img = Thumbnails.Load(p, 960, Vision.AnalyzeLongSide);
                        people.SetResult(p, Vision.Analyze(img));
                    }
                    catch { people.SetNothing(p); }
                    Done = Math.Min(Total, Done + 1);
                    n++;
                    if (n % 8 == 0) Raise(n % 64 == 0);
                }
                Raise(true);
            }
            catch (Exception ex) { Error = ex.Message; Busy = false; Thread.Sleep(30000); }
        }
    }
}
