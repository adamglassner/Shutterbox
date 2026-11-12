using System.Globalization;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox;

/// <summary>The full-window photo viewer: next/previous, zoom and pan, favorite, rotate, slideshow.</summary>
public partial class MainWindow
{
    private List<Photo> _viewList = new();
    private int _viewIndex;
    private Photo _viewPhoto;
    private bool _fullLoaded;
    private CancellationTokenSource _viewCts;
    private readonly Dictionary<string, BitmapSource> _viewCache = new();
    private readonly DispatcherTimer _slideTimer;
    private readonly DispatcherTimer _hideUiTimer;
    private bool _slideshow;
    private Point _panStart;
    private Point _panOrigin;
    private bool _panning;

    public bool ViewerOpen => ViewerLayer.Visibility == Visibility.Visible;
    public Photo ViewerPhoto => _viewPhoto;
    public int ViewerIndex => _viewIndex;
    public bool SlideshowRunning => _slideshow;
    public double ViewerZoom => ViewerScale.ScaleX;

    /// <summary>Opens a photo, browsing through the photos currently shown (or a given list).</summary>
    public void OpenViewer(Photo p, List<Photo> list = null)
    {
        _viewList = list ?? _flat.Select(i => i.Photo).ToList();
        if (_viewList.Count == 0) return;
        _viewIndex = Math.Max(0, _viewList.IndexOf(p));
        ViewerLayer.Visibility = Visibility.Visible;
        ViewerLayer.Focus();
        Keyboard.Focus(ViewerLayer);
        ShowViewerChrome();
        ShowViewerPhoto(false);
    }

    public void CloseViewer()
    {
        StopSlideshow();
        _viewCts?.Cancel();
        ViewerLayer.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
        ViewerFade.Source = null;
        _viewCache.Clear();
        Mouse.OverrideCursor = null;
        // Select the photo you ended on so the timeline picks up where you were
        if (_viewPhoto != null && _indexOf.TryGetValue(_viewPhoto.Id, out var i))
        {
            SelectOnly(_flat[i]);
            ScrollToPhoto(_viewPhoto.Id);
        }
        _viewPhoto = null;
        Keyboard.Focus(this);
    }

    public void ViewerStep(int delta)
    {
        if (_viewList.Count == 0) return;
        int next = _viewIndex + delta;
        if (_slideshow) next = (next + _viewList.Count) % _viewList.Count;
        if (next < 0 || next >= _viewList.Count) return;
        _viewIndex = next;
        ShowViewerPhoto(_slideshow);
    }

