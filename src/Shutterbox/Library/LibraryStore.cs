using System.Text.Json;
using System.Text.Json.Serialization;
using Shutterbox.Services;

namespace Shutterbox.Library;

[Flags]
public enum LibraryChange
{
    None = 0,
    /// <summary>Photos were added, removed, trashed or restored.</summary>
    Photos = 1,
    /// <summary>Favorites, tags, captions or rotation changed (the set of photos is the same).</summary>
    Details = 2,
    Albums = 4,
    Folders = 8,
    All = 15,
}

/// <summary>
/// The photo library: which folders are watched, every photo found in them, albums, favorites and tags.
/// Kept in %APPDATA%\Shutterbox\library.json. Every change is made on the UI thread (the scanner hands
/// its results over in batches), so no locking is needed.
/// </summary>
public sealed class LibraryStore
{
    public static LibraryStore Current { get; set; }

    public string Folder { get; }
    public string FilePath => IOPath.Combine(Folder, "library.json");

    private LibraryData _data = new();
    private readonly Dictionary<string, Photo> _byId = new();
    private readonly Dictionary<string, Photo> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer _saveTimer;

    public event Action<LibraryChange> Changed;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public LibraryStore(string folder) => Folder = folder;

    public IReadOnlyList<string> Folders => _data.Folders;
    public IReadOnlyList<Photo> Photos => _data.Photos;
    public IReadOnlyList<Album> Albums => _data.Albums;
    public int Count => _data.Photos.Count;

    public Photo Get(string id) => id != null && _byId.TryGetValue(id, out var p) ? p : null;
    public Photo GetByPath(string path) => path != null && _byPath.TryGetValue(path, out var p) ? p : null;
    public Album GetAlbum(string id) => _data.Albums.FirstOrDefault(a => a.Id == id);

