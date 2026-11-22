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
    private bool _gateRecovery;

    // ================================================================== state changes

    private void OnVaultChanged()
    {
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

    private async void GateUnlock_Click(object sender, RoutedEventArgs e)
    {
        var secret = GatePassword.Password;
        if (secret.Length == 0) { GatePassword.Focus(); return; }
        GateError_Show(null);
        IsEnabled = false;
        bool ok;
        try { ok = await Task.Run(() => Vault.Unlock(secret)); }
        finally { IsEnabled = true; }
        GatePassword.Clear();
        if (!ok)
        {
            GateError_Show(Vault.Info?.Kind == "PIN" ? "That PIN isn't right." : "That password isn't right.");
            GatePassword.Focus();
            return;
        }
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
        if (!ok) { GateError_Show("That recovery key isn't right. It's the 25 letters and numbers shown when you set up the Locked album."); return; }
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
        if (_view != LibraryView.Locked) ShowView(LibraryView.Locked);
        else RebuildView(false);
        var pending = _pendingLock;
        _pendingLock = null;
        if (pending?.Count > 0) await LockPhotosAsync(pending);
    }

    // ================================================================== adding

    /// <summary>Copies photos and videos into the Locked album (asks to unlock or set it up first).</summary>
    public async void AddToLocked(List<Photo> photos)
    {
        photos = photos.Where(p => !p.IsLocked).ToList();
        if (photos.Count == 0) return;
        if (!Vault.IsSetUp || !Vault.IsUnlocked)
        {
            _pendingLock = photos;
            ShowView(LibraryView.Locked);
            if (!Vault.IsSetUp) SetUpLocked();
            return;
        }
        await LockPhotosAsync(photos);
    }

    /// <summary>The actual copy. The self-test calls it directly.</summary>
    public async Task<(int added, int skipped, List<string> errors)> LockPhotosAsync(List<Photo> photos)
    {
        var progress = new Progress<(int done, int total)>(p =>
            ShowToast(p.total == 1 ? "Encrypting into the Locked album…" : $"Encrypting into the Locked album: {p.done:N0} of {p.total:N0}…"));
        var r = await Vault.AddAsync(photos, progress);
        var originals = photos.Where(p => !p.IsLocked && Lib.Get(p.Id) != null).ToList();
        string msg = r.added == 1 ? "Locked 1 item" : $"Locked {r.added:N0} items";
        if (r.skipped > 0) msg += $" ({r.skipped:N0} already there)";
        if (r.errors.Count > 0) msg += $". {r.errors.Count:N0} couldn't be locked: {r.errors[0]}";
        if (r.added > 0 && originals.Count > 0)
            ShowToast(msg + ". The originals are still in your library.", "Delete originals", () => DeleteOriginals(originals));
        else ShowToast(msg + ".");
        return r;
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
        AddToLocked(photos);
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
}
