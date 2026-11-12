using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Shutterbox.Library;

namespace Shutterbox;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A photo as shown on the timeline. One per photo, reused across views; never holds on to its thumbnail.</summary>
public sealed class PhotoItem : Observable
{
    public Photo Photo { get; }
    public PhotoItem(Photo p) => Photo = p;

    private double _tile = 150;
    public double TileSize
    {
        get => _tile;
        set { if (Math.Abs(_tile - value) > 0.1) { _tile = value; OnChanged(); } }
    }

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; OnChanged(); OnChanged(nameof(SelectedVisibility)); } }
    }

    public Visibility SelectedVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FavoriteVisibility => Photo.Favorite ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailedVisibility => Photo.Unreadable || Thumbnails.HasFailed(Photo) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Asks the thumbnail service each time; it answers from memory or starts making the thumbnail.</summary>
    public ImageSource Thumb => Thumbnails.Get(Photo, img =>
    {
        OnChanged(nameof(Thumb));
        if (img == null) OnChanged(nameof(FailedVisibility));
    });

    public void Refresh()
    {
        _search = null;
        OnChanged(nameof(Thumb));
        OnChanged(nameof(FavoriteVisibility));
        OnChanged(nameof(FailedVisibility));
    }

    private string _search;
    /// <summary>Lower-case text that search matches against: name, folder, tags, caption, camera, month and year.</summary>
    public string SearchText(IEnumerable<string> albumNames)
    {
        if (_search != null) return _search;
        var p = Photo;
        var parts = new List<string>
        {
            p.FileName, p.FolderPath, p.Caption, p.Make, p.Model, p.Lens,
            p.Taken.ToString("yyyy", CultureInfo.InvariantCulture),
            p.Taken.ToString("MMMM", CultureInfo.CurrentCulture), p.Taken.ToString("MMM", CultureInfo.CurrentCulture),
            p.Taken.ToString("dddd", CultureInfo.CurrentCulture),
            p.Taken.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            p.Favorite ? "favorite favourite" : null,
            p.HasLocation ? "location gps map" : null,
            p.Unreadable ? "unreadable" : null,
        };
        parts.AddRange(p.Tags);
        if (albumNames != null) parts.AddRange(albumNames);
        return _search = string.Join(" | ", parts.Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
    }

    public void ForgetSearch() => _search = null;
}

public abstract class TimelineRow
{
    public double Height { get; set; }
}

public sealed class HeaderRow : TimelineRow
{
    public string Title { get; set; }
    public string Subtitle { get; set; }
    public string CountText { get; set; }
    public List<PhotoItem> Items { get; set; }
    public Thickness TitleMargin => new(0, 0, 0, 10);
}

public sealed class PhotoRow : TimelineRow
{
    public List<PhotoItem> Items { get; set; }
}

public sealed class AlbumCard : Observable
{
    public Album Album { get; init; }
    public PhotoItem Cover { get; init; }
    public string Name => Album?.Name ?? "New album";
    public string CountText { get; init; }
    public bool IsNew => Album == null;
    public Visibility NewVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CoverVisibility => IsNew ? Visibility.Collapsed : Visibility.Visible;
}
