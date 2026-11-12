using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Shutterbox.Library;
using Shutterbox.Services;
using Shutterbox.Views;

namespace Shutterbox;

/// <summary>Things you do to photos: favorites, rotation, albums, Trash, copying, menus, importing and folders.</summary>
public partial class MainWindow
{
    // ================================================================== photo actions

    public void ToggleFavorite(List<Photo> photos)
    {
        if (photos.Count == 0) return;
        bool on = !photos.All(p => p.Favorite);
        Lib.SetFavorite(photos, on);
        if (photos.Count > 1) ShowToast(on ? $"Added {photos.Count:N0} photos to Favorites" : $"Removed {photos.Count:N0} photos from Favorites");
    }

    public void RotatePhotos(List<Photo> photos, int degrees)
    {
        if (photos.Count == 0) return;
        foreach (var p in photos) Thumbnails.Forget(p);
        Lib.Rotate(photos, degrees);
        _infoPhoto = null; // reload the preview
        UpdateInfo();
    }

    public void CopyPhotos(List<Photo> photos)
    {
        if (photos.Count == 0) return;
        if (Shell.CopyFiles(photos.Select(p => p.Path)))
            ShowToast(photos.Count == 1 ? "Copied. Paste it into a folder, email or chat." : $"Copied {photos.Count:N0} photos. Paste them into a folder, email or chat.");
    }

    public async void CopyImage(Photo p)
    {
        var img = await Thumbnails.LoadAsync(p, 0, 0);
        if (img != null && Shell.CopyImage(img)) ShowToast("Copied the picture. Paste it into a document, email or chat.");
        else ShowToast("Couldn't copy this picture.");
    }

    public void TrashPhotos(List<Photo> photos)
    {
        if (photos.Count == 0) return;
        Lib.Trash(photos);
        ClearSelection();
        SelectionChanged();
        ShowToast(photos.Count == 1 ? "Moved to Trash" : $"Moved {photos.Count:N0} photos to Trash", "Undo", () => Lib.Restore(photos));
    }

    public void RestorePhotos(List<Photo> photos)
    {
        if (photos.Count == 0) return;
        Lib.Restore(photos);
        ClearSelection();
        SelectionChanged();
        ShowToast(photos.Count == 1 ? "Restored 1 photo" : $"Restored {photos.Count:N0} photos");
    }

    /// <summary>Sends photos in Trash (all of them when null) to the Windows Recycle Bin.</summary>
    public void EmptyTrash(List<Photo> photos)
    {
        var list = (photos ?? Lib.Photos.Where(p => p.IsTrashed).ToList()).Where(p => p.IsTrashed).ToList();
        if (list.Count == 0) return;
        if (!Confirm(list.Count == 1
                ? $"Delete “{list[0].FileName}” from your PC?\n\nThe file goes to the Windows Recycle Bin."
                : $"Delete these {list.Count:N0} photos from your PC?\n\nThe files go to the Windows Recycle Bin.", "Delete photos")) return;
        var (deleted, failed) = Lib.EmptyTrash(list);
        ClearSelection();
        SelectionChanged();
        ShowToast(failed.Count == 0
            ? (deleted == 1 ? "Moved 1 file to the Recycle Bin" : $"Moved {deleted:N0} files to the Recycle Bin")
            : $"Moved {deleted:N0} files to the Recycle Bin. {failed.Count:N0} couldn't be deleted (in use or read-only) and are still in Trash.");
    }

