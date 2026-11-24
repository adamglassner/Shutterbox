using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Input;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox.Views;

/// <summary>
/// Every photo with a location on an OpenStreetMap map. Drag to move, scroll to zoom. Pins group photos taken close
/// together (they split as you zoom in); clicking one shows its photos. Map tiles are cached in the thumbnail folder.
/// </summary>
public sealed class PlacesMap : Grid
{
    private const int Tile = 256, CellPixels = 72;
    private static readonly HttpClient Http = MakeClient();
    private static readonly Dictionary<string, BitmapSource> Memory = new();
    private static readonly HashSet<string> Loading = new();

    private readonly Canvas _tiles = new() { ClipToBounds = true };
    private readonly Canvas _pins = new() { ClipToBounds = true };
    private readonly Action<List<Photo>, string> _open;
    private List<Photo> _photos = new();
    private int _zoom = 3;
    private double _cx, _cy; // center in world pixels at _zoom
    private Point? _drag;
    private Point _dragCenter;
    private bool _fitted;

    public PlacesMap(Action<List<Photo>, string> open)
    {
        _open = open;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "TileBg");
        Children.Add(_tiles);
        Children.Add(_pins);
        var attribution = new TextBlock
        {
            Text = "© OpenStreetMap contributors", FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            Foreground = Brushes.Black,
        };
        attribution.MouseLeftButtonUp += (_, _) => Shell.Open("https://www.openstreetmap.org/copyright");
        Children.Add(attribution);
        var zoom = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
        Button ZoomBtn(string text, int step)
        {
            var b = new Button { Content = text, Width = 34, Height = 34, Margin = new Thickness(0, 0, 0, 6), FontSize = 16, ToolTip = step > 0 ? "Zoom in" : "Zoom out" };
            b.Click += (_, _) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), step);
            return b;
        }
        zoom.Children.Add(ZoomBtn("+", 1));
        zoom.Children.Add(ZoomBtn("−", -1));
        Children.Add(zoom);
        SizeChanged += (_, _) => { if (!_fitted) Fit(); Render(); };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is FrameworkElement { Tag: "pin" }) return; _drag = e.GetPosition(this); _dragCenter = new Point(_cx, _cy); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_drag is not Point d) return;
            var p = e.GetPosition(this);
            _cx = _dragCenter.X - (p.X - d.X);
            _cy = _dragCenter.Y - (p.Y - d.Y);
            Render();
        };
        MouseLeftButtonUp += (_, _) => { _drag = null; ReleaseMouseCapture(); };
        MouseWheel += (_, e) => { ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1 : -1); e.Handled = true; };
    }

    private static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Shutterbox/{AppInfo.Version} (+https://github.com/adamglassner/Shutterbox)");
        return c;
    }

    public int PlaceCount => _photos.Count;

    public void SetPhotos(IEnumerable<Photo> photos)
    {
        _photos = photos.Where(p => p.HasLocation).ToList();
        _fitted = false;
        if (ActualWidth > 0) { Fit(); Render(); }
    }

    // ------------------------------------------------------------------ projection (Web Mercator)

    private static double WorldX(double lng, int z) => (lng + 180) / 360 * Tile * Math.Pow(2, z);
    private static double WorldY(double lat, int z)
    {
        var s = Math.Sin(Math.Clamp(lat, -85, 85) * Math.PI / 180);
        return (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * Tile * Math.Pow(2, z);
    }

    private void Fit()
    {
        if (_photos.Count == 0 || ActualWidth <= 0) return;
        _fitted = true;
        double minLat = _photos.Min(p => p.Latitude.Value), maxLat = _photos.Max(p => p.Latitude.Value);
        double minLng = _photos.Min(p => p.Longitude.Value), maxLng = _photos.Max(p => p.Longitude.Value);
        _zoom = 14;
        while (_zoom > 2 && (WorldX(maxLng, _zoom) - WorldX(minLng, _zoom) > ActualWidth - 120 || WorldY(minLat, _zoom) - WorldY(maxLat, _zoom) > ActualHeight - 120)) _zoom--;
        _cx = (WorldX(minLng, _zoom) + WorldX(maxLng, _zoom)) / 2;
        _cy = (WorldY(minLat, _zoom) + WorldY(maxLat, _zoom)) / 2;
    }

    private void ZoomAt(Point at, int step)
    {
        int z = Math.Clamp(_zoom + step, 2, 18);
        if (z == _zoom) return;
        // keep the point under the mouse where it is
        double wx = _cx - ActualWidth / 2 + at.X, wy = _cy - ActualHeight / 2 + at.Y;
        double f = Math.Pow(2, z - _zoom);
        _cx = wx * f - at.X + ActualWidth / 2;
        _cy = wy * f - at.Y + ActualHeight / 2;
        _zoom = z;
        Render();
    }

    // ------------------------------------------------------------------ drawing

    private void Render()
    {
        if (ActualWidth <= 0) return;
        double left = _cx - ActualWidth / 2, top = _cy - ActualHeight / 2;
        int n = 1 << _zoom;
        _tiles.Children.Clear();
        for (int tx = (int)Math.Floor(left / Tile); tx <= (int)Math.Floor((left + ActualWidth) / Tile); tx++)
            for (int ty = (int)Math.Floor(top / Tile); ty <= (int)Math.Floor((top + ActualHeight) / Tile); ty++)
            {
                if (ty < 0 || ty >= n) continue;
                int wrapped = ((tx % n) + n) % n;
                var img = new Image { Width = Tile, Height = Tile, Source = TileImage(_zoom, wrapped, ty) };
                Canvas.SetLeft(img, tx * Tile - left);
                Canvas.SetTop(img, ty * Tile - top);
                _tiles.Children.Add(img);
            }

        _pins.Children.Clear();
        var cells = new Dictionary<(long, long), List<Photo>>();
        foreach (var p in _photos)
        {
            var key = ((long)Math.Floor(WorldX(p.Longitude.Value, _zoom) / CellPixels), (long)Math.Floor(WorldY(p.Latitude.Value, _zoom) / CellPixels));
            if (!cells.TryGetValue(key, out var l)) cells[key] = l = new();
            l.Add(p);
        }
        foreach (var group in cells.Values.OrderBy(g => g.Count))
        {
            double x = group.Average(p => WorldX(p.Longitude.Value, _zoom)) - left, y = group.Average(p => WorldY(p.Latitude.Value, _zoom)) - top;
            if (x < -40 || y < -40 || x > ActualWidth + 40 || y > ActualHeight + 40) continue;
            _pins.Children.Add(Pin(group.OrderByDescending(p => p.Taken).ToList(), x, y));
        }
    }

    private FrameworkElement Pin(List<Photo> photos, double x, double y)
    {
        var first = photos[0];
        var border = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(10), BorderBrush = Brushes.White, BorderThickness = new Thickness(2.5),
            Cursor = Cursors.Hand, Tag = "pin", ToolTip = $"{photos.Count:N0} photo{(photos.Count == 1 ? "" : "s")}",
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.45 },
        };
        border.SetResourceReference(Border.BackgroundProperty, "TileBg");
        void Apply(BitmapSource img) { if (img != null) border.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; }
        var thumb = Thumbnails.Get(first, Apply);
        if (thumb != null) Apply(thumb);
        var grid = new Grid { Tag = "pin" };
        grid.Children.Add(border);
        if (photos.Count > 1)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -8, -8, 0), Tag = "pin",
                Child = new TextBlock { Text = photos.Count.ToString("N0"), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Tag = "pin" },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Accent");
            grid.Children.Add(badge);
        }
        grid.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            double lat = photos.Average(p => p.Latitude.Value), lng = photos.Average(p => p.Longitude.Value);
            _open(photos, $"Near {Math.Abs(lat):0.00}°{(lat >= 0 ? "N" : "S")}, {Math.Abs(lng):0.00}°{(lng >= 0 ? "E" : "W")}");
        };
        Canvas.SetLeft(grid, x - 28);
        Canvas.SetTop(grid, y - 28);
        return grid;
    }

    private BitmapSource TileImage(int z, int x, int y)
    {
        var key = $"{z}/{x}/{y}";
        lock (Memory) if (Memory.TryGetValue(key, out var b)) return b;
        var path = IOPath.Combine(SettingsService.CacheFolder, "maptiles", z.ToString(), x.ToString(), y + ".png");
        if (File.Exists(path) && (DateTime.Now - File.GetLastWriteTime(path)).TotalDays < 30)
        {
            try { var bmp = Decode(File.ReadAllBytes(path)); lock (Memory) Memory[key] = bmp; return bmp; } catch { }
        }
        lock (Loading) if (!Loading.Add(key)) return null;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync($"https://tile.openstreetmap.org/{z}/{x}/{y}.png");
                Directory.CreateDirectory(IOPath.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
                var bmp = Decode(bytes);
                lock (Memory) Memory[key] = bmp;
                await Dispatcher.InvokeAsync(Render);
            }
            catch { /* offline: the tile stays blank */ }
            finally { lock (Loading) Loading.Remove(key); }
        });
        return null;
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
