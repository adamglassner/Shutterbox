using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Shutterbox.Library;
using Shutterbox.Services;
using Shutterbox.Views;

namespace Shutterbox;

public enum LibraryView { Photos, Favorites, Recent, Albums, Album, Folder, Trash, Settings }
public enum TimelineZoom { Years, Months, Days }

public partial class MainWindow : Window
{
    private LibraryStore Lib => LibraryStore.Current;
    private static AppSettings S => SettingsService.Current;

    private LibraryView _view = LibraryView.Photos;
    private string _albumId;
    private string _folder;
    private TimelineZoom _zoom = TimelineZoom.Days;

    // every photo's view model, created on demand and reused across views
    private readonly Dictionary<string, PhotoItem> _items = new();
    // photos in the current view, in the order shown
    private List<PhotoItem> _flat = new();
    private readonly Dictionary<string, int> _indexOf = new();
    private List<TimelineRow> _rows = new();
    private readonly Dictionary<string, PhotoRow> _rowOf = new();
    private int _cols = 1;
    private double _tile = 150;
    private double _builtWidth;

    private readonly HashSet<string> _selected = new();
    private string _focusId;
    private int _anchor = -1;

    private readonly DispatcherTimer _rebuildTimer, _searchTimer, _resizeTimer, _toastTimer, _watchTimer;
    private CancellationTokenSource _previewCts;
    private bool _suppressNav;
    private bool _suppressSearch;
    private List<string> _offline = new();
    private SettingsPage _settingsPage;

    public LibraryView CurrentView => _view;
    public IReadOnlyList<PhotoItem> VisiblePhotos => _flat;
    public int ColumnCount => _cols;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _rebuildTimer = MakeTimer(350, () => RebuildView(true));
        _searchTimer = MakeTimer(220, () => RebuildView(false));
        _resizeTimer = MakeTimer(90, RelayoutIfNeeded);
        _toastTimer = MakeTimer(4500, HideToast);
        _watchTimer = MakeTimer(2500, ScanDirtyFolders);
        _slideTimer = new DispatcherTimer();
        _slideTimer.Tick += (_, _) => SlideshowTick();
        _hideUiTimer = MakeTimer(2600, HideViewerChrome);