    public bool Confirm(string message, string title)
    {
        if (App.IsSelfTest) return true;
        return MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    // ================================================================== albums

    private void NewAlbum_Click(object sender, RoutedEventArgs e) => NewAlbum(null, _selected.Count > 0 ? SelectedPhotos : null);

    /// <summary>Asks for a name and makes an album, optionally with photos in it. In the self-test the name is passed in.</summary>
    public Album NewAlbum(string name, List<Photo> photos = null)
    {
        if (name == null)
        {
            name = InputDialog.Ask(this, "New album", "Name", photos?.Count > 0 ? $"An album with the {photos.Count:N0} selected photo{(photos.Count == 1 ? "" : "s")}." : null, "");
            if (name == null) return null;
        }
        var a = Lib.CreateAlbum(name);
        if (photos?.Count > 0) Lib.AddToAlbum(a, photos);
        ShowToast(photos?.Count > 0 ? $"Made “{a.Name}” with {photos.Count:N0} photo{(photos.Count == 1 ? "" : "s")}" : $"Made “{a.Name}”. Drag photos onto it in the sidebar.",
            "Open", () => ShowView(LibraryView.Album, albumId: a.Id));
        return a;
    }

    public void AddToAlbum(Album a, List<Photo> photos)
    {
        if (photos.Count == 0) return;
        int added = Lib.AddToAlbum(a, photos);
        ShowToast(added == 0 ? $"Already in “{a.Name}”" : $"Added {added:N0} photo{(added == 1 ? "" : "s")} to “{a.Name}”", "Open",
            () => ShowView(LibraryView.Album, albumId: a.Id));
    }

    public void RemoveFromAlbum(Album a, List<Photo> photos)
    {
        if (photos.Count == 0) return;
        Lib.RemoveFromAlbum(a, photos);
        ClearSelection();
        SelectionChanged();
        ShowToast($"Removed {photos.Count:N0} photo{(photos.Count == 1 ? "" : "s")} from “{a.Name}”. The photos are still in your library.", "Undo",
            () => Lib.AddToAlbum(a, photos));
    }

    public void RenameAlbum(Album a)
    {
        var name = InputDialog.Ask(this, "Rename album", "Name", null, a.Name);
        if (name == null) return;
        Lib.RenameAlbum(a, name);
        UpdateHeader();
    }

    public void DeleteAlbum(Album a)
    {
        if (!Confirm($"Delete the album “{a.Name}”?\n\nThe photos stay in your library; only the album goes.", "Delete album")) return;
        Lib.DeleteAlbum(a);
        if (_view == LibraryView.Album && _albumId == a.Id) ShowView(LibraryView.Albums);
        ShowToast($"Deleted the album “{a.Name}”");
    }

    private void ShowAlbumMenu(Album a, FrameworkElement target)
    {
        var m = NewMenu(target);
        Item(m, "", "Open", () => ShowView(LibraryView.Album, albumId: a.Id));
        Item(m, "", "Slideshow", () => StartSlideshow(Lib.PhotosIn(a).OrderBy(p => p.Taken).ToList()), Lib.PhotosIn(a).Any());
        Item(m, "", "Rename…", () => RenameAlbum(a));
        m.Items.Add(new Separator());
        Item(m, "", "Delete album", () => DeleteAlbum(a));
        m.IsOpen = true;
    }

    private void ShowAddToAlbumMenu(FrameworkElement target, List<Photo> photos)
    {
        var m = NewMenu(target);
        FillAlbumItems(m.Items, photos);
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    private void FillAlbumItems(ItemCollection items, List<Photo> photos)
    {
        foreach (var a in Lib.Albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var album = a;
            var mi = new MenuItem { Header = a.Name, Icon = Glyph("") };
            mi.Click += (_, _) => AddToAlbum(album, photos);
            items.Add(mi);
        }
        if (Lib.Albums.Count > 0) items.Add(new Separator());
        var add = new MenuItem { Header = "New album…", Icon = Glyph("") };
        add.Click += (_, _) => NewAlbum(null, photos);
        items.Add(add);
    }

    // ================================================================== menus

    private ContextMenu NewMenu(FrameworkElement target) => new() { PlacementTarget = target, Placement = PlacementMode.MousePoint };

    private TextBlock Glyph(string g) => new() { Text = g, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13 };

    private MenuItem Item(ItemsControl menu, string glyph, string text, Action a, bool enabled = true, string gesture = null)
    {
        var mi = new MenuItem { Header = text, Icon = glyph != null ? Glyph(glyph) : null, IsEnabled = enabled, InputGestureText = gesture ?? "" };
        mi.Click += (_, _) => a();
        menu.Items.Add(mi);
        return mi;
    }

    private void ShowPhotoMenu(List<Photo> photos, FrameworkElement target)
    {
        if (photos.Count == 0) return;
        var m = NewMenu(target);
        var one = photos.Count == 1 ? photos[0] : null;
        if (_view == LibraryView.Trash)
        {
            Item(m, "", photos.Count == 1 ? "Restore" : $"Restore {photos.Count:N0} photos", () => RestorePhotos(photos));
            Item(m, "", "Delete from PC…", () => EmptyTrash(photos), true, "Delete");
            if (one != null) { m.Items.Add(new Separator()); Item(m, "", "Show in File Explorer", () => Shell.ShowInExplorer(one.Path)); }
            m.IsOpen = true;
            return;
        }
        if (one != null) Item(m, "", "Open", () => OpenViewer(one), true, "Enter");
        bool allFav = photos.All(p => p.Favorite);
        Item(m, allFav ? "" : "", allFav ? "Remove from Favorites" : "Add to Favorites", () => ToggleFavorite(photos), true, "F");
        var albums = new MenuItem { Header = "Add to album", Icon = Glyph("") };
        FillAlbumItems(albums.Items, photos);
        m.Items.Add(albums);
        if (_view == LibraryView.Album && Lib.GetAlbum(_albumId) is Album current)
        {
            Item(m, "", "Remove from this album", () => RemoveFromAlbum(current, photos));
            if (one != null) Item(m, "", "Use as album cover", () => { Lib.SetAlbumCover(current, one); ShowToast($"“{one.FileName}” is now the cover of “{current.Name}”"); });
        }
        m.Items.Add(new Separator());
        Item(m, "", "Rotate right", () => RotatePhotos(photos, 90), true, "R");
        Item(m, "", "Rotate left", () => RotatePhotos(photos, -90), true, "Shift+R");
        m.Items.Add(new Separator());
        Item(m, "", photos.Count == 1 ? "Copy" : $"Copy {photos.Count:N0} photos", () => CopyPhotos(photos), true, "Ctrl+C");
        if (one != null)
        {
            Item(m, "", "Copy picture", () => CopyImage(one));
            Item(m, "", "Edit in your photo editor", () => Shell.Edit(one.Path));
            Item(m, "", "Open with…", () => Shell.OpenWith(one.Path));
            Item(m, "", "Show in File Explorer", () => Shell.ShowInExplorer(one.Path));
            Item(m, "", "Set as desktop background", () => ShowToast(Shell.SetWallpaper(one.Path) ? "Desktop background changed" : "Couldn't set the desktop background"));
        }
        Item(m, "", "Slideshow", () => StartSlideshow(photos.Count > 1 ? photos : null), true, "F5");
        m.Items.Add(new Separator());
        Item(m, "", "Move to Trash", () => TrashPhotos(photos), true, "Delete");
        m.IsOpen = true;
    }

    private void ShowShareMenu(List<Photo> photos, FrameworkElement target)
    {
        if (photos.Count == 0) return;
        var one = photos[0];
        var m = NewMenu(target);
        m.Placement = PlacementMode.Bottom;
        Item(m, "", "Copy file", () => CopyPhotos(photos));
        Item(m, "", "Copy picture", () => CopyImage(one));
        Item(m, "", "Show in File Explorer", () => Shell.ShowInExplorer(one.Path));
        Item(m, "", "Open with…", () => Shell.OpenWith(one.Path));
        Item(m, "", "Set as desktop background", () => ShowToast(Shell.SetWallpaper(one.Path) ? "Desktop background changed" : "Couldn't set the desktop background"));
        m.IsOpen = true;
    }

    private void ShowFolderMenu(string folder, FrameworkElement target)
    {
        var m = NewMenu(target);
        Item(m, "", "Open", () => ShowView(LibraryView.Folder, folder: folder));
        Item(m, "", "Show in File Explorer", () => Shell.ShowInExplorer(folder));
        Item(m, "", "Rescan", () => _ = StartScan(new[] { folder }));
        m.Items.Add(new Separator());
        Item(m, "", "Remove from library…", () => RemoveFolder(folder));
        m.IsOpen = true;
    }

    // ================================================================== folders

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder with photos", Multiselect = true };
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pics)) dlg.InitialDirectory = pics;
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames) _ = AddFolderToLibrary(f);
    }

    private void AddPictures_Click(object sender, RoutedEventArgs e)
    {
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pics)) _ = AddFolderToLibrary(pics);
        else AddFolder_Click(sender, e);
    }

    /// <summary>Adds a folder and scans it. The task finishes when the scan is done.</summary>
    public Task AddFolderToLibrary(string folder)
    {
        folder = LibraryStore.NormalizeFolder(folder);
        if (!Directory.Exists(folder)) { ShowToast("That folder doesn't exist."); return Task.CompletedTask; }
        if (!Lib.AddFolder(folder))
        {
            ShowToast("That folder is already in your library.");
            return Task.CompletedTask;
        }
        SetupWatchers();
        RefreshSidebar();
        if (_view is LibraryView.Settings) _settingsPage?.Refresh();
        else if (_view is not LibraryView.Photos) ShowView(LibraryView.Photos);
        ShowToast($"Looking for photos in {ShortPath(folder)}…");
        return StartScan(new[] { folder });
    }

    public void RemoveFolder(string folder)
    {
        int n = Lib.CountIn(folder);
        if (!Confirm($"Remove {folder} from your library?\n\nShutterbox forgets its {n:N0} photos (with their tags and albums). The files on your PC aren't touched.", "Remove folder")) return;
        Lib.RemoveFolder(folder);
        _offline.RemoveAll(o => string.Equals(o, folder, StringComparison.OrdinalIgnoreCase));
        SetupWatchers();
        if (_view == LibraryView.Folder && string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase)) ShowView(LibraryView.Photos);
        RefreshSidebar();
        _settingsPage?.Refresh();
        ShowToast($"Removed {ShortPath(folder)} from your library");
    }

    // ================================================================== import

    private void Import_Click(object sender, RoutedEventArgs e) => OpenImport(null);

    public void OpenImport(IEnumerable<string> sources)
    {
        var dlg = new ImportDialog(this, sources) { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>Called by the import dialog after copying: makes sure the photos are in the library and shows them.</summary>
    public async Task AfterImportAsync(string dest, ImportResult result)
    {
        if (result.Copied > 0)
        {
            if (!Lib.IsInLibraryFolder(IOPath.Combine(dest, "x")) && !Lib.Folders.Contains(LibraryStore.NormalizeFolder(dest), StringComparer.OrdinalIgnoreCase))
            {
                Lib.AddFolder(dest);
                SetupWatchers();
            }
            await StartScan(new[] { LibraryStore.NormalizeFolder(dest) });
            ShowView(LibraryView.Recent);
        }
        var msg = result.Copied == 1 ? "Imported 1 photo" : $"Imported {result.Copied:N0} photos";
        if (result.Skipped > 0) msg += $", skipped {result.Skipped:N0} you already have";
        if (result.Failed > 0) msg += $", {result.Failed:N0} couldn't be copied";
        ShowToast(msg + ".");
    }

    // ================================================================== drag and drop

    private List<Photo> DraggedPhotos(DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ShutterboxPhotoIds") && e.Data.GetData("ShutterboxPhotoIds") is string[] ids)
            return ids.Select(Lib.Get).Where(p => p != null).ToList();
        return new List<Photo>();
    }

    private void AlbumTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ShutterboxPhotoIds") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void AlbumNav_Drop(object sender, DragEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag || !tag.StartsWith("album:")) return;
        var a = Lib.GetAlbum(tag[6..]);
        var photos = DraggedPhotos(e);
        if (a != null && photos.Count > 0) AddToAlbum(a, photos);
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ShutterboxPhotoIds")) e.Effects = DragDropEffects.None;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ShutterboxPhotoIds")) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            e.Handled = true;
            // dropped from File Explorer: offer to import (or add the folder)
            Dispatcher.BeginInvoke(new Action(() => OpenImport(paths)));
        }
    }
}