    private int ScreenPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (int)(Math.Max(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight) * Math.Max(1, dpi.DpiScaleX));
    }

    private void ShowViewerPhoto(bool crossfade)
    {
        _viewCts?.Cancel();
        _viewCts = new CancellationTokenSource();
        var ct = _viewCts.Token;
        var p = _viewList[_viewIndex];
        _viewPhoto = p;
        _fullLoaded = false;
        ResetZoom();

        ViewerTitle.Text = p.FileName;
        var parts = new List<string> { p.Taken.ToString("dddd, MMMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture) };
        if (!string.IsNullOrEmpty(p.CameraText)) parts.Add(p.CameraText);
        if (!string.IsNullOrEmpty(p.ExposureText)) parts.Add(p.ExposureText);
        ViewerSub.Text = string.Join("   ·   ", parts);
        ViewerCounter.Text = $"{_viewIndex + 1:N0} of {_viewList.Count:N0}";
        ViewerFavBtn.Content = p.Favorite ? "" : "";
        ViewerFavBtn.Foreground = p.Favorite ? (Brush)FindResource("Accent") : (Brush)FindResource("ViewerText");
        ViewerPrevBtn.Visibility = _slideshow || _viewIndex > 0 ? Visibility.Visible : Visibility.Hidden;
        ViewerNextBtn.Visibility = _slideshow || _viewIndex < _viewList.Count - 1 ? Visibility.Visible : Visibility.Hidden;
        if (_slideshow) { ViewerPrevBtn.Visibility = Visibility.Hidden; ViewerNextBtn.Visibility = Visibility.Hidden; }

        if (crossfade && ViewerImage.Source != null)
        {
            ViewerFade.Source = ViewerImage.Source;
            ViewerFade.Opacity = 1;
            ViewerFade.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)));
            ViewerImage.Opacity = 0;
            ViewerImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)));
        }
        else
        {
            ViewerFade.BeginAnimation(OpacityProperty, null);
            ViewerImage.BeginAnimation(OpacityProperty, null);
            ViewerFade.Opacity = 0;
            ViewerImage.Opacity = 1;
        }

        // Thumbnail first so something shows immediately, then the screen-sized picture
        var key = Thumbnails.KeyOf(p);
        if (_viewCache.TryGetValue(key, out var cached))
        {
            SetViewerImage(cached, p);
        }
        else
        {
            var thumb = Thumbnails.Get(p, null);
            ViewerImage.Source = thumb;
            ViewerMessage.Visibility = Visibility.Collapsed;
            _ = LoadViewerImageAsync(p, ct);
        }
        PreloadNeighbors(ct);
    }

    private void SetViewerImage(BitmapSource img, Photo p)
    {
        if (img == null)
        {
            ViewerImage.Source = null;
            ViewerMessage.Visibility = Visibility.Visible;
            ViewerMessageText.Text = IOPath.GetExtension(p.Path).ToLowerInvariant() is ".heic" or ".heif"
                ? $"Windows can't show {p.FileName} yet.\nInstall “HEIF Image Extensions” from the Microsoft Store, then open it again."
                : File.Exists(p.Path) ? $"Windows can't show {p.FileName}.\nIt may be damaged, or need a codec from the Microsoft Store." : $"{p.FileName} isn't there any more.\nIt may have been moved, renamed or deleted.";
            return;
        }
        ViewerMessage.Visibility = Visibility.Collapsed;
        ViewerImage.Source = img;
    }

    private async Task LoadViewerImageAsync(Photo p, CancellationToken ct)
    {
        try
        {
            var px = ScreenPixels();
            var img = await Thumbnails.LoadAsync(p, px, px * 2, ct);
            if (ct.IsCancellationRequested || _viewPhoto != p) return;
            if (img != null) _viewCache[Thumbnails.KeyOf(p)] = img;
            SetViewerImage(img, p);
        }
        catch (OperationCanceledException) { }
    }

    private void PreloadNeighbors(CancellationToken ct)
    {
        // keep the cache small: current and the ones either side
        var keep = new HashSet<string>();
        for (int d = -1; d <= 1; d++)
        {
            int i = _viewIndex + d;
            if (_slideshow) i = (i + _viewList.Count) % _viewList.Count;
            if (i >= 0 && i < _viewList.Count) keep.Add(Thumbnails.KeyOf(_viewList[i]));
        }
        foreach (var k in _viewCache.Keys.Where(k => !keep.Contains(k)).ToList()) _viewCache.Remove(k);
        var px = ScreenPixels();
        foreach (var d in new[] { 1, -1 })
        {
            int i = _viewIndex + d;
            if (_slideshow) i = (i + _viewList.Count) % _viewList.Count;
            if (i < 0 || i >= _viewList.Count) continue;
            var p = _viewList[i];
            var key = Thumbnails.KeyOf(p);
            if (_viewCache.ContainsKey(key)) continue;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, ct);
                    var img = await Thumbnails.LoadAsync(p, px, px * 2, ct);
                    if (img != null && !ct.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() => { if (ViewerOpen) _viewCache[key] = img; });
                }
                catch { }
            });
        }
    }

    // ------------------------------------------------------------------ zoom and pan

    private void ResetZoom()
    {
        ViewerScale.ScaleX = ViewerScale.ScaleY = 1;
        ViewerPan.X = ViewerPan.Y = 0;
        ViewerZoomText.Text = "";
        ViewerStage.Cursor = null;
    }

    public void ZoomViewer(double factor, Point? at = null)
    {
        if (ViewerImage.Source == null) return;
        double old = ViewerScale.ScaleX;
        double s = Math.Clamp(old * factor, 1, 10);
        if (Math.Abs(s - old) < 0.001) return;
        // zoom around the given point (in the image's own coordinates before scaling)
        var center = at ?? new Point(ViewerImage.ActualWidth / 2, ViewerImage.ActualHeight / 2);
        var screen = new Point(center.X * old + ViewerPan.X, center.Y * old + ViewerPan.Y);
        ViewerScale.ScaleX = ViewerScale.ScaleY = s;
        if (s <= 1.0001) { ViewerPan.X = ViewerPan.Y = 0; }
        else
        {
            ViewerPan.X = screen.X - center.X * s;
            ViewerPan.Y = screen.Y - center.Y * s;
            ClampPan();
        }
        ViewerZoomText.Text = s > 1.001 ? $"{s * 100:0}%" : "";
        ViewerStage.Cursor = s > 1.001 ? Cursors.SizeAll : null;
        if (s > 1.5 && !_fullLoaded && _viewPhoto != null) _ = LoadFullAsync(_viewPhoto);
    }

    private void ClampPan()
    {
        double s = ViewerScale.ScaleX;
        double w = ViewerImage.ActualWidth, h = ViewerImage.ActualHeight;
        ViewerPan.X = Math.Clamp(ViewerPan.X, w - w * s, 0);
        ViewerPan.Y = Math.Clamp(ViewerPan.Y, h - h * s, 0);
    }

    private async Task LoadFullAsync(Photo p)
    {
        _fullLoaded = true;
        var img = await Thumbnails.LoadAsync(p, 0, 0);
        if (img != null && _viewPhoto == p && ViewerOpen) ViewerImage.Source = img;
    }

    private void ViewerStage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_slideshow) return;
        ZoomViewer(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(ViewerImage));
        e.Handled = true;
    }

    private void ViewerStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (ViewerScale.ScaleX > 1.001) ResetZoom();
            else ZoomViewer(2.5, e.GetPosition(ViewerImage));
            e.Handled = true;
            return;
        }
        if (ViewerScale.ScaleX > 1.001)
        {
            _panning = true;
            _panStart = e.GetPosition(ViewerStage);
            _panOrigin = new Point(ViewerPan.X, ViewerPan.Y);
            ViewerStage.CaptureMouse();
            e.Handled = true;
        }
    }

    private void ViewerStage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(ViewerStage);
        ViewerPan.X = _panOrigin.X + (p.X - _panStart.X);
        ViewerPan.Y = _panOrigin.Y + (p.Y - _panStart.Y);
        ClampPan();
    }

    private void ViewerStage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ViewerStage.ReleaseMouseCapture();
    }

    private void ViewerStage_SizeChanged(object sender, SizeChangedEventArgs e) => ResetZoom();

    // ------------------------------------------------------------------ chrome and keys

    private void Viewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_slideshow) ShowViewerChrome();
    }

    private void ShowViewerChrome()
    {
        ViewerTopBar.Visibility = Visibility.Visible;
        ViewerCounterBox.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = null;
        if (_slideshow) Restart(_hideUiTimer);
    }

    private void HideViewerChrome()
    {
        if (!_slideshow || !ViewerOpen) return;
        ViewerTopBar.Visibility = Visibility.Collapsed;
        ViewerCounterBox.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = Cursors.None;
    }

    private void ViewerKey(KeyEventArgs e)
    {
        var p = _viewPhoto;
        switch (e.Key)
        {
            case Key.Escape:
                if (_slideshow) StopSlideshow(); else CloseViewer();
                break;
            case Key.Left: case Key.PageUp: case Key.Back: ViewerStep(-1); break;
            case Key.Right: case Key.PageDown: ViewerStep(1); break;
            case Key.Home: _viewIndex = 0; ShowViewerPhoto(false); break;
            case Key.End: _viewIndex = _viewList.Count - 1; ShowViewerPhoto(false); break;
            case Key.Space: case Key.F5:
                if (_slideshow) StopSlideshow(); else StartSlideshowHere();
                break;
            case Key.Add: case Key.OemPlus: ZoomViewer(1.25); break;
            case Key.Subtract: case Key.OemMinus: ZoomViewer(1 / 1.25); break;
            case Key.D0: case Key.NumPad0: ResetZoom(); break;
            case Key.F: if (p != null) ViewerFavorite_Click(null, null); break;
            case Key.R: if (p != null) RotateViewer(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -90 : 90); break;
            case Key.Delete: if (p != null) ViewerTrash_Click(null, null); break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): if (p != null) CopyPhotos(new List<Photo> { p }); break;
            case Key.Enter: if (_slideshow) StopSlideshow(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ViewerClose_Click(object sender, RoutedEventArgs e) => CloseViewer();
    private void ViewerPrev_Click(object sender, RoutedEventArgs e) => ViewerStep(-1);
    private void ViewerNext_Click(object sender, RoutedEventArgs e) => ViewerStep(1);
    private void ViewerZoomIn_Click(object sender, RoutedEventArgs e) => ZoomViewer(1.25);
    private void ViewerZoomOut_Click(object sender, RoutedEventArgs e) => ZoomViewer(1 / 1.25);
    private void ViewerEdit_Click(object sender, RoutedEventArgs e) { if (_viewPhoto != null) Shell.Edit(_viewPhoto.Path); }
    private void ViewerShare_Click(object sender, RoutedEventArgs e) { if (_viewPhoto != null) ShowShareMenu(new List<Photo> { _viewPhoto }, (FrameworkElement)sender); }

    private void ViewerFavorite_Click(object sender, RoutedEventArgs e)
    {
        var p = _viewPhoto;
        if (p == null) return;
        Lib.SetFavorite(new[] { p }, !p.Favorite);
        ViewerFavBtn.Content = p.Favorite ? "" : "";
        ViewerFavBtn.Foreground = p.Favorite ? (Brush)FindResource("Accent") : (Brush)FindResource("ViewerText");
    }

    private void ViewerRotate_Click(object sender, RoutedEventArgs e) => RotateViewer(90);

    private void RotateViewer(int degrees)
    {
        var p = _viewPhoto;
        if (p == null) return;
        _viewCache.Remove(Thumbnails.KeyOf(p));
        Thumbnails.Forget(p);
        Lib.Rotate(new[] { p }, degrees);
        ShowViewerPhoto(false);
    }

    private void ViewerTrash_Click(object sender, RoutedEventArgs e)
    {
        var p = _viewPhoto;
        if (p == null) return;
        if (p.IsTrashed) return;
        Lib.Trash(new[] { p });
        ShowToast("Moved to Trash", "Undo", () => Lib.Restore(new[] { p }));
        _viewList.RemoveAt(_viewIndex);
        if (_viewList.Count == 0) { _viewPhoto = null; CloseViewer(); return; }
        if (_viewIndex >= _viewList.Count) _viewIndex = _viewList.Count - 1;
        ShowViewerPhoto(false);
    }

    // ------------------------------------------------------------------ slideshow

    private void Slideshow_Click(object sender, RoutedEventArgs e) => StartSlideshow();
    private void ViewerPlay_Click(object sender, RoutedEventArgs e) { if (_slideshow) StopSlideshow(); else StartSlideshowHere(); }

    /// <summary>Plays the selected photos (or the whole view) from the start.</summary>
    public void StartSlideshow(List<Photo> photos = null)
    {
        if (photos == null && _selected.Count > 1) photos = SelectedPhotos;
        var list = photos ?? _flat.Select(i => i.Photo).ToList();
        list = list.Where(p => !p.Unreadable).ToList();
        if (list.Count == 0) { ShowToast("There are no photos to show."); return; }
        // A slideshow tells the story in order: oldest first
        list = list.OrderBy(p => p.Taken).ToList();
        OpenViewer(list[0], list);
        StartSlideshowHere();
    }

    private void StartSlideshowHere()
    {
        if (_viewList.Count == 0) return;
        _slideshow = true;
        ResetZoom();
        _slideTimer.Interval = TimeSpan.FromSeconds(S.SlideshowSeconds);
        _slideTimer.Start();
        ViewerPlayBtn.Content = "";
        ViewerPrevBtn.Visibility = Visibility.Hidden;
        ViewerNextBtn.Visibility = Visibility.Hidden;
        Restart(_hideUiTimer);
    }

    public void StopSlideshow()
    {
        if (!_slideshow) return;
        _slideshow = false;
        _slideTimer.Stop();
        _hideUiTimer.Stop();
        ViewerPlayBtn.Content = "";
        ShowViewerChrome();
        if (ViewerOpen && _viewList.Count > 0) ShowViewerPhoto(false);
    }

    private void SlideshowTick()
    {
        if (!_slideshow || !ViewerOpen) { _slideTimer.Stop(); return; }
        ViewerStep(1);
    }
}
