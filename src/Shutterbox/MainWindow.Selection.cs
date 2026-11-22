using System.Windows.Controls;
using System.Windows.Input;
using Shutterbox.Library;

namespace Shutterbox;

/// <summary>
/// Picking lots of photos and videos at once:
///  - the check circle on a tile (shows on hover) adds or removes it without holding Ctrl;
///  - Select mode (the check button in the header): every click adds or removes, and dragging across tiles paints a selection;
///  - dragging a box from empty space selects everything it touches, scrolling when you reach the top or bottom;
///  - Select on a day's heading, Select all in the selection bar, Ctrl+A, Shift+click ranges, Space for the photo you're on.
/// </summary>
public partial class MainWindow
{
    private bool _selectMode;
    private bool _painting;
    private bool _paintOn;

    public bool SelectModeOn => _selectMode;

    // ================================================================== select mode

    private void SelectMode_Click(object sender, RoutedEventArgs e) => SetSelectMode(SelectModeToggle.IsChecked == true);

    public void SetSelectMode(bool on)
    {
        _selectMode = on;
        SelectModeToggle.IsChecked = on;
        PhotoItem.SelectMode = on;
        foreach (var it in _items.Values) it.SelectModeChanged();
        UpdateSelectionUi();
        if (on && _selected.Count == 0) ShowToast("Click or drag across photos and videos to select them. Press Esc or Done when you're finished.");
    }

    // ================================================================== check circle and painting