    // ------------------------------------------------------------------ load / save

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(FilePath), Json);
                if (d != null) _data = d;
            }
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            _data = new LibraryData();
        }
        _data.Folders ??= new();
        _data.Photos ??= new();
        _data.Albums ??= new();
        _data.Photos.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Path));
        _byId.Clear();
        _byPath.Clear();
        foreach (var p in _data.Photos.ToList())
        {
            p.Tags ??= new();
            if (string.IsNullOrEmpty(p.Id) || _byId.ContainsKey(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (_byPath.ContainsKey(p.Path)) { _data.Photos.Remove(p); continue; }
            _byId[p.Id] = p;
            _byPath[p.Path] = p;
        }
        foreach (var a in _data.Albums)
        {
            a.PhotoIds ??= new();
            a.PhotoIds.RemoveAll(id => !_byId.ContainsKey(id));
        }
    }

    public void Save()
    {
        _saveTimer?.Stop();
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, _data, Json);
            File.Move(tmp, FilePath, true);
        }
        catch { /* best effort; the next change saves again */ }
    }

    /// <summary>Saves a moment later, so a burst of changes is written once.</summary>
    public void SaveSoon()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null || !disp.CheckAccess()) { Save(); return; }
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _saveTimer.Tick += (_, _) => Save();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Raise(LibraryChange c)
    {
        SaveSoon();
        Changed?.Invoke(c);
    }

    // ------------------------------------------------------------------ folders

    public static string NormalizeFolder(string path)
    {
        try { path = IOPath.GetFullPath(path); } catch { }
        return path.Length > 3 ? path.TrimEnd('\\', '/') : path;
    }

    public static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        var f = folder.EndsWith('\\') ? folder : folder + "\\";
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsInLibraryFolder(string path) => _data.Folders.Any(f => IsUnder(path, f));

    /// <summary>Adds a folder to the library. Returns false if it (or a folder that contains it) is already there.</summary>
    public bool AddFolder(string folder)
    {
        folder = NormalizeFolder(folder);
        if (_data.Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase) || IsUnder(folder, f))) return false;
        // A parent replaces any of its subfolders that were added separately
        _data.Folders.RemoveAll(f => IsUnder(f, folder));
        _data.Folders.Add(folder);
        Raise(LibraryChange.Folders);
        return true;
    }

    /// <summary>Removes a folder and forgets its photos (the files are not touched).</summary>
    public void RemoveFolder(string folder)
    {
        _data.Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        var gone = _data.Photos.Where(p => IsUnder(p.Path, folder) && !IsInLibraryFolder(p.Path)).ToList();
        RemovePhotosInternal(gone);
        Raise(LibraryChange.Folders | LibraryChange.Photos | LibraryChange.Albums);
    }

    public int CountIn(string folder) => _data.Photos.Count(p => !p.IsTrashed && IsUnder(p.Path, folder));

    // ------------------------------------------------------------------ scanner results

    /// <summary>Adds new photos and refreshes changed ones. Your favorites, tags, captions, rotation and albums are kept.</summary>
    public void Apply(IEnumerable<Photo> found, IEnumerable<string> removedPaths)
    {
        var change = LibraryChange.None;
        foreach (var f in found)
        {
            if (_byPath.TryGetValue(f.Path, out var existing))
            {
                CopyFileFields(f, existing);
                change |= LibraryChange.Details;
            }
            else
            {
                f.Tags ??= new();
                if (_byId.ContainsKey(f.Id)) f.Id = Guid.NewGuid().ToString("N");
                _data.Photos.Add(f);
                _byId[f.Id] = f;
                _byPath[f.Path] = f;
                change |= LibraryChange.Photos;
            }
        }
        if (removedPaths != null)
        {
            var gone = removedPaths.Select(GetByPath).Where(p => p != null).ToList();
            if (gone.Count > 0)
            {
                RemovePhotosInternal(gone);
                change |= LibraryChange.Photos | LibraryChange.Albums;
            }
        }
        if (change != LibraryChange.None) Raise(change);
    }

    private static void CopyFileFields(Photo from, Photo to)
    {
        to.Size = from.Size;
        to.ModifiedTicks = from.ModifiedTicks;
        to.Taken = from.Taken;
        to.TakenSource = from.TakenSource;
        to.Width = from.Width;
        to.Height = from.Height;
        to.Make = from.Make;
        to.Model = from.Model;
        to.Lens = from.Lens;
        to.ExposureTime = from.ExposureTime;
        to.FNumber = from.FNumber;
        to.FocalLength = from.FocalLength;
        to.Iso = from.Iso;
        to.Latitude = from.Latitude;
        to.Longitude = from.Longitude;
        to.Orientation = from.Orientation;
        to.Unreadable = from.Unreadable;
    }

    private void RemovePhotosInternal(List<Photo> gone)
    {
        if (gone.Count == 0) return;
        var ids = new HashSet<string>(gone.Select(p => p.Id));
        _data.Photos.RemoveAll(p => ids.Contains(p.Id));
        foreach (var p in gone) { _byId.Remove(p.Id); _byPath.Remove(p.Path); }
        foreach (var a in _data.Albums)
        {
            a.PhotoIds.RemoveAll(ids.Contains);
            if (a.CoverId != null && ids.Contains(a.CoverId)) a.CoverId = null;
        }
    }

    /// <summary>Changes a photo's path after its file was moved or renamed by Shutterbox.</summary>
    public void Relocate(Photo p, string newPath)
    {
        _byPath.Remove(p.Path);
        p.Path = newPath;
        _byPath[newPath] = p;
        Raise(LibraryChange.Details);
    }

    // ------------------------------------------------------------------ your changes

    public void SetFavorite(IEnumerable<Photo> photos, bool on)
    {
        foreach (var p in photos) p.Favorite = on;
        Raise(LibraryChange.Details);
    }

    public void AddTag(IEnumerable<Photo> photos, string tag)
    {
        tag = (tag ?? "").Trim();
        if (tag.Length == 0) return;
        foreach (var p in photos)
            if (!p.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) p.Tags.Add(tag);
        Raise(LibraryChange.Details);
    }

    public void RemoveTag(IEnumerable<Photo> photos, string tag)
    {
        foreach (var p in photos) p.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        Raise(LibraryChange.Details);
    }

    public void SetCaption(Photo p, string caption)
    {
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (p.Caption == caption) return;
        p.Caption = caption;
        Raise(LibraryChange.Details);
    }

    /// <summary>Rotates by a multiple of 90 degrees (positive = clockwise). Only Shutterbox's view changes, not the file.</summary>
    public void Rotate(IEnumerable<Photo> photos, int degrees)
    {
        foreach (var p in photos) p.Rotation = ((p.Rotation + degrees) % 360 + 360) % 360;
        Raise(LibraryChange.Details);
    }

    public IEnumerable<string> AllTags() =>
        _data.Photos.SelectMany(p => p.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.First());

    // ------------------------------------------------------------------ trash

    public void Trash(IEnumerable<Photo> photos)
    {
        var now = DateTime.Now;
        foreach (var p in photos) p.Trashed ??= now;
        Raise(LibraryChange.Photos);
    }

    public void Restore(IEnumerable<Photo> photos)
    {
        foreach (var p in photos) p.Trashed = null;
        Raise(LibraryChange.Photos);
    }

    /// <summary>
    /// Sends trashed photos' files to the Windows Recycle Bin and forgets them.
    /// Returns how many were deleted and the photos that couldn't be (they stay in Trash).
    /// </summary>
    public (int deleted, List<Photo> failed) EmptyTrash(IEnumerable<Photo> photos = null)
    {
        var list = (photos ?? _data.Photos.Where(p => p.IsTrashed)).Where(p => p.IsTrashed).ToList();
        var done = new List<Photo>();
        var failed = new List<Photo>();
        foreach (var p in list)
        {
            if (!File.Exists(p.Path) || Shell.SendToRecycleBin(p.Path)) done.Add(p);
            else failed.Add(p);
        }
        RemovePhotosInternal(done);
        Raise(LibraryChange.Photos | LibraryChange.Albums);
        return (done.Count, failed);
    }

    // ------------------------------------------------------------------ albums

    public Album CreateAlbum(string name)
    {
        var a = new Album { Name = UniqueAlbumName(name) };
        _data.Albums.Add(a);
        Raise(LibraryChange.Albums);
        return a;
    }

    private string UniqueAlbumName(string name, Album except = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "New album" : name.Trim();
        var n = name;
        for (int i = 2; _data.Albums.Any(a => a != except && string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} ({i})";
        return n;
    }

    public void RenameAlbum(Album a, string name)
    {
        a.Name = UniqueAlbumName(name, a);
        Raise(LibraryChange.Albums);
    }

    public void DeleteAlbum(Album a)
    {
        _data.Albums.Remove(a);
        Raise(LibraryChange.Albums);
    }

    public int AddToAlbum(Album a, IEnumerable<Photo> photos)
    {
        int added = 0;
        var have = new HashSet<string>(a.PhotoIds);
        foreach (var p in photos)
            if (have.Add(p.Id)) { a.PhotoIds.Add(p.Id); added++; }
        Raise(LibraryChange.Albums);
        return added;
    }

    public void RemoveFromAlbum(Album a, IEnumerable<Photo> photos)
    {
        var ids = new HashSet<string>(photos.Select(p => p.Id));
        a.PhotoIds.RemoveAll(ids.Contains);
        if (a.CoverId != null && ids.Contains(a.CoverId)) a.CoverId = null;
        Raise(LibraryChange.Albums);
    }

    public void SetAlbumCover(Album a, Photo p)
    {
        a.CoverId = p?.Id;
        Raise(LibraryChange.Albums);
    }

    public IEnumerable<Photo> PhotosIn(Album a) =>
        a.PhotoIds.Select(Get).Where(p => p != null && !p.IsTrashed);

    public Photo CoverOf(Album a) =>
        Get(a.CoverId) is Photo c && !c.IsTrashed ? c : PhotosIn(a).OrderByDescending(p => p.Taken).FirstOrDefault();

    public IEnumerable<Album> AlbumsOf(Photo p) => _data.Albums.Where(a => a.PhotoIds.Contains(p.Id));
}
