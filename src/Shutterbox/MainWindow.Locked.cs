using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Shutterbox.Library;
using Shutterbox.Services;
using Shutterbox.Views;

namespace Shutterbox;

/// <summary>The Locked album: setting it up, unlocking (PIN, password, Windows Hello, recovery key), adding, restoring, auto-lock.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _autoLockTimer;
    /// <summary>Photos waiting to be locked once the album is unlocked or set up.</summary>
    private List<Photo> _pendingLock;
    private bool _pendingMove;
    private string _pendingAlbum;
    /// <summary>The album shown inside the Locked album (null = all of it).</summary>
    private string _lockedAlbumId;
    private bool _gateRecovery;

    // ================================================================== state changes

    private void OnVaultChanged()
    {
        if (!Vault.IsUnlocked || (_lockedAlbumId != null && Vault.GetAlbum(_lockedAlbumId) == null)) _lockedAlbumId = null;
        RefreshSidebar();
        if (!Vault.IsUnlocked && ViewerOpen && _viewPhoto?.IsLocked == true) CloseViewer();
        if (_view == LibraryView.Locked) { UpdateHeaderControls(); RebuildView(true); }
        _settingsPage?.Refresh();
    }

    private void CheckAutoLock()
    {
        if (!Vault.IsUnlocked || Vault.IsBusy || App.IsSelfTest) return;
        int minutes = Vault.Info?.AutoLockMinutes ?? 0;
        if (minutes <= 0) return;
        if (_videoPlaying && _viewPhoto?.IsLocked == true) { Vault.LastActivity = DateTime.Now; return; }
        if (DateTime.Now - Vault.LastActivity >= TimeSpan.FromMinutes(minutes))
        {
            Vault.Lock();
            ShowToast("The Locked album locked itself after " + (minutes == 1 ? "a minute" : $"{minutes} minutes") + " without use.");
        }
    }

    public void LockNow()
    {
        if (ViewerOpen && _viewPhoto?.IsLocked == true) CloseViewer();
        Vault.Lock();
        if (Vault.IsBusy) ShowToast("The Locked album locks as soon as Shutterbox finishes.");
    }

    // ================================================================== the gate (set up / unlock)

    private void ShowGate()
    {
        LockedGate.Visibility = Visibility.Visible;
        bool setUp = Vault.IsSetUp;
        GateSetup.Visibility = setUp ? Visibility.Collapsed : Visibility.Visible;
        GateUnlock.Visibility = setUp && !_gateRecovery ? Visibility.Visible : Visibility.Collapsed;
        GateRecovery.Visibility = setUp && _gateRecovery ? Visibility.Visible : Visibility.Collapsed;
        GateTitle.Text = setUp ? "Locked album" : "Keep photos and videos to yourself";
        var kind = Vault.Info?.Kind ?? "PIN";
        GateKindLabel.Text = kind == "PIN" ? "PIN" : "Password";
        GateText.Text = setUp
            ? (_pendingLock?.Count > 0
                ? $"Unlock to add the {_pendingLock.Count:N0} item{(_pendingLock.Count == 1 ? "" : "s")} you chose."
                : $"Enter your {(kind == "PIN" ? "PIN" : "password")} to see what's inside.")
            : "Copy photos and videos into an encrypted album that can only be opened in Shutterbox, with a PIN, a password or Windows Hello. " +
              "Your originals aren't touched unless you choose to delete them.";
        GateHelloBtn.Visibility = WindowsHello.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (setUp && !_gateRecovery) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (LockedGate.IsVisible) GatePassword.Focus(); }));
    }

    private void GateError_Show(string message)
    {
        GateError.Text = message;
        GateError.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GateSetup_Click(object sender, RoutedEventArgs e) => SetUpLocked();

    public void SetUpLocked()
    {
        if (Vault.IsSetUp) return;
        var dlg = new LockSetupDialog(LockSetupDialog.Mode.SetUp) { Owner = this };
        dlg.ShowDialog();
        if (Vault.IsUnlocked) AfterUnlock();
    }

    private void GatePassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GateUnlock_Click(null, null); e.Handled = true; }
    }

    /// <summary>How long until another guess is allowed on this PC (after 5 wrong ones in a row: 30 seconds, doubling up to 15 minutes).</summary>
    private static TimeSpan WaitLeft
    {
        get
        {
            var until = SettingsService.Current.WaitUntil;
            return until == null || until <= DateTime.Now ? TimeSpan.Zero : until.Value - DateTime.Now;
        }
    }

    private static TimeSpan WaitAfter(int wrong) =>
        wrong < 5 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(900, 30 * (1 << Math.Min(5, wrong - 5))));

    private void GuessFailed()
    {
        var s = SettingsService.Current;
        s.WrongInARow++;
        var wait = WaitAfter(s.WrongInARow);
        s.WaitUntil = wait == TimeSpan.Zero ? null : DateTime.Now + wait;
        SettingsService.Save();
        if (Vault.PhotographsWrongGuesses && !App.IsSelfTest) _ = Webcam.TakeAndSaveIntruderPhotoAsync();
    }

    private static void GuessSucceeded()
    {
        var s = SettingsService.Current;
        if (s.WrongInARow == 0 && s.WaitUntil == null) return;
        s.WrongInARow = 0;
        s.WaitUntil = null;
        SettingsService.Save();
    }

    private async void GateUnlock_Click(object sender, RoutedEventArgs e)
    {
        var secret = GatePassword.Password;
        if (secret.Length == 0) { GatePassword.Focus(); return; }
        if (WaitLeft > TimeSpan.Zero)
        {
            GateError_Show($"Too many wrong guesses. Try again in {(int)WaitLeft.TotalMinutes}:{WaitLeft.Seconds:00}.");
            return;
        }
        GateError_Show(null);
        IsEnabled = false;
        bool ok;
        try { ok = await Task.Run(() => Vault.Unlock(secret)); }
        finally { IsEnabled = true; }
        GatePassword.Clear();
        if (!ok)
        {
            GuessFailed();
            GateError_Show((Vault.Info?.Kind == "PIN" ? "That PIN isn't right." : "That password isn't right.") +
                           (WaitLeft > TimeSpan.Zero ? $" Wait {(int)WaitLeft.TotalMinutes}:{WaitLeft.Seconds:00} before trying again." : ""));
            GatePassword.Focus();
            return;
        }
        GuessSucceeded();
        AfterUnlock();
    }

    private async void GateHello_Click(object sender, RoutedEventArgs e)
    {
        GateError_Show(null);
        GateHelloBtn.IsEnabled = false;
        try
        {
            var problem = await WindowsHello.UnlockAsync();
            Activate();
            if (problem == null) { AfterUnlock(); return; }
            if (problem != "Canceled.") GateError_Show(problem);
            GateHelloBtn.Visibility = WindowsHello.IsOn ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { GateHelloBtn.IsEnabled = true; }
    }

    private void GateForgot_Click(object sender, RoutedEventArgs e) { _gateRecovery = true; GateError_Show(null); ShowGate(); GateRecoveryBox.Focus(); }
    private void GateBack_Click(object sender, RoutedEventArgs e) { _gateRecovery = false; GateError_Show(null); ShowGate(); }

    private async void GateRecovery_Click(object sender, RoutedEventArgs e)
    {
        var key = GateRecoveryBox.Text;
        GateError_Show(null);
        IsEnabled = false;
        bool ok;
        try { ok = await Task.Run(() => Vault.UnlockWithRecoveryKey(key)); }
        finally { IsEnabled = true; }
        if (!ok) { GuessFailed(); GateError_Show("That recovery key isn't right. It's the 25 letters and numbers shown when you set up the Locked album."); return; }
        GuessSucceeded();
        GateRecoveryBox.Text = "";
        _gateRecovery = false;
        AfterUnlock(silent: true);
        // A forgotten PIN or password: choose a new one now
        var dlg = new LockSetupDialog(LockSetupDialog.Mode.Change) { Owner = this };
        dlg.ShowDialog();
    }

    private async void AfterUnlock(bool silent = false)
    {
        Vault.LastActivity = DateTime.Now;
        var warn = Vault.LastUnlockWarnings;
        if (warn.Count > 0)
            ShowToast($"{warn.Count} wrong {(warn.Count == 1 ? "guess" : "guesses")} since you last opened it. The last: {warn[0].Time:dddd h:mm tt} on {warn[0].Device}.",
                      "See", () => new WrongGuessesWindow { Owner = this }.ShowDialog());
        if (_view != LibraryView.Locked) ShowView(LibraryView.Locked);
        else RebuildView(false);
        var pending = _pendingLock;
        _pendingLock = null;
        if (pending?.Count > 0) await LockPhotosAsync(pending, _pendingMove, _pendingAlbum);
    }

    // ================================================================== adding

    /// <summary>
    /// Copies (or moves) photos and videos into the Locked album, optionally into one of its albums.
    /// Asks to unlock or set it up first.
    /// </summary>
    public async void AddToLocked(List<Photo> photos, bool move = false, string albumId = null)
    {
        photos = photos.Where(p => !p.IsLocked).ToList();
        if (photos.Count == 0) return;
        if (!Vault.IsSetUp || !Vault.IsUnlocked)
        {
            _pendingLock = photos;
            _pendingMove = move;
            _pendingAlbum = albumId;
            ShowView(LibraryView.Locked);
            if (!Vault.IsSetUp) SetUpLocked();
            return;
        }
        await LockPhotosAsync(photos, move, albumId);
    }

    /// <summary>The actual copy. The self-test calls it directly.</summary>
    public async Task<(int added, int skipped, List<string> errors)> LockPhotosAsync(List<Photo> photos, bool move = false, string albumId = null)
    {
        if (move && !Confirm($"Move {(photos.Count == 1 ? "this item" : $"these {photos.Count:N0} items")} into the Locked album?\n\n" +
                             "Each one is encrypted and checked, then the original is deleted from this PC. It doesn't go to the Recycle Bin, " +
                             "so no unlocked copy is left behind. You can restore them from the Locked album any time.", "Move to Locked album"))
            return (0, 0, new List<string>());
        var album = Vault.GetAlbum(albumId);
        var verb = move ? "Moving" : "Encrypting";
        var progress = new Progress<(int done, int total)>(p =>
            ShowToast(p.total == 1 ? $"{verb} into the Locked album…" : $"{verb} into the Locked album: {p.done:N0} of {p.total:N0}…"));
        var r = await Vault.AddAsync(photos, progress, albumId, verify: move);
        string where = album != null ? $"“{album.Name}” in the Locked album" : "the Locked album";
        if (move)
        {
            // only originals whose encrypted copy checked out are deleted
            var inLibrary = r.secured.Where(p => Lib.Get(p.Id) == p).ToList();
            var (deleted, failed) = Lib.DeleteForGood(inLibrary);
            foreach (var p in r.secured.Except(inLibrary))
                try { File.Delete(p.Path); deleted++; } catch { failed.Add(p); }
            string m = $"Moved {r.added:N0} item{(r.added == 1 ? "" : "s")} into {where}";
            if (r.skipped > 0) m += $". {r.skipped:N0} {(r.skipped == 1 ? "was" : "were")} already there, so {(r.skipped == 1 ? "its original was" : "their originals were")} left alone";
            if (failed.Count > 0) m += $". {failed.Count:N0} original{(failed.Count == 1 ? "" : "s")} couldn't be deleted (in use?)";
            if (r.errors.Count > 0) m += $". {r.errors.Count:N0} couldn't be moved and {(r.errors.Count == 1 ? "is" : "are")} untouched: {r.errors[0]}";
            ShowToast(m + ".");
            return (r.added, r.skipped, r.errors);
        }
        var originals = photos.Where(p => !p.IsLocked && Lib.Get(p.Id) != null).ToList();
        string msg = (r.added == 1 ? "Copied 1 item" : $"Copied {r.added:N0} items") + $" into {where}";
        if (r.skipped > 0) msg += $" ({r.skipped:N0} already there)";
        if (r.errors.Count > 0) msg += $". {r.errors.Count:N0} couldn't be locked: {r.errors[0]}";
        if (r.added > 0 && originals.Count > 0)
            ShowToast(msg + ". The originals are still in your library.", "Delete originals", () => DeleteOriginals(originals));
        else ShowToast(msg + ".");
        return (r.added, r.skipped, r.errors);
    }

    /// <summary>After locking: sends the originals to the Recycle Bin and takes them out of the library.</summary>
    public void DeleteOriginals(List<Photo> originals)
    {
        if (!Confirm($"Move the {originals.Count:N0} original{(originals.Count == 1 ? "" : "s")} to the Windows Recycle Bin?\n\n" +
                     "The encrypted copies stay in the Locked album. Empty the Recycle Bin afterwards so nothing is left behind.", "Delete originals")) return;
        Lib.Trash(originals);
        var (deleted, failed) = Lib.EmptyTrash(originals);
        if (failed.Count > 0) Lib.Restore(failed);
        ShowToast(failed.Count == 0 ? $"Moved {deleted:N0} original{(deleted == 1 ? "" : "s")} to the Recycle Bin"
                                    : $"Moved {deleted:N0} to the Recycle Bin; {failed.Count:N0} couldn't be deleted");
    }

    private void AddFilesToLocked()
    {
        var exts = string.Join(";", MetadataReader.Extensions.Concat(MetadataReader.VideoExtensions).Select(x => "*" + x));
        var dlg = new OpenFileDialog
        {
            Title = "Add photos and videos to the Locked album",
            Multiselect = true,
            Filter = $"Photos and videos|{exts}|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        var photos = dlg.FileNames.Where(File.Exists).Select(f => Lib.GetByPath(f) ?? MetadataReader.Read(f)).ToList();
        AddToLocked(photos, false, _lockedAlbumId);
    }

    private void LockedNav_Drop(object sender, DragEventArgs e)
    {
        var photos = DraggedPhotos(e);
        e.Handled = true;
        if (photos.Count > 0) AddToLocked(photos);
    }

    // ================================================================== restoring and deleting

    public async void RestoreFromLocked(List<Photo> photos)
    {
        photos = photos.Where(p => p.IsLocked).ToList();
        if (photos.Count == 0) return;
        var dlg = new OpenFolderDialog { Title = "Restore to which folder?" };
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pics)) dlg.InitialDirectory = pics;
        if (dlg.ShowDialog(this) != true) return;
        await RestoreFromLockedAsync(photos, dlg.FolderName);
    }

    public async Task<int> RestoreFromLockedAsync(List<Photo> photos, string folder)
    {
        if (ViewerOpen) CloseViewer();
        var (restored, errors) = await Vault.RestoreAsync(photos, folder);
        ClearSelection();
        SelectionChanged();
        // if the folder is in the library, the restored files show up there
        if (Lib.IsInLibraryFolder(IOPath.Combine(folder, "x")) || Lib.Folders.Contains(LibraryStore.NormalizeFolder(folder), StringComparer.OrdinalIgnoreCase))
            _ = StartScan(new[] { LibraryStore.NormalizeFolder(folder) });
        ShowToast((restored == 1 ? "Restored 1 item" : $"Restored {restored:N0} items") + $" to {ShortPath(folder)}" +
                  (errors.Count > 0 ? $". {errors.Count:N0} couldn't be restored: {errors[0]}" : "."), "Show", () => Shell.ShowInExplorer(folder));
        return restored;
    }

    public void DeleteFromLocked(List<Photo> photos)
    {
        photos = photos.Where(p => p.IsLocked).ToList();
        if (photos.Count == 0) return;
        if (!Confirm((photos.Count == 1 ? $"Delete “{photos[0].FileName}” from the Locked album?" : $"Delete these {photos.Count:N0} items from the Locked album?") +
                     "\n\nThey're deleted for good: encrypted items don't go to the Recycle Bin. Restore them first to keep a copy.", "Delete from Locked album")) return;
        if (ViewerOpen) CloseViewer();
        Vault.Remove(photos);
        ClearSelection();
        SelectionChanged();
        ShowToast(photos.Count == 1 ? "Deleted 1 item from the Locked album" : $"Deleted {photos.Count:N0} items from the Locked album");
    }

    // ================================================================== albums inside the Locked album

    /// <summary>The tabs above the Locked album: All, each album, and New album.</summary>
    private void BuildLockedAlbumBar()
    {
        LockedAlbumBar.Children.Clear();
        RadioButton Tab(string text, string id, int count)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("ChoicePill"),
                GroupName = "lockedAlbums",
                Content = count >= 0 ? $"{text}  ·  {count:N0}" : text,
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = _lockedAlbumId == id,
                Tag = id,
            };
            rb.Checked += (_, _) =>
            {
                if (_lockedAlbumId == id) return;
                var target = Vault.GetAlbum(id);
                if (target is { IsProtected: true, IsOpen: false } &&
                    !SecretPrompt.Ask(this, $"“{target.Name}” has its own {(target.Protected.Kind == "PIN" ? "PIN" : "password")}",
                        "Enter it to see what's inside.", s => Vault.UnlockAlbum(target, s)))
                {
                    Dispatcher.BeginInvoke(new Action(() => RebuildView(true)));
                    return;
                }
                _lockedAlbumId = id;
                ClearSelection();
                RebuildView(false);
            };
            return rb;
        }
        LockedAlbumBar.Children.Add(Tab("All", null, Vault.Count));
        foreach (var a in Vault.Albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var tab = a.IsProtected && !a.IsOpen ? Tab(a.Name + "  ·  locked", a.Id, -1) : Tab(a.Name, a.Id, Vault.PhotosIn(a).Count());
            var album = a;
            tab.MouseRightButtonUp += (s, e) => { ShowLockedAlbumOptions(album, (FrameworkElement)s); e.Handled = true; };
            tab.ToolTip = "Right-click to rename or delete";
            LockedAlbumBar.Children.Add(tab);
        }
        var add = new Button { Style = (Style)FindResource("ToolButton"), Height = 34, Margin = new Thickness(0, 0, 8, 8), ToolTip = "A new album inside the Locked album" };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = "\uE710", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "New album", VerticalAlignment = VerticalAlignment.Center });
        add.Content = sp;
        add.Click += (_, _) => NewLockedAlbum(null, _selected.Count > 0 ? SelectedPhotos : null);
        LockedAlbumBar.Children.Add(add);
        if (Vault.GetAlbum(_lockedAlbumId) is VaultAlbum current)
        {
            var more = new Button { Style = (Style)FindResource("ToolButton"), Height = 34, Margin = new Thickness(0, 0, 8, 8), Content = "Rename or delete…" };
            more.Click += (s, _) => ShowLockedAlbumOptions(current, (FrameworkElement)s);
            LockedAlbumBar.Children.Add(more);
        }
        LockedAlbumBar.Visibility = Visibility.Visible;
    }

    /// <summary>Makes an album inside the Locked album (asks for a name unless one is given) and opens it.</summary>
    public VaultAlbum NewLockedAlbum(string name, List<Photo> photos = null)
    {
        if (!Vault.IsUnlocked) return null;
        if (name == null)
        {
            name = InputDialog.Ask(this, "New locked album", "Name", "Only visible while the Locked album is unlocked; its name is encrypted too.", "");
            if (name == null) return null;
        }
        var a = Vault.CreateAlbum(name);
        if (photos?.Count > 0 && photos.All(p => p.IsLocked)) Vault.AddToAlbum(a, photos);
        _lockedAlbumId = a.Id;
        if (_view == LibraryView.Locked) RebuildView(false);
        return a;
    }

    private void ShowLockedAlbumOptions(VaultAlbum a, FrameworkElement target)
    {
        var m = NewMenu(target);
        Item(m, "\uE8AC", "Rename…", () =>
        {
            var name = InputDialog.Ask(this, "Rename album", "Name", null, a.Name);
            if (name != null) { Vault.RenameAlbum(a, name); RebuildView(true); }
        });
        if (!a.IsProtected)
            Item(m, "\uE72E", "Give it its own PIN…", () =>
            {
                var dlg = new LockSetupDialog(LockSetupDialog.Mode.Album, a.Name) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                Vault.ProtectAlbum(a, dlg.Secret, dlg.SecretKind);
                ShowToast($"“{a.Name}” now has its own {(dlg.SecretKind == "PIN" ? "PIN" : "password")}.");
            });
        else if (a.IsOpen)
        {
            Item(m, "\uE72E", "Lock this album", () => { if (_lockedAlbumId == a.Id) _lockedAlbumId = null; Vault.LockAlbum(a); });
            Item(m, "\uE8D7", "Change its PIN…", () =>
            {
                var dlg = new LockSetupDialog(LockSetupDialog.Mode.Album, a.Name) { Owner = this };
                if (dlg.ShowDialog() == true) { Vault.ChangeAlbumSecret(a, dlg.Secret, dlg.SecretKind); ShowToast("Changed."); }
            });
            Item(m, "\uE785", "Take its PIN off", () =>
            {
                if (Confirm($"Take the PIN off “{a.Name}”?\n\nIts items are re-encrypted with the Locked album's key and open whenever the Locked album does.", "Take it off"))
                    Vault.UnprotectAlbum(a);
            });
        }
        if (!a.IsProtected || a.IsOpen)
            Item(m, "\uE74D", "Delete album", () =>
            {
                if (!Confirm($"Delete the album “{a.Name}”?\n\nIts photos and videos stay in the Locked album (under All).", "Delete album")) return;
                if (_lockedAlbumId == a.Id) _lockedAlbumId = null;
                Vault.DeleteAlbum(a);
            });
        m.IsOpen = true;
    }

    /// <summary>Add locked items to one of the Locked album's albums.</summary>
    private void ShowLockedAlbumMenu(FrameworkElement target, List<Photo> photos)
    {
        var m = NewMenu(target);
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        foreach (var a in Vault.Albums.Where(a => !a.IsProtected || a.IsOpen))
        {
            var album = a;
            Item(m, "\uE8B9", a.IsProtected ? a.Name + " (moves them in)" : a.Name, () =>
            {
                int n = Vault.AddToAlbum(album, photos.Where(p => p.ProtectedIn == null));
                ShowToast(n == 0 ? $"Already in “{album.Name}”" : $"{(album.IsProtected ? "Moved" : "Added")} {n:N0} to “{album.Name}”");
            });
        }
        if (Vault.Albums.Count > 0) m.Items.Add(new Separator());
        Item(m, "\uE710", "New album…", () => NewLockedAlbum(null, photos));
        m.IsOpen = true;
    }

    /// <summary>The Lock menu: copy or move, into the Locked album or one of its albums.</summary>
    private void ShowLockMenu(FrameworkElement target, List<Photo> photos)
    {
        var m = NewMenu(target);
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        FillLockMenu(m, photos);
        m.IsOpen = true;
    }

    /// <summary>Copy/Move to Locked album items (with a submenu of its albums when it's unlocked and has some).</summary>
    private void FillLockMenu(ItemsControl menu, List<Photo> photos)
    {
        void Add(string glyph, string text, bool move, string tip)
        {
            var albums = Vault.IsUnlocked ? Vault.Albums.Where(a => !a.IsProtected || a.IsOpen).ToList() : new List<VaultAlbum>();
            if (albums.Count == 0)
            {
                var mi = Item(menu, glyph, text, () => AddToLocked(photos, move));
                mi.ToolTip = tip;
                return;
            }
            var parent = new MenuItem { Header = text, Icon = Glyph(glyph), ToolTip = tip };
            Item(parent, "\uE72E", "Locked album (All)", () => AddToLocked(photos, move));
            parent.Items.Add(new Separator());
            foreach (var a in albums)
            {
                var id = a.Id;
                Item(parent, "\uE8B9", a.Name, () => AddToLocked(photos, move, id));
            }
            menu.Items.Add(parent);
        }
        Add("\uE8C8", "Copy to Locked album", false, "Encrypted copies go in the Locked album; the originals stay where they are");
        Add("\uE72E", "Move to Locked album", true, "Encrypted, checked, then the originals are deleted from this PC");
    }
}