    private void TileCheck_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItem it) return;
        e.Handled = true;
        Keyboard.Focus(this);
        int idx = _indexOf.TryGetValue(it.Photo.Id, out var i) ? i : -1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0 && idx >= 0)
        {
            SelectRange(_anchor, idx, true);
            return;
        }
        BeginPaint(it, !it.IsSelected);
        _anchor = idx;
    }

    /// <summary>Adds (or removes) a tile and keeps doing the same to tiles the mouse passes over while the button is down.</summary>
    private void BeginPaint(PhotoItem it, bool on)
    {
        _painting = true;
        _paintOn = on;
        SetSelected(it, on);
        _focusId = it.Photo.Id;
        _downItem = null;
        _deferSelect = false;
        SelectionChanged();
    }

    private void Tile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_painting) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _painting = false; return; }
        if ((sender as FrameworkElement)?.DataContext is PhotoItem it) PaintOver(it);
    }

    internal void PaintOver(PhotoItem it)
    {
        if (!_painting || it.IsSelected == _paintOn) return;
        SetSelected(it, _paintOn);
        SelectionChanged();
    }

    internal void EndPaint() => _painting = false;

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        _painting = false;
    }

    /// <summary>Space adds or removes the photo you're on (after moving with the arrow keys).</summary>
    private void ToggleFocused()
    {
        var it = FocusItem();
        if (it == null) return;
        SetSelected(it, !it.IsSelected);
        _anchor = _indexOf.TryGetValue(it.Photo.Id, out var i) ? i : _anchor;
        SelectionChanged();
    }

    // ================================================================== drag a box

    private bool _marquee;
    private Point _marqueeStart;           // in the timeline, when the drag started
    private double _marqueeStartOffset;     // scroll offset then
    private HashSet<string> _marqueeBase;   // what was selected before (kept with Ctrl)
    private readonly HashSet<string> _marqueeHits = new();
    private DispatcherTimer _marqueeScroll;
    private bool _marqueeMoved;

    /// <summary>Starts a box selection from empty space in the timeline.</summary>
    private void BeginMarquee(MouseButtonEventArgs e)
    {
        var sv = TimelineScroller();
        if (sv == null) return;
        _marquee = true;
        _marqueeMoved = false;
        _marqueeStart = e.GetPosition(Timeline);
        _marqueeStartOffset = sv.VerticalOffset;
        bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _selectMode;
        _marqueeBase = add ? new HashSet<string>(_selected) : new HashSet<string>();
        _marqueeHits.Clear();
        Timeline.CaptureMouse();
        Timeline.MouseMove -= Marquee_MouseMove;
        Timeline.MouseMove += Marquee_MouseMove;
        Timeline.MouseLeftButtonUp -= Marquee_MouseUp;
        Timeline.MouseLeftButtonUp += Marquee_MouseUp;
        Timeline.LostMouseCapture -= Marquee_LostCapture;
        Timeline.LostMouseCapture += Marquee_LostCapture;
        if (_marqueeScroll == null)
        {
            _marqueeScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _marqueeScroll.Tick += (_, _) => MarqueeAutoScroll();
        }
        _marqueeScroll.Start();
    }

    private void Marquee_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_marquee) return;
        var p = e.GetPosition(Timeline);
        if (!_marqueeMoved && Math.Abs(p.X - _marqueeStart.X) < 4 && Math.Abs(p.Y - _marqueeStart.Y) < 4) return;
        _marqueeMoved = true;
        UpdateMarquee(p);
    }

    private void MarqueeAutoScroll()
    {
        if (!_marquee || !_marqueeMoved) return;
        var sv = TimelineScroller();
        if (sv == null) return;
        var p = Mouse.GetPosition(Timeline);
        double edge = 48, speed = 0;
        if (p.Y < edge) speed = -(edge - p.Y) * 0.6;
        else if (p.Y > Timeline.ActualHeight - edge) speed = (p.Y - (Timeline.ActualHeight - edge)) * 0.6;
        if (Math.Abs(speed) < 0.5) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + Math.Clamp(speed, -40, 40));
        Timeline.UpdateLayout();
        UpdateMarquee(p);
    }

    /// <summary>Draws the box and selects the tiles it touches (in content coordinates, so it works while scrolling).</summary>
    private void UpdateMarquee(Point now)
    {
        var sv = TimelineScroller();
        if (sv == null) return;
        double offset = sv.VerticalOffset;
        double y0 = _marqueeStart.Y + _marqueeStartOffset, y1 = now.Y + offset;
        double top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);
        double left = Math.Min(_marqueeStart.X, now.X), right = Math.Max(_marqueeStart.X, now.X);

        // the box on screen
        double screenTop = Math.Max(0, top - offset), screenBottom = Math.Min(Timeline.ActualHeight, bottom - offset);
        Canvas.SetLeft(Marquee, left);
        Canvas.SetTop(Marquee, screenTop);
        Marquee.Width = Math.Max(1, right - left);
        Marquee.Height = Math.Max(1, screenBottom - screenTop);
        Marquee.Visibility = Visibility.Visible;

        // tiles on screen: in or out of the box (tiles scrolled away keep what they had)
        var panel = TimelinePanel();
        if (panel == null) return;
        var box = new Rect(left, top, right - left, bottom - top);
        foreach (var child in panel.Children)
        {
            if (child is not ListBoxItem li || li.DataContext is not PhotoRow row) continue;
            var presenter = FindChild<ItemsPresenter>(li);
            var host = presenter != null ? VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as Panel : null : null;
            if (host == null) continue;
            foreach (var c in host.Children)
            {
                if (c is not FrameworkElement fe || fe.DataContext is not PhotoItem it) continue;
                var pos = fe.TranslatePoint(new Point(0, 0), Timeline);
                var r = new Rect(pos.X, pos.Y + offset, fe.ActualWidth, fe.ActualHeight);
                if (r.IntersectsWith(box)) _marqueeHits.Add(it.Photo.Id);
                else _marqueeHits.Remove(it.Photo.Id);
            }
        }
        bool changed = false;
        foreach (var it in _flat)
        {
            bool want = _marqueeBase.Contains(it.Photo.Id) || _marqueeHits.Contains(it.Photo.Id);
            if (it.IsSelected != want) { SetSelected(it, want); changed = true; }
        }
        if (changed) SelectionChanged();
    }

    private void Marquee_MouseUp(object sender, MouseButtonEventArgs e) => EndMarquee();
    private void Marquee_LostCapture(object sender, MouseEventArgs e) => EndMarquee();

    private void EndMarquee()
    {
        if (!_marquee) return;
        _marquee = false;
        _marqueeScroll?.Stop();
        Marquee.Visibility = Visibility.Collapsed;
        Timeline.MouseMove -= Marquee_MouseMove;
        Timeline.MouseLeftButtonUp -= Marquee_MouseUp;
        Timeline.LostMouseCapture -= Marquee_LostCapture;
        if (Timeline.IsMouseCaptured) Timeline.ReleaseMouseCapture();
        if (!_marqueeMoved && _marqueeBase.Count == 0 && _selected.Count > 0 && !_selectMode)
        {
            // a plain click on empty space clears the selection
            ClearSelection();
            SelectionChanged();
        }
        else if (_marqueeMoved)
        {
            var first = _flat.FirstOrDefault(i => _marqueeHits.Contains(i.Photo.Id));
            if (first != null) { _focusId = first.Photo.Id; _anchor = _indexOf[first.Photo.Id]; }
        }
    }

    /// <summary>For the self-test: a box drag between two points in the timeline.</summary>
    internal void SimulateMarquee(Point from, Point to, bool add)
    {
        var sv = TimelineScroller();
        _marquee = true;
        _marqueeMoved = true;
        _marqueeStart = from;
        _marqueeStartOffset = sv?.VerticalOffset ?? 0;
        _marqueeBase = add ? new HashSet<string>(_selected) : new HashSet<string>();
        _marqueeHits.Clear();
        UpdateMarquee(to);
        _marquee = false;
        Marquee.Visibility = Visibility.Collapsed;
    }
}