        RestoreWindow();
        Lib.Changed += OnLibraryChanged;
        SettingsService.Changed += OnSettingsChanged;
        InfoToggle.IsChecked = S.ShowInfoPanel;
        ShortcutsText.Text =
            "Click, Ctrl+click, Shift+click  select\n" +
            "Enter or double-click  open\n" +
            "Arrow keys  move   ·   Ctrl+A  select all\n" +
            "F  favorite   ·   R  rotate   ·   Delete  Trash\n" +
            "Ctrl+F  search   ·   Ctrl+I  import\n" +
            "F5  slideshow   ·   I  details panel";
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        AddPicturesText.Text = string.IsNullOrEmpty(pics) ? "Add my Pictures folder" : $"Add my Pictures folder ({ShortPath(pics)})";
        Loaded += OnLoaded;
    }

    private static DispatcherTimer MakeTimer(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        return t;
    }

    private static void Restart(DispatcherTimer t) { t.Stop(); t.Start(); }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyInfoPanelVisibility();
        RefreshSidebar();
        _suppressNav = true;
        NavPhotos.IsChecked = true;
        _suppressNav = false;
        ShowView(LibraryView.Photos);
        if (App.IsSelfTest)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = RunSelfTestAsync()));
            return;
        }
        SetupWatchers();
        if (S.ScanOnStart && Lib.Folders.Count > 0) _ = StartScan(null);
    }

    // ================================================================== window state

    private void RestoreWindow()
    {
        Width = S.WinWidth;
        Height = S.WinHeight;
        if (!double.IsNaN(S.WinLeft) && !double.IsNaN(S.WinTop))
        {
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            if (S.WinLeft >= vl - 50 && S.WinTop >= vt - 50 &&
                S.WinLeft < vl + SystemParameters.VirtualScreenWidth - 100 && S.WinTop < vt + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = S.WinLeft;
                Top = S.WinTop;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (S.WinMaximized && !App.IsSelfTest) WindowState = WindowState.Maximized;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsSelfTest) return;
        S.WinMaximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!b.IsEmpty)
        {
            S.WinLeft = b.Left; S.WinTop = b.Top; S.WinWidth = b.Width; S.WinHeight = b.Height;
        }
        SettingsService.Save();
        _scanCts?.Cancel();
        foreach (var w in _watchers) w.Dispose();
        Lib.Save();
    }

    private void OnSettingsChanged()
    {
        if (_view != LibraryView.Settings) Restart(_rebuildTimer);
        SetupWatchers();
    }

    // ================================================================== sidebar

    public void RefreshSidebar()
    {
        int photos = 0, favs = 0, recent = 0, trash = 0;
        long bytes = 0;
        var since = DateTime.Now.AddDays(-30);
        foreach (var p in Lib.Photos)
        {
            if (p.IsTrashed) { trash++; continue; }
            photos++;
            bytes += p.Size;
            if (p.Favorite) favs++;
            if (p.Added >= since) recent++;
        }
        CountPhotos.Text = photos > 0 ? photos.ToString("N0") : "";
        CountFavorites.Text = favs > 0 ? favs.ToString("N0") : "";
        CountRecent.Text = recent > 0 ? recent.ToString("N0") : "";
        CountTrash.Text = trash > 0 ? trash.ToString("N0") : "";
        CountAlbums.Text = Lib.Albums.Count > 0 ? Lib.Albums.Count.ToString("N0") : "";
        if (!_scanning)
            CardText.Text = Lib.Folders.Count == 0 ? "No folders yet" : $"{photos:N0} photos · {Photo.FormatSize(bytes)}";

        // Albums
        AlbumNav.Children.Clear();
        foreach (var a in Lib.Albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = Lib.PhotosIn(a).Count();
            var rb = MakeNav("", a.Name, count > 0 ? count.ToString("N0") : "", "album:" + a.Id);
            rb.AllowDrop = true;
            rb.DragOver += AlbumTarget_DragOver;
            rb.Drop += AlbumNav_Drop;
            rb.MouseRightButtonUp += (s, e) => { ShowAlbumMenu(a, (FrameworkElement)s); e.Handled = true; };
            if (_view == LibraryView.Album && _albumId == a.Id) { _suppressNav = true; rb.IsChecked = true; _suppressNav = false; }
            AlbumNav.Children.Add(rb);
        }
        AlbumNavEmpty.Visibility = Lib.Albums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Folders
        FolderNav.Children.Clear();
        foreach (var f in Lib.Folders)
        {
            var name = IOPath.GetFileName(f.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = f;
            var offline = _offline.Any(o => string.Equals(o, f, StringComparison.OrdinalIgnoreCase));
            var count = Lib.CountIn(f);
            var rb = MakeNav(offline ? "" : "", name, offline ? "offline" : (count > 0 ? count.ToString("N0") : ""), "folder:" + f);
            rb.ToolTip = offline ? f + "\nThis folder isn't available right now (an unplugged drive?). Its photos are kept." : f;
            var folder = f;
            rb.MouseRightButtonUp += (s, e) => { ShowFolderMenu(folder, (FrameworkElement)s); e.Handled = true; };
            if (_view == LibraryView.Folder && string.Equals(_folder, f, StringComparison.OrdinalIgnoreCase)) { _suppressNav = true; rb.IsChecked = true; _suppressNav = false; }
            FolderNav.Children.Add(rb);
        }
    }

    private RadioButton MakeNav(string glyph, string text, string count, string tag)
    {
        var dock = new DockPanel();
        var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("NavIcon") };
        var cnt = new TextBlock { Text = count, Style = (Style)FindResource("NavCount") };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        dock.Children.Add(icon);
        dock.Children.Add(cnt);
        dock.Children.Add(label);
        var rb = new RadioButton { Style = (Style)FindResource("NavItem"), Tag = tag, Content = dock };
        rb.Checked += Nav_Checked;
        return rb;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNav || sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (tag.StartsWith("album:")) ShowView(LibraryView.Album, albumId: tag[6..]);
        else if (tag.StartsWith("folder:")) ShowView(LibraryView.Folder, folder: tag[7..]);
        else ShowView(tag switch
        {
            "favorites" => LibraryView.Favorites,
            "recent" => LibraryView.Recent,
            "albums" => LibraryView.Albums,
            "trash" => LibraryView.Trash,
            "settings" => LibraryView.Settings,
            _ => LibraryView.Photos,
        });
    }

    /// <summary>Checks the sidebar entry for the current view without re-triggering navigation.</summary>
    private void SyncNav()
    {
        _suppressNav = true;
        try
        {
            RadioButton target = _view switch
            {
                LibraryView.Photos => NavPhotos,
                LibraryView.Favorites => NavFavorites,
                LibraryView.Recent => NavRecent,
                LibraryView.Albums => NavAlbums,
                LibraryView.Trash => NavTrash,
                LibraryView.Settings => NavSettings,
                LibraryView.Album => AlbumNav.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == "album:" + _albumId),
                LibraryView.Folder => FolderNav.Children.OfType<RadioButton>().FirstOrDefault(r => string.Equals((string)r.Tag, "folder:" + _folder, StringComparison.OrdinalIgnoreCase)),
                _ => null,
            };
            if (target != null) target.IsChecked = true;
            else
            {
                foreach (var r in new[] { NavPhotos, NavFavorites, NavRecent, NavAlbums, NavTrash, NavSettings }) r.IsChecked = false;
                foreach (var r in AlbumNav.Children.OfType<RadioButton>().Concat(FolderNav.Children.OfType<RadioButton>())) r.IsChecked = false;
            }
        }
        finally { _suppressNav = false; }
    }

    // ================================================================== views

    public void ShowView(LibraryView view, string albumId = null, string folder = null)
    {
        if (view == LibraryView.Album && Lib.GetAlbum(albumId) == null) view = LibraryView.Albums;
        _view = view;
        _albumId = view == LibraryView.Album ? albumId : null;
        _folder = view == LibraryView.Folder ? folder : null;
        SyncNav();
        ClearSelection();
        _suppressSearch = true;
        SearchBox.Text = "";
        _suppressSearch = false;

        bool timeline = view is not (LibraryView.Albums or LibraryView.Settings);
        ZoomBox.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
        SlideshowBtn.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = view == LibraryView.Settings ? Visibility.Hidden : Visibility.Visible;
        AlbumsPage.Visibility = view == LibraryView.Albums ? Visibility.Visible : Visibility.Collapsed;
        if (view == LibraryView.Settings)
        {
            _settingsPage ??= new SettingsPage(this);
            _settingsPage.Refresh();
            SettingsHost.Content = _settingsPage;
            SettingsHost.Visibility = Visibility.Visible;
        }
        else SettingsHost.Visibility = Visibility.Collapsed;
        ApplyInfoPanelVisibility();
        RebuildView(false);
    }

    public void RebuildView(bool keepScroll)
    {
        _rebuildTimer.Stop();
        _searchTimer.Stop();
        if (_view == LibraryView.Settings)
        {
            Timeline.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            Banner.Visibility = Visibility.Collapsed;
            _flat = new();
            _indexOf.Clear();
            UpdateHeader();
            UpdateSelectionUi();
            return;
        }
        if (_view == LibraryView.Albums)
        {
            Timeline.Visibility = Visibility.Collapsed;
            _flat = new();
            _indexOf.Clear();
            BuildAlbumGrid();
            UpdateHeader();
            UpdateBanner();
            UpdateEmptyState();
            UpdateSelectionUi();
            return;
        }

        IEnumerable<Photo> photos = PhotosForView();
        var q = SearchBox.Text.Trim();
        if (q.Length > 0) photos = Filter(photos, q);
        _flat = photos.Select(ItemFor).ToList();
        _indexOf.Clear();
        for (int i = 0; i < _flat.Count; i++) _indexOf[_flat[i].Photo.Id] = i;

        // forget selected photos that are no longer shown
        foreach (var id in _selected.Where(id => !_indexOf.ContainsKey(id)).ToList())
        {
            _selected.Remove(id);
            if (_items.TryGetValue(id, out var it)) it.IsSelected = false;
        }
        if (_focusId != null && !_indexOf.ContainsKey(_focusId)) _focusId = null;

        BuildRows(keepScroll);
        Timeline.Visibility = Visibility.Visible;
        UpdateHeader();
        UpdateBanner();
        UpdateEmptyState();
        UpdateSelectionUi();
        UpdateInfo();
    }

    private IEnumerable<Photo> PhotosForView()
    {
        var all = Lib.Photos;
        switch (_view)
        {
            case LibraryView.Favorites:
                return all.Where(p => !p.IsTrashed && p.Favorite).OrderByDescending(p => p.Taken);
            case LibraryView.Recent:
                var since = DateTime.Now.AddDays(-30);
                return all.Where(p => !p.IsTrashed && p.Added >= since).OrderByDescending(p => p.Added.Date).ThenByDescending(p => p.Taken);
            case LibraryView.Album:
                var a = Lib.GetAlbum(_albumId);
                return a == null ? Enumerable.Empty<Photo>() : Lib.PhotosIn(a).OrderByDescending(p => p.Taken);
            case LibraryView.Folder:
                return all.Where(p => !p.IsTrashed && LibraryStore.IsUnder(p.Path, _folder)).OrderByDescending(p => p.Taken);
            case LibraryView.Trash:
                return all.Where(p => p.IsTrashed).OrderByDescending(p => p.Trashed).ThenByDescending(p => p.Taken);
            default:
                return all.Where(p => !p.IsTrashed).OrderByDescending(p => p.Taken);
        }
    }

    private IEnumerable<Photo> Filter(IEnumerable<Photo> photos, string query)
    {
        var tokens = query.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var albumNames = new Dictionary<string, List<string>>();
        foreach (var a in Lib.Albums)
            foreach (var id in a.PhotoIds)
            {
                if (!albumNames.TryGetValue(id, out var l)) albumNames[id] = l = new List<string>();
                l.Add(a.Name);
            }
        return photos.Where(p =>
        {
            var text = ItemFor(p).SearchText(albumNames.TryGetValue(p.Id, out var names) ? names : null);
            return tokens.All(t => text.Contains(t));
        });
    }

    public PhotoItem ItemFor(Photo p)
    {
        if (!_items.TryGetValue(p.Id, out var it)) _items[p.Id] = it = new PhotoItem(p);
        return it;
    }

    // ------------------------------------------------------------------ timeline rows

    private double TimelineWidth()
    {
        double w = Timeline.ActualWidth;
        if (w < 50) w = Math.Max(400, LayoutWidth - 252 - (InfoShown ? 330 : 0));
        return Math.Max(120, w - Timeline.Padding.Left - Timeline.Padding.Right - 14);
    }

    private double BaseTile()
    {
        double t = S.Density switch { TileDensity.Compact => 112, TileDensity.Large => 214, _ => 156 };
        return t * (_zoom switch { TimelineZoom.Years => 0.5, TimelineZoom.Months => 0.72, _ => 1.0 });
    }

    private void BuildRows(bool keepScroll)
    {
        string anchorId = keepScroll ? FirstVisiblePhotoId() : null;
        const double gap = 4;
        double avail = TimelineWidth();
        _builtWidth = avail;
        double baseTile = BaseTile();
        _cols = Math.Max(1, (int)Math.Floor((avail + gap) / (baseTile + gap)));
        _tile = Math.Max(40, Math.Floor((avail + gap) / _cols - gap));

        var rows = new List<TimelineRow>();
        _rowOf.Clear();
        var today = DateTime.Today;
        bool first = true;
        foreach (var g in Group(_flat))
        {
            var items = g.items;
            rows.Add(new HeaderRow
            {
                Title = g.title,
                Subtitle = g.subtitle,
                CountText = items.Count == 1 ? "1 photo" : $"{items.Count:N0} photos",
                Items = items,
                Height = first ? 46 : 64,
            });
            first = false;
            for (int i = 0; i < items.Count; i += _cols)
            {
                var row = new PhotoRow { Items = items.Skip(i).Take(_cols).ToList(), Height = _tile + gap };
                foreach (var it in row.Items)
                {
                    it.TileSize = _tile;
                    _rowOf[it.Photo.Id] = row;
                }
                rows.Add(row);
            }
        }
        _rows = rows;
        Timeline.ItemsSource = rows;
        var sv = TimelineScroller();
        sv?.ScrollToTop();
        if (anchorId != null && _rowOf.TryGetValue(anchorId, out var anchorRow) && rows.IndexOf(anchorRow) > 2)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!ReferenceEquals(Timeline.ItemsSource, rows)) return;
                ScrollRowToTop(anchorRow);
            }));
        }
    }

    private List<(string title, string subtitle, List<PhotoItem> items)> Group(List<PhotoItem> list)
    {
        var groups = new List<(string, string, List<PhotoItem>)>();
        Func<Photo, DateTime> key;
        Func<DateTime, string> title;
        var today = DateTime.Today;
        string Day(DateTime d) =>
            d == today ? "Today" : d == today.AddDays(-1) ? "Yesterday" :
            d.Year == today.Year ? d.ToString("dddd, MMM d", CultureInfo.CurrentCulture) : d.ToString("dddd, MMM d, yyyy", CultureInfo.CurrentCulture);

        if (_view == LibraryView.Recent)
        {
            key = p => p.Added.Date;
            title = d => d == today ? "Added today" : d == today.AddDays(-1) ? "Added yesterday" : "Added " + d.ToString("dddd, MMM d", CultureInfo.CurrentCulture);
        }
        else if (_view == LibraryView.Trash)
        {
            key = p => (p.Trashed ?? DateTime.Today).Date;
            title = d => d == today ? "Moved to Trash today" : "Moved to Trash " + d.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }
        else if (_zoom == TimelineZoom.Years)
        {
            key = p => new DateTime(p.Taken.Year, 1, 1);
            title = d => d.Year.ToString(CultureInfo.CurrentCulture);
        }
        else if (_zoom == TimelineZoom.Months)
        {
            key = p => new DateTime(p.Taken.Year, p.Taken.Month, 1);
            title = d => d.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        }
        else
        {
            key = p => p.Taken.Date;
            title = Day;
        }

        var rootNames = new HashSet<string>(Lib.Folders.Select(f => IOPath.GetFileName(f.TrimEnd('\\'))), StringComparer.OrdinalIgnoreCase);
        DateTime? current = null;
        List<PhotoItem> bucket = null;
        void Close()
        {
            if (bucket == null || bucket.Count == 0) return;
            string sub = "";
            if (_zoom == TimelineZoom.Days || _view is LibraryView.Recent)
            {
                var top = bucket.GroupBy(i => i.Photo.FolderName, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(gr => gr.Count()).First();
                if (!rootNames.Contains(top.Key) && top.Count() * 2 >= bucket.Count && !LooksLikeDateFolder(top.Key)) sub = top.Key;
            }
            groups.Add((title(current.Value), sub, bucket));
        }
        foreach (var it in list)
        {
            var k = key(it.Photo);
            if (current != k)
            {
                Close();
                current = k;
                bucket = new List<PhotoItem>();
            }
            bucket.Add(it);
        }
        Close();
        return groups;
    }

    private static bool LooksLikeDateFolder(string name) =>
        !string.IsNullOrEmpty(name) && (name.All(c => char.IsDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.') ||
                                        name.Equals("DCIM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("100", StringComparison.Ordinal));

    private ScrollViewer TimelineScroller() => FindChild<ScrollViewer>(Timeline);

    private VirtualizingStackPanel TimelinePanel() => FindChild<VirtualizingStackPanel>(Timeline);

    private string FirstVisiblePhotoId()
    {
        var panel = TimelinePanel();
        if (panel == null) return null;
        PhotoRow best = null;
        double bestY = double.MaxValue;
        foreach (var child in panel.Children)
        {
            if (child is not ListBoxItem li || li.DataContext is not PhotoRow pr || !li.IsVisible) continue;
            var y = li.TranslatePoint(new Point(0, 0), Timeline).Y;
            if (y + li.ActualHeight > 12 && y < bestY) { bestY = y; best = pr; }
        }
        return best?.Items.FirstOrDefault()?.Photo.Id;
    }

    /// <summary>Scrolls so the row is at the top of the timeline (coming from below lands it at the top edge).</summary>
    private void ScrollRowToTop(TimelineRow row)
    {
        var sv = TimelineScroller();
        if (sv == null) return;
        int idx = _rows.IndexOf(row);
        if (idx > 0 && _rows[idx - 1] is HeaderRow) row = _rows[idx - 1];
        sv.ScrollToEnd();
        Timeline.UpdateLayout();
        Timeline.ScrollIntoView(row);
    }

    private void ScrollToPhoto(string id)
    {
        if (id != null && _rowOf.TryGetValue(id, out var row)) Timeline.ScrollIntoView(row);
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Restart(_resizeTimer);
    }

    private void RelayoutIfNeeded()
    {
        if (Timeline.Visibility != Visibility.Visible) return;
        var avail = TimelineWidth();
        if (Math.Abs(avail - _builtWidth) < 2) return;
        const double gap = 4;
        int cols = Math.Max(1, (int)Math.Floor((avail + gap) / (BaseTile() + gap)));
        double tile = Math.Max(40, Math.Floor((avail + gap) / cols - gap));
        if (cols == _cols && Math.Abs(tile - _tile) < 1) { _builtWidth = avail; return; }
        BuildRows(true);
    }

    public static T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    // ------------------------------------------------------------------ header, banner, empty state

    private void UpdateHeader()
    {
        string title, sub;
        int n = _flat.Count;
        string photos(int c) => c == 1 ? "1 photo" : $"{c:N0} photos";
        switch (_view)
        {
            case LibraryView.Favorites: title = "Favorites"; sub = photos(n); break;
            case LibraryView.Recent: title = "Recently added"; sub = photos(n) + " in the last 30 days"; break;
            case LibraryView.Albums: title = "Albums"; sub = Lib.Albums.Count == 1 ? "1 album" : $"{Lib.Albums.Count} albums"; break;
            case LibraryView.Album:
                var a = Lib.GetAlbum(_albumId);
                title = a?.Name ?? "Album";
                sub = photos(n) + (a != null ? " · made " + a.Created.ToString("MMM d, yyyy", CultureInfo.CurrentCulture) : "");
                break;
            case LibraryView.Folder:
                title = IOPath.GetFileName(_folder?.TrimEnd('\\') ?? "") is { Length: > 0 } fn ? fn : _folder;
                sub = photos(n);
                break;
            case LibraryView.Trash: title = "Trash"; sub = photos(n); break;
            case LibraryView.Settings: title = "Settings"; sub = "Shutterbox " + AppInfo.Version; break;
            default: title = "Photos"; sub = photos(n); break;
        }
        var q = SearchBox.Text.Trim();
        if (q.Length > 0 && _view is not (LibraryView.Albums or LibraryView.Settings)) sub = $"{photos(n)} match “{q}”";
        if (Lib.Folders.Count == 0 && _view is LibraryView.Photos or LibraryView.Recent or LibraryView.Favorites) sub = "";
        TitleText.Text = title;
        SubtitleText.Text = sub;
        Title = _view == LibraryView.Photos ? "Shutterbox" : $"{title} - Shutterbox";
    }

    private void UpdateBanner()
    {
        BannerActions.Children.Clear();
        string icon = null, text = null;
        void Act(string label, Action a, bool accent = false)
        {
            var b = new Button { Content = label, Style = (Style)FindResource(accent ? "ToolAccentButton" : "ToolButton"), Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            b.Click += (_, _) => a();
            BannerActions.Children.Add(b);
        }
        switch (_view)
        {
            case LibraryView.Trash when _flat.Count > 0:
                icon = "";
                text = "Photos in Trash are hidden everywhere else. Emptying the Trash moves the files to the Windows Recycle Bin, where you can still get them back.";
                Act("Restore all", () => RestorePhotos(_flat.Select(i => i.Photo).ToList()));
                Act("Empty Trash", () => EmptyTrash(null), true);
                break;
            case LibraryView.Album:
                var a = Lib.GetAlbum(_albumId);
                if (a == null) break;
                icon = "";
                text = "Albums only point at your photos, so a photo can be in several albums. Drag photos onto an album in the sidebar to add them.";
                if (_flat.Count > 0) Act("Slideshow", () => StartSlideshow());
                Act("Rename", () => RenameAlbum(a));
                Act("Delete album", () => DeleteAlbum(a));
                break;
            case LibraryView.Folder:
                icon = "";
                text = _folder;
                Act("Show in File Explorer", () => Shell.ShowInExplorer(_folder));
                Act("Rescan", () => _ = StartScan(new[] { _folder }));
                break;
            case LibraryView.Photos when _offline.Count > 0:
                icon = "";
                text = $"{(_offline.Count == 1 ? "A folder isn't" : "Some folders aren't")} available right now ({string.Join(", ", _offline.Select(ShortPath))}). " +
                       "Their photos are kept and come back when the drive is connected.";
                Act("Check again", () => _ = StartScan(null));
                break;
        }
        Banner.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        BannerIcon.Text = icon ?? "";
        BannerText.Text = text ?? "";
    }

    private void UpdateEmptyState()
    {
        bool noFolders = Lib.Folders.Count == 0;
        string title = null, text = null;
        bool buttons = false;
        var q = SearchBox.Text.Trim();
        if (_view == LibraryView.Settings) { }
        else if (noFolders && _view is LibraryView.Photos or LibraryView.Recent or LibraryView.Favorites)
        {
            title = "Let's find your photos";
            text = "Shutterbox organizes the pictures already on your PC. Choose the folders it should look in. Your files stay where they are.";
            buttons = true;
        }
        else if (_view == LibraryView.Albums)
        {
            // the album grid always has the "New album" card
        }
        else if (_flat.Count == 0)
        {
            if (q.Length > 0) { title = "No matches"; text = $"No photos match “{q}”. Search looks at names, folders, tags, captions, cameras, albums, months and years."; }
            else switch (_view)
            {
                case LibraryView.Favorites: title = "No favorites yet"; text = "Press F or click the heart on a photo to keep your best shots here."; break;
                case LibraryView.Recent: title = "Nothing new"; text = "Photos added to your library in the last 30 days show up here."; break;
                case LibraryView.Album: title = "This album is empty"; text = "Select photos and choose Add to album, or drag them onto the album in the sidebar."; break;
                case LibraryView.Trash: title = "Trash is empty"; text = "Photos you delete wait here until you empty the Trash."; break;
                case LibraryView.Folder: title = "No photos here"; text = _scanning ? "Still looking…" : "This folder doesn't have any pictures Shutterbox can show."; break;
                default:
                    title = _scanning ? "Looking for photos…" : "No photos found yet";
                    text = _scanning ? "Your photos appear here as they're found." : "Shutterbox didn't find pictures in your folders. Add another folder or import from a camera or phone.";
                    buttons = !_scanning;
                    break;
            }
        }
        EmptyState.Visibility = title != null ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = title ?? "";
        EmptyText.Text = text ?? "";
        EmptyButtons.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        AddPicturesBtn.Visibility = noFolders ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)) return "~" + path[home.Length..];
        return path;
    }

    // ------------------------------------------------------------------ albums page

    private void BuildAlbumGrid()
    {
        var cards = new List<AlbumCard>();
        foreach (var a in Lib.Albums.OrderByDescending(a => a.Created))
        {
            var cover = Lib.CoverOf(a);
            var n = Lib.PhotosIn(a).Count();
            cards.Add(new AlbumCard { Album = a, Cover = cover != null ? ItemFor(cover) : null, CountText = n == 1 ? "1 photo" : $"{n:N0} photos" });
        }
        cards.Add(new AlbumCard { Album = null, CountText = "Make an album" });
        AlbumGrid.ItemsSource = cards;
    }

    private void AlbumCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlbumCard card) return;
        if (card.IsNew) NewAlbum(null);
        else ShowView(LibraryView.Album, albumId: card.Album.Id);
    }

    private void AlbumCard_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumCard { IsNew: false } card)
        {
            ShowAlbumMenu(card.Album, (FrameworkElement)sender);
            e.Handled = true;
        }
    }

    private void AlbumCard_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlbumCard card) return;
        var photos = DraggedPhotos(e);
        if (photos.Count == 0) return;
        var album = card.Album ?? NewAlbum(null, photos);
        if (album != null && card.Album != null) AddToAlbum(album, photos);
        e.Handled = true;
    }

    // ================================================================== selection

    public List<Photo> SelectedPhotos => _flat.Where(i => _selected.Contains(i.Photo.Id)).Select(i => i.Photo).ToList();

    private void SetSelected(PhotoItem it, bool on)
    {
        if (on) _selected.Add(it.Photo.Id); else _selected.Remove(it.Photo.Id);
        it.IsSelected = on;
    }

    private void ClearSelection()
    {
        foreach (var id in _selected.ToList())
            if (_items.TryGetValue(id, out var it)) it.IsSelected = false;
        _selected.Clear();
        _anchor = -1;
        _focusId = null;
    }

    public void SelectOnly(PhotoItem it)
    {
        ClearSelection();
        SetSelected(it, true);
        _focusId = it.Photo.Id;
        _anchor = _indexOf.TryGetValue(it.Photo.Id, out var i) ? i : -1;
        SelectionChanged();
    }

    public void SelectAll()
    {
        foreach (var it in _flat) SetSelected(it, true);
        SelectionChanged();
    }

    private void SelectRange(int from, int to, bool add)
    {
        if (!add)
        {
            foreach (var id in _selected.ToList())
                if (_items.TryGetValue(id, out var x)) x.IsSelected = false;
            _selected.Clear();
        }
        int a = Math.Min(from, to), b = Math.Max(from, to);
        for (int i = Math.Max(0, a); i <= b && i < _flat.Count; i++) SetSelected(_flat[i], true);
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        UpdateSelectionUi();
        UpdateInfo();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        SelectionChanged();
    }

    private void GroupSelect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HeaderRow h) return;
        bool all = h.Items.All(i => i.IsSelected);
        foreach (var it in h.Items) SetSelected(it, !all);
        SelectionChanged();
    }

    private void UpdateSelectionUi()
    {
        int n = _selected.Count;
        SelectionBar.Visibility = n > 1 || (n == 1 && !InfoShown) ? Visibility.Visible : Visibility.Collapsed;
        if (n == 0) return;
        SelectionText.Text = n == 1 ? "1 selected" : $"{n:N0} selected";
        SelectionActions.Children.Clear();
        void Act(string glyph, string label, Action a, string tip = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
            sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)FindResource("BarButton"), Content = sp, ToolTip = tip };
            b.Click += (_, _) => a();
            SelectionActions.Children.Add(b);
        }
        var sel = SelectedPhotos;
        if (_view == LibraryView.Trash)
        {
            Act("", "Restore", () => RestorePhotos(SelectedPhotos));
            Act("", "Delete from PC", () => EmptyTrash(SelectedPhotos), "Moves the files to the Windows Recycle Bin");
            return;
        }
        bool allFav = sel.All(p => p.Favorite);
        Act(allFav ? "" : "", allFav ? "Unfavorite" : "Favorite", () => ToggleFavorite(SelectedPhotos), "F");
        Act("", "Add to album", () => ShowAddToAlbumMenu(SelectionActions.Children[1] as FrameworkElement, SelectedPhotos));
        if (_view == LibraryView.Album && Lib.GetAlbum(_albumId) is Album al)
            Act("", "Remove from album", () => RemoveFromAlbum(al, SelectedPhotos));
        Act("", "Rotate", () => RotatePhotos(SelectedPhotos, 90), "R");
        Act("", "Copy", () => CopyPhotos(SelectedPhotos), "Ctrl+C");
        Act("", "Slideshow", () => StartSlideshow(SelectedPhotos));
        Act("", "Move to Trash", () => TrashPhotos(SelectedPhotos), "Delete");
    }

    // ------------------------------------------------------------------ mouse on tiles

    private Point _downPos;
    private PhotoItem _downItem;
    private bool _deferSelect;

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItem it) return;
        e.Handled = true;
        Keyboard.Focus(this);
        if (e.ClickCount >= 2)
        {
            SelectOnly(it);
            OpenViewer(it.Photo);
            return;
        }
        var mods = Keyboard.Modifiers;
        int idx = _indexOf.TryGetValue(it.Photo.Id, out var i) ? i : -1;
        if (mods.HasFlag(ModifierKeys.Shift) && _anchor >= 0 && idx >= 0)
        {
            SelectRange(_anchor, idx, mods.HasFlag(ModifierKeys.Control));
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            SetSelected(it, !it.IsSelected);
            _anchor = idx;
            SelectionChanged();
        }
        else if (it.IsSelected && _selected.Count > 1)
        {
            _deferSelect = true; // might be the start of dragging the whole selection
        }
        else SelectOnly(it);
        _focusId = it.Photo.Id;
        _downPos = e.GetPosition(this);
        _downItem = it;
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItem it && _deferSelect && _downItem == it) SelectOnly(it);
        _deferSelect = false;
        _downItem = null;
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_downItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _downPos.X) < 6 && Math.Abs(p.Y - _downPos.Y) < 6) return;
        var photos = _downItem.IsSelected ? SelectedPhotos : new List<Photo> { _downItem.Photo };
        _downItem = null;
        _deferSelect = false;
        if (photos.Count == 0) return;
        var data = new DataObject();
        data.SetData("ShutterboxPhotoIds", photos.Select(x => x.Id).ToArray());
        var paths = photos.Where(x => File.Exists(x.Path)).Select(x => x.Path).ToArray();
        if (paths.Length > 0) data.SetData(DataFormats.FileDrop, paths);
        try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Link); } catch { }
    }

    private void Tile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItem it) return;
        e.Handled = true;
        if (!it.IsSelected) SelectOnly(it);
        _focusId = it.Photo.Id;
        ShowPhotoMenu(SelectedPhotos, (FrameworkElement)sender);
    }

    private void Timeline_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A click on empty space (not a photo, a button or the scroll bar) clears the selection
        var src = e.OriginalSource as DependencyObject;
        for (var d = src; d != null && d != Timeline; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement fe && (fe.Tag as string == "tile" || fe is ButtonBase || fe is ScrollBar || fe is Thumb)) return;
        }
        if (_selected.Count > 0)
        {
            ClearSelection();
            SelectionChanged();
        }
        Keyboard.Focus(this);
    }

    // ================================================================== keyboard

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewerLayer.Visibility == Visibility.Visible) { ViewerKey(e); return; }
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool inText = Keyboard.FocusedElement is TextBox;

        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.I) { Import_Click(null, null); e.Handled = true; return; }
        if (e.Key == Key.F5) { StartSlideshow(); e.Handled = true; return; }
        if (inText)
        {
            if (e.Key == Key.Escape)
            {
                if (Keyboard.FocusedElement == SearchBox && SearchBox.Text.Length > 0) SearchBox.Text = "";
                else Keyboard.Focus(this);
                e.Handled = true;
            }
            return;
        }
        if (_view is LibraryView.Settings or LibraryView.Albums) return;

        switch (e.Key)
        {
            case Key.A when ctrl: SelectAll(); e.Handled = true; return;
            case Key.C when ctrl: CopyPhotos(SelectedPhotos); e.Handled = true; return;
            case Key.R when ctrl: _ = StartScan(null); e.Handled = true; return;
            case Key.Escape:
                if (_selected.Count > 0) { ClearSelection(); SelectionChanged(); }
                else if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                e.Handled = true; return;
            case Key.Enter:
                if (FocusItem() is PhotoItem f) OpenViewer(f.Photo);
                e.Handled = true; return;
            case Key.Delete:
                if (_selected.Count > 0)
                {
                    if (_view == LibraryView.Trash) EmptyTrash(SelectedPhotos);
                    else TrashPhotos(SelectedPhotos);
                }
                e.Handled = true; return;
            case Key.F when !ctrl:
                if (_selected.Count > 0) ToggleFavorite(SelectedPhotos);
                e.Handled = true; return;
            case Key.R when !ctrl:
                if (_selected.Count > 0) RotatePhotos(SelectedPhotos, mods.HasFlag(ModifierKeys.Shift) ? -90 : 90);
                e.Handled = true; return;
            case Key.I when !ctrl:
                InfoToggle.IsChecked = !(InfoToggle.IsChecked == true);
                InfoToggle_Click(null, null);
                e.Handled = true; return;
            case Key.Left: MoveFocus(-1, mods); e.Handled = true; return;
            case Key.Right: MoveFocus(1, mods); e.Handled = true; return;
            case Key.Up: MoveFocus(-_cols, mods); e.Handled = true; return;
            case Key.Down: MoveFocus(_cols, mods); e.Handled = true; return;
            case Key.Home: MoveTo(0, mods); e.Handled = true; return;
            case Key.End: MoveTo(_flat.Count - 1, mods); e.Handled = true; return;
            case Key.PageDown: MoveFocus(_cols * Math.Max(1, (int)(Timeline.ActualHeight / (_tile + 4)) - 1), mods); e.Handled = true; return;
            case Key.PageUp: MoveFocus(-_cols * Math.Max(1, (int)(Timeline.ActualHeight / (_tile + 4)) - 1), mods); e.Handled = true; return;
        }
    }

    private PhotoItem FocusItem()
    {
        if (_focusId != null && _indexOf.TryGetValue(_focusId, out var i)) return _flat[i];
        return _flat.FirstOrDefault(x => x.IsSelected);
    }

    private void MoveFocus(int delta, ModifierKeys mods)
    {
        if (_flat.Count == 0) return;
        int cur = _focusId != null && _indexOf.TryGetValue(_focusId, out var i) ? i : -1;
        int next = cur < 0 ? 0 : Math.Clamp(cur + delta, 0, _flat.Count - 1);
        MoveTo(next, mods);
    }

    private void MoveTo(int index, ModifierKeys mods)
    {
        if (_flat.Count == 0) return;
        index = Math.Clamp(index, 0, _flat.Count - 1);
        var it = _flat[index];
        if (mods.HasFlag(ModifierKeys.Shift))
        {
            if (_anchor < 0) _anchor = index;
            SelectRange(_anchor, index, false);
            _focusId = it.Photo.Id;
        }
        else SelectOnly(it);
        ScrollToPhoto(it.Photo.Id);
    }

    // ================================================================== search & zoom

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch || !IsLoaded) return;
        if (_view == LibraryView.Albums && SearchBox.Text.Trim().Length > 0)
        {
            // searching from the album grid searches all photos
            _suppressSearch = true;
            var text = SearchBox.Text;
            ShowView(LibraryView.Photos);
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
            _suppressSearch = false;
        }
        Restart(_searchTimer);
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RebuildView(false); e.Handled = true; }
        else if (e.Key == Key.Down && _flat.Count > 0) { Keyboard.Focus(this); MoveTo(0, ModifierKeys.None); e.Handled = true; }
    }

    private void Zoom_Checked(object sender, RoutedEventArgs e)
    {
        var z = sender == ZoomYears ? TimelineZoom.Years : sender == ZoomMonths ? TimelineZoom.Months : TimelineZoom.Days;
        if (z == _zoom) return;
        _zoom = z;
        if (IsLoaded) RebuildView(true);
    }

    public void SetZoom(TimelineZoom z)
    {
        (z == TimelineZoom.Years ? ZoomYears : z == TimelineZoom.Months ? ZoomMonths : ZoomDays).IsChecked = true;
    }

    // ================================================================== info panel

    /// <summary>Width the window's content is laid out at (the self-test lays it out bigger than the build machine's screen).</summary>
    private double LayoutWidth => RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : ActualWidth;

    /// <summary>The details panel hides itself on narrow windows so the timeline keeps enough room.</summary>
    private bool InfoShown => S.ShowInfoPanel && _view is not (LibraryView.Settings or LibraryView.Albums) && LayoutWidth >= 1180;

    private void InfoToggle_Click(object sender, RoutedEventArgs e)
    {
        S.ShowInfoPanel = InfoToggle.IsChecked == true;
        SettingsService.Save();
        ApplyInfoPanelVisibility();
        UpdateSelectionUi();
        UpdateInfo();
    }

    private void ApplyInfoPanelVisibility()
    {
        bool show = InfoShown;
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InfoColumn.Width = new GridLength(show ? 330 : 0);
        InfoToggle.Visibility = _view is LibraryView.Settings or LibraryView.Albums ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!IsLoaded) return;
        bool was = InfoPanel.Visibility == Visibility.Visible;
        if (was != InfoShown) { ApplyInfoPanelVisibility(); UpdateSelectionUi(); UpdateInfo(); }
    }

    private Photo _infoPhoto;

    public void UpdateInfo()
    {
        if (!InfoShown) return;
        var sel = _selected.Count;
        InfoNone.Visibility = sel == 0 ? Visibility.Visible : Visibility.Collapsed;
        InfoSingle.Visibility = sel == 1 ? Visibility.Visible : Visibility.Collapsed;
        InfoMulti.Visibility = sel > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (sel == 0)
        {
            _infoPhoto = null;
            var count = Lib.Photos.Count(p => !p.IsTrashed);
            NoneText.Text = count == 0
                ? "Your photos' details appear here: when and where each was taken, the camera settings, tags and albums."
                : "Click a photo to see when and where it was taken, the camera settings, tags and albums. Double-click to open it.";
            return;
        }
        if (sel == 1) ShowSingleInfo(SelectedPhotos.FirstOrDefault() ?? Lib.Get(_selected.First()));
        else ShowMultiInfo(SelectedPhotos);
    }

    private void ShowSingleInfo(Photo p)
    {
        if (p == null) return;
        bool samePhoto = _infoPhoto == p;
        _infoPhoto = p;
        InfoName.Text = p.FileName;
        var date = p.Taken.ToString("dddd, MMMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture);
        InfoDate.Text = p.TakenSource switch
        {
            "file" => date + "  (file date)",
            _ => date,
        };
        InfoDate.ToolTip = p.TakenSource switch
        {
            "exif" => "Date taken, from the photo",
            "name" => "Date taken, from the file name",
            _ => "This photo doesn't say when it was taken, so the file's date is used",
        };

        // fields
        InfoFields.Children.Clear();
        InfoFields.RowDefinitions.Clear();
        InfoFields.ColumnDefinitions.Clear();
        InfoFields.ColumnDefinitions.Add(new ColumnDefinition());
        InfoFields.ColumnDefinitions.Add(new ColumnDefinition());
        int cell = 0;
        void Field(string name, object value, bool wide = false, string tip = null)
        {
            if (value == null || value is string s && string.IsNullOrWhiteSpace(s)) return;
            if (wide && cell % 2 == 1) cell++;
            int row = cell / 2, col = cell % 2;
            while (InfoFields.RowDefinitions.Count <= row) InfoFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 12), ToolTip = tip };
            sp.Children.Add(new TextBlock { Text = name.ToUpperInvariant(), Style = (Style)FindResource("FieldName") });
            if (value is UIElement el) sp.Children.Add(el);
            else sp.Children.Add(new TextBlock { Text = value.ToString(), Style = (Style)FindResource("FieldValue") });
            Grid.SetRow(sp, row);
            Grid.SetColumn(sp, col);
            if (wide) Grid.SetColumnSpan(sp, 2);
            InfoFields.Children.Add(sp);
            cell += wide ? 2 : 1;
        }
        Field("Camera", p.CameraText);
        Field("Lens", p.Lens);
        Field("Exposure", p.ExposureText);
        Field("Focal length", p.FocalLength is double fl ? $"{fl:0.#} mm" : null);
        Field("Size", p.Width > 0 ? $"{p.DisplayWidth:N0} × {p.DisplayHeight:N0} · {Photo.FormatSize(p.Size)}" : Photo.FormatSize(p.Size), true);
        if (p.HasLocation)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            sp.Children.Add(new TextBlock { Text = p.LocationText, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var map = new Button { Style = (Style)FindResource("LinkButton"), Content = "Map", Margin = new Thickness(10, 0, 0, 0), ToolTip = "Open this place on OpenStreetMap" };
            map.Click += (_, _) => Shell.OpenMap(p.Latitude.Value, p.Longitude.Value);
            sp.Children.Add(map);
            Field("Location", sp, true);
        }
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            sp.Children.Add(new TextBlock { Text = ShortPath(p.FolderPath), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210, VerticalAlignment = VerticalAlignment.Center });
            var show = new Button { Style = (Style)FindResource("LinkButton"), Content = "Show", Margin = new Thickness(10, 0, 0, 0), ToolTip = "Show in File Explorer" };
            show.Click += (_, _) => Shell.ShowInExplorer(p.Path);
            sp.Children.Add(show);
            Field("Folder", sp, true, p.Path);
        }
        if (p.Unreadable)
            Field("Note", IOPath.GetExtension(p.Path).ToLowerInvariant() is ".heic" or ".heif"
                ? "Windows can't show this HEIC photo yet. Install “HEIF Image Extensions” from the Microsoft Store."
                : "Windows can't read this picture. It may be damaged or need a codec from the Microsoft Store.", true);

        if (!CaptionBox.IsKeyboardFocused || !samePhoto) CaptionBox.Text = p.Caption ?? "";
        BuildTagChips(p);
        var albums = Lib.AlbumsOf(p).ToList();
        InfoAlbumsLabel.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InfoAlbums.Children.Clear();
        foreach (var a in albums)
        {
            var b = new Button { Style = (Style)FindResource("LinkButton"), Content = a.Name, Margin = new Thickness(0, 0, 14, 6) };
            var album = a;
            b.Click += (_, _) => ShowView(LibraryView.Album, albumId: album.Id);
            InfoAlbums.Children.Add(b);
        }
        InfoFavIcon.Text = p.Favorite ? "" : "";
        InfoFavIcon.Foreground = p.Favorite ? (Brush)FindResource("Accent") : (Brush)FindResource("Text");

        // preview: thumbnail right away, sharper version a moment later
        if (!samePhoto || PreviewImage.Source == null)
        {
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var ct = _previewCts.Token;
            PreviewImage.Source = Thumbnails.Get(p, img => { if (_infoPhoto == p && img != null && PreviewImage.Source == null) PreviewImage.Source = img; });
            _ = LoadPreviewAsync(p, ct);
        }
    }

    private async Task LoadPreviewAsync(Photo p, CancellationToken ct)
    {
        try
        {
            await Task.Delay(120, ct);
            var img = await Thumbnails.LoadAsync(p, 700, 1400, ct);
            if (!ct.IsCancellationRequested && _infoPhoto == p && img != null) PreviewImage.Source = img;
        }
        catch (OperationCanceledException) { }
    }

    private void BuildTagChips(Photo p)
    {
        TagsPanel.Children.Clear();
        foreach (var tag in p.Tags)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = tag, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button { Style = (Style)FindResource("LinkButton"), Content = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9,
                                 Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(8, 1, 0, 0), ToolTip = $"Remove “{tag}”" };
            var t = tag;
            x.Click += (_, _) => Lib.RemoveTag(new[] { p }, t);
            sp.Children.Add(x);
            var chip = new Border { Background = (Brush)FindResource("ChipBg"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6), Child = sp };
            TagsPanel.Children.Add(chip);
        }
    }

    private void ShowMultiInfo(List<Photo> sel)
    {
        _infoPhoto = null;
        MultiTitle.Text = $"{sel.Count:N0} photos selected";
        if (sel.Count == 0) return;
        var from = sel.Min(p => p.Taken);
        var to = sel.Max(p => p.Taken);
        string range = from.Date == to.Date ? from.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture)
            : from.Year == to.Year ? $"{from:MMM d} – {to:MMM d, yyyy}" : $"{from:MMM d, yyyy} – {to:MMM d, yyyy}";
        int favs = sel.Count(p => p.Favorite);
        MultiText.Text = $"{range}\n{Photo.FormatSize(sel.Sum(p => p.Size))}" + (favs > 0 ? $" · {favs} favorite{(favs == 1 ? "" : "s")}" : "");
        var stack = new[] { Stack1, Stack2, Stack3 };
        for (int i = 0; i < 3; i++)
        {
            var border = stack[i];
            if (i < sel.Count)
            {
                var p = sel[i];
                void Apply(BitmapSource img) { if (img != null) border.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; }
                var img = Thumbnails.Get(p, Apply);
                if (img != null) Apply(img); else border.Background = (Brush)FindResource("TileBg");
                border.Visibility = Visibility.Visible;
            }
            else border.Visibility = Visibility.Collapsed;
        }
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_infoPhoto != null) OpenViewer(_infoPhoto);
    }

    private void InfoOpen_Click(object sender, RoutedEventArgs e) { if (_infoPhoto != null) OpenViewer(_infoPhoto); }
    private void InfoEdit_Click(object sender, RoutedEventArgs e) { if (_infoPhoto != null) Shell.Edit(_infoPhoto.Path); }
    private void InfoShare_Click(object sender, RoutedEventArgs e) { if (_infoPhoto != null) ShowShareMenu(new List<Photo> { _infoPhoto }, ShareBtn); }
    private void InfoFavorite_Click(object sender, RoutedEventArgs e) { if (_infoPhoto != null) ToggleFavorite(new List<Photo> { _infoPhoto }); }

    private void CaptionBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_infoPhoto != null) Lib.SetCaption(_infoPhoto, CaptionBox.Text);
    }

    private void CaptionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _infoPhoto != null)
        {
            Lib.SetCaption(_infoPhoto, CaptionBox.Text);
            Keyboard.Focus(this);
            e.Handled = true;
        }
    }

    private void TagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _infoPhoto == null) return;
        foreach (var t in TagBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Lib.AddTag(new[] { _infoPhoto }, t);
        TagBox.Text = "";
        e.Handled = true;
    }

    private void MultiTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var sel = SelectedPhotos;
        foreach (var t in MultiTagBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Lib.AddTag(sel, t);
        if (MultiTagBox.Text.Trim().Length > 0) ShowToast($"Tagged {sel.Count:N0} photos “{MultiTagBox.Text.Trim()}”");
        MultiTagBox.Text = "";
        e.Handled = true;
    }

    // ================================================================== library changes

    private void OnLibraryChanged(LibraryChange c)
    {
        if (c.HasFlag(LibraryChange.Details))
            foreach (var it in _items.Values) it.Refresh();
        if (c.HasFlag(LibraryChange.Photos) || c.HasFlag(LibraryChange.Albums))
            foreach (var it in _items.Values) it.ForgetSearch();
        RefreshSidebar();

        bool rebuild = c.HasFlag(LibraryChange.Photos) || c.HasFlag(LibraryChange.Folders)
                       || (c.HasFlag(LibraryChange.Albums) && _view is LibraryView.Albums or LibraryView.Album)
                       || (c.HasFlag(LibraryChange.Details) && (_view == LibraryView.Favorites || SearchBox.Text.Trim().Length > 0));
        if (rebuild && _view != LibraryView.Settings) Restart(_rebuildTimer);
        else
        {
            UpdateSelectionUi();
            UpdateInfo();
            if (_view == LibraryView.Album) UpdateHeader();
        }
        if (_view == LibraryView.Settings && c.HasFlag(LibraryChange.Folders)) _settingsPage?.Refresh();
    }

    // ================================================================== scanning

    private bool _scanning;
    private CancellationTokenSource _scanCts;
    private Task _scanTask;
    private readonly HashSet<string> _pendingScan = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingAll;

    public bool IsScanning => _scanning;

    /// <summary>Scans the given folders (null = all). If a scan is running, this one runs right after it. The task finishes when everything queued is done.</summary>
    public Task StartScan(IEnumerable<string> folders)
    {
        if (folders == null) _pendingAll = true;
        else foreach (var f in folders) _pendingScan.Add(f);
        if (_scanTask != null && !_scanTask.IsCompleted) return _scanTask;
        _scanTask = ScanLoopAsync();
        return _scanTask;
    }

    private async Task ScanLoopAsync()
    {
        while (_pendingAll || _pendingScan.Count > 0)
        {
            var folders = _pendingAll
                ? Lib.Folders.ToList()
                : _pendingScan.Where(f => Lib.Folders.Any(lf => string.Equals(lf, f, StringComparison.OrdinalIgnoreCase) || LibraryStore.IsUnder(f, lf))).ToList();
            bool all = _pendingAll;
            _pendingAll = false;
            _pendingScan.Clear();
            if (folders.Count == 0) continue;

            _scanning = true;
            _scanCts = new CancellationTokenSource();
            CardStatus.Text = "scanning";
            CardDot.Fill = (Brush)FindResource("Accent");
            CardProgress.IsIndeterminate = false;
            CardProgress.Value = 0;
            CardText.Text = "Looking for photos…";
            UpdateEmptyState();
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.ToRead > 0)
                {
                    CardProgress.Value = (double)p.Done / p.ToRead;
                    CardText.Text = $"Reading photos: {p.Done:N0} of {p.ToRead:N0}";
                }
                else CardText.Text = $"Looking for photos: {p.Found:N0} found";
            });
            try
            {
                var r = await Scanner.ScanAsync(Lib, folders, progress, _scanCts.Token);
                if (all) _offline = r.OfflineFolders;
                else
                {
                    _offline.RemoveAll(o => folders.Contains(o, StringComparer.OrdinalIgnoreCase));
                    _offline.AddRange(r.OfflineFolders);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowToast("Couldn't finish looking for photos: " + ex.Message); }
            _scanning = false;
        }
        CardStatus.Text = _offline.Count > 0 ? "folder offline" : "up to date";
        CardDot.Fill = (Brush)FindResource(_offline.Count > 0 ? "DangerText" : "Success");
        CardProgress.Value = 0;
        RefreshSidebar();
        RebuildView(true);
    }

    // ------------------------------------------------------------------ watching folders for changes

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    private void SetupWatchers()
    {
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
        if (!S.WatchFolders || App.IsSelfTest) return;
        foreach (var f in Lib.Folders)
        {
            if (!Directory.Exists(f)) continue;
            try
            {
                var w = new FileSystemWatcher(f)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                var root = f;
                FileSystemEventHandler h = (_, e) => OnFolderEvent(root, e.FullPath);
                w.Created += h;
                w.Changed += h;
                w.Deleted += h;
                w.Renamed += (_, e) => OnFolderEvent(root, e.FullPath);
                w.Error += (_, _) => OnFolderEvent(root, null);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { }
        }
    }

    private void OnFolderEvent(string root, string path)
    {
        if (path != null)
        {
            var ext = IOPath.GetExtension(path);
            // pictures, or folders (which have no extension); ignore temp files
            if (ext.Length > 0 && !MetadataReader.IsPicture(path)) return;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _dirty.Add(root);
            Restart(_watchTimer);
        }));
    }

    private void ScanDirtyFolders()
    {
        if (_dirty.Count == 0) return;
        var list = _dirty.ToList();
        _dirty.Clear();
        _ = StartScan(list);
    }

    // ================================================================== toast

    private Action _toastAction;

    public void ShowToast(string text, string action = null, Action onAction = null)
    {
        ToastText.Text = text;
        _toastAction = onAction;
        ToastAction.Content = action;
        ToastAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Interval = TimeSpan.FromMilliseconds(action != null ? 7000 : 4500);
        Restart(_toastTimer);
    }

    private void HideToast() => Toast.Visibility = Visibility.Collapsed;

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var a = _toastAction;
        HideToast();
        a?.Invoke();
    }
}
