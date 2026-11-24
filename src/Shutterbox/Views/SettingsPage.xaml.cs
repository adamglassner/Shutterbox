using System.Windows.Controls;
using Microsoft.Win32;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox.Views;

public partial class SettingsPage : UserControl
{
    private readonly MainWindow _main;
    private bool _loading;
    private static AppSettings S => SettingsService.Current;

    public SettingsPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        foreach (var a in ThemeManager.Accents)
        {
            var name = a.Name;
            var b = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Width = 34, Height = 34, Margin = new Thickness(0, 0, 12, 0),
                Background = new SolidColorBrush(a.Dark),
                ToolTip = name,
                Tag = name,
            };
            b.Click += (_, _) =>
            {
                S.Accent = name;
                SettingsService.Commit();
                Refresh();
            };
            AccentPanel.Children.Add(b);
        }
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            ThemeDark.IsChecked = S.Theme == ThemeMode.Dark;
            ThemeLight.IsChecked = S.Theme == ThemeMode.Light;
            ThemeSystem.IsChecked = S.Theme == ThemeMode.System;
            DensityCompact.IsChecked = S.Density == TileDensity.Compact;
            DensityComfortable.IsChecked = S.Density == TileDensity.Comfortable;
            DensityLarge.IsChecked = S.Density == TileDensity.Large;
            foreach (var b in AccentPanel.Children.OfType<Button>())
            {
                bool on = (string)b.Tag == S.Accent;
                b.BorderBrush = on ? (Brush)FindResource("Text") : (Brush)FindResource("ControlBorder");
                b.BorderThickness = new Thickness(on ? 2 : 1);
                b.Content = on ? new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = Brushes.Black } : null;
            }
            WatchBox.IsChecked = S.WatchFolders;
            ScanStartBox.IsChecked = S.ScanOnStart;
            FindPeopleBox.IsChecked = S.FindPeopleAndThings;
            var (analyzed, faces, people) = PeopleStore.Current?.Counts() ?? (0, 0, 0);
            PeopleStatus.Text = Analyzer.Total > 0 && Analyzer.Done < Analyzer.Total
                ? $"Looked through {Analyzer.Done:N0} of {Analyzer.Total:N0} photos so far · {faces:N0} faces"
                : $"Looked through {analyzed:N0} photos · {faces:N0} faces";
            ImportFolderBox.Text = S.ImportFolder;
            SlideBox.SelectedItem = SlideBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.SlideshowSeconds.ToString())
                                    ?? SlideBox.Items[1];
            VersionText.Text = "Shutterbox " + AppInfo.Version;
            RefreshLocked();
            BuildFolders();
            var lib = LibraryStore.Current;
            StorageText.Text =
                $"Your library ({lib.Count:N0} photos, albums and tags) is kept in {MainWindow.ShortPath(SettingsService.Folder)}.\n" +
                $"Thumbnails take {Photo.FormatSize(Thumbnails.CacheSize())} in {MainWindow.ShortPath(SettingsService.CacheFolder)}.";
        }
        finally { _loading = false; }
    }

    private void RefreshLocked()
    {
        bool setUp = Vault.IsSetUp, open = Vault.IsUnlocked;
        LockedSetupBtn.Visibility = setUp ? Visibility.Collapsed : Visibility.Visible;
        LockedOpenBtn.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        LockedLockBtn.Visibility = setUp && open ? Visibility.Visible : Visibility.Collapsed;
        LockedOptions.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        LockedDeleteBtn.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        ProtectScreenBox.IsChecked = S.ProtectScreen;
        LockOnLeaveBox.IsChecked = S.LockOnLeave;
        PanicBox.IsChecked = S.PanicHotkey;
        HideLockedBox.IsChecked = S.HideLocked;
        IntruderBox.IsChecked = Vault.PhotographsWrongGuesses;
        IntruderBox.IsEnabled = LockedCheckBtn.IsEnabled = NewRecoveryBtn.IsEnabled = open;
        var guesses = Vault.FailedAttempts;
        GuessSummary.Text = guesses.Count == 0 ? "No wrong guesses logged." : $"{guesses.Count:N0} wrong guesses logged, the last {guesses[0].Time:dddd, MMM d, h:mm tt} on {guesses[0].Device}.";
        var info = Vault.Info;
        LockedStatus.Text = !setUp
            ? "Copy photos and videos into an encrypted album that only Shutterbox can open, with a PIN, a password or Windows Hello."
            : open ? $"Unlocked · {Vault.Count:N0} item{(Vault.Count == 1 ? "" : "s")} · protected with a {(info?.Kind == "Password" ? "password" : "PIN")}"
                   : $"Locked · protected with a {(info?.Kind == "Password" ? "password" : "PIN")}. Unlock it to change these settings.";
        LockedChangeBtn.IsEnabled = LockedMoveBtn.IsEnabled = LockedDeleteBtn.IsEnabled = AutoLockBox.IsEnabled = HelloBtn.IsEnabled = open;
        HelloBtn.Content = WindowsHello.IsOn ? "Turn off Windows Hello" : "Turn on Windows Hello…";
        HelloText.Text = WindowsHello.IsOn ? "Windows Hello is on for this PC: unlock with your fingerprint, face or Windows PIN."
                                           : "Windows Hello is off. It's set up separately on each PC.";
        var mins = (info?.AutoLockMinutes ?? 5).ToString();
        AutoLockBox.SelectedItem = AutoLockBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == mins) ?? AutoLockBox.Items[1];
        LockedPath.Text = MainWindow.ShortPath(Vault.Folder);
        LockedPath.ToolTip = Vault.Folder;
    }

    private void LockedMessage(string text)
    {
        LockedMsg.Text = text ?? "";
        LockedMsg.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LockedSetup_Click(object sender, RoutedEventArgs e) { _main.SetUpLocked(); Refresh(); }
    private void LockedOpen_Click(object sender, RoutedEventArgs e) => _main.ShowView(LibraryView.Locked);
    private void LockedLock_Click(object sender, RoutedEventArgs e) { _main.LockNow(); Refresh(); }

    private void LockedPrivacy_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ProtectScreen = ProtectScreenBox.IsChecked == true;
        S.LockOnLeave = LockOnLeaveBox.IsChecked == true;
        S.PanicHotkey = PanicBox.IsChecked == true;
        S.HideLocked = HideLockedBox.IsChecked == true;
        SettingsService.Save();
        _main.ApplyLockedPrivacy();
    }

    private void Intruder_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (IntruderBox.IsChecked == true) Vault.EnableIntruderPhotos();
            else if (_main.Confirm("Turn off photographing wrong guesses? The photos taken are deleted.", "Turn off")) Vault.DisableIntruderPhotos();
        }
        catch (Exception ex) { _main.ShowToast(ex.Message); }
        Refresh();
    }

    private void WrongGuesses_Click(object sender, RoutedEventArgs e) => new WrongGuessesWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private async void LockedCheck_Click(object sender, RoutedEventArgs e)
    {
        LockedCheckBtn.IsEnabled = false;
        try
        {
            var (ok, problems) = await Vault.CheckAsync(new Progress<(int, int)>(p => _main.ShowToast($"Checking the Locked album: {p.Item1:N0} of {p.Item2:N0}…")));
            _main.ShowToast(problems.Count == 0 ? $"All {ok:N0} items are there and decrypt correctly."
                : $"{ok:N0} items are fine; {problems.Count:N0} {(problems.Count == 1 ? "has a problem" : "have problems")}: {string.Join("; ", problems.Take(3))}");
        }
        catch (Exception ex) { _main.ShowToast(ex.Message); }
        finally { LockedCheckBtn.IsEnabled = true; }
    }

    private void NewRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.Confirm("Get a new recovery key?\n\nThe one you have now stops working. Save the new one somewhere safe.", "Continue")) return;
        new LockSetupDialog(LockSetupDialog.Mode.NewRecoveryKey) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void LockedChange_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LockSetupDialog(LockSetupDialog.Mode.Change) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) _main.ShowToast("PIN or password changed");
        Refresh();
    }

    private async void Hello_Click(object sender, RoutedEventArgs e)
    {
        LockedMessage(null);
        if (WindowsHello.IsOn) { WindowsHello.TurnOff(); Refresh(); return; }
        HelloBtn.IsEnabled = false;
        var problem = await WindowsHello.TurnOnAsync();
        Window.GetWindow(this)?.Activate();
        if (problem != null && problem != "Canceled.") LockedMessage(problem);
        Refresh();
    }

    private void AutoLockBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AutoLockBox.SelectedItem is not ComboBoxItem item || !Vault.IsUnlocked) return;
        Vault.SetAutoLock(int.Parse((string)item.Tag));
    }

    private async void LockedMove_Click(object sender, RoutedEventArgs e)
    {
        LockedMessage(null);
        var dlg = new OpenFolderDialog { Title = "Move the Locked album into which folder? (for example your OneDrive folder)" };
        var cloud = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(cloud) && Directory.Exists(cloud)) dlg.InitialDirectory = cloud;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        LockedMoveBtn.IsEnabled = false;
        var progress = new Progress<(int done, int total)>(p => _main.ShowToast($"Moving the Locked album: {p.done:N0} of {p.total:N0} files…"));
        var problem = await Vault.MoveToAsync(dlg.FolderName, progress);
        if (problem != null) LockedMessage(problem);
        else _main.ShowToast($"The Locked album is now in {MainWindow.ShortPath(Vault.Folder)}", "Show", () => Shell.ShowInExplorer(Vault.Folder));
        Refresh();
    }

    private void LockedShow_Click(object sender, RoutedEventArgs e) => Shell.ShowInExplorer(Vault.Folder);

    private void LockedUseOther_Click(object sender, RoutedEventArgs e)
    {
        LockedMessage(null);
        if (Vault.IsSetUp && !_main.Confirm("Switch to another Locked album?\n\nThe one Shutterbox uses now stays where it is (" + Vault.Folder +
                                             "). You can switch back the same way.", "Use another Locked album")) return;
        var dlg = new OpenFolderDialog { Title = "Choose the “Shutterbox Locked album” folder" };
        var cloud = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(cloud) && Directory.Exists(cloud)) dlg.InitialDirectory = cloud;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var problem = Vault.OpenFolder(dlg.FolderName);
        if (problem != null) { LockedMessage(problem); return; }
        _main.ShowToast("Using the Locked album in " + MainWindow.ShortPath(Vault.Folder) + ". Unlock it with its PIN, password or recovery key.");
        _main.ShowView(LibraryView.Locked);
    }

    private void LockedDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!Vault.IsUnlocked) return;
        if (!_main.Confirm($"Delete the Locked album and the {Vault.Count:N0} item{(Vault.Count == 1 ? "" : "s")} in it, for good?\n\n" +
                           "Encrypted items don't go to the Recycle Bin. Restore anything you want to keep first.", "Delete Locked album")) return;
        Vault.Delete();
        SettingsService.Current.LockedFolder = "";
        SettingsService.Save();
        _main.ShowToast("The Locked album is deleted");
        Refresh();
    }

    private void BuildFolders()
    {
        FolderList.Children.Clear();
        var lib = LibraryStore.Current;
        foreach (var f in lib.Folders)
        {
            var exists = Directory.Exists(f);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = new Button { Style = (Style)FindResource("ToolButton"), Content = "Remove", Margin = new Thickness(8, 0, 0, 0), Height = 32 };
            var folder = f;
            remove.Click += (_, _) => _main.RemoveFolder(folder);
            DockPanel.SetDock(remove, Dock.Right);
            var show = new Button { Style = (Style)FindResource("ToolButton"), Content = "Show", Margin = new Thickness(8, 0, 0, 0), Height = 32, IsEnabled = exists };
            show.Click += (_, _) => Shell.ShowInExplorer(folder);
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(show);
            var icon = new TextBlock { Text = exists ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, Margin = new Thickness(0, 0, 12, 0),
                                       VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource(exists ? "Accent" : "DangerText") };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = f, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f });
            text.Children.Add(new TextBlock
            {
                Text = exists ? $"{lib.CountIn(f):N0} photos" : "Not available right now. Its photos are kept until it's back.",
                FontSize = 12, Foreground = (Brush)FindResource("TextTertiary"),
            });
            row.Children.Add(text);
            FolderList.Children.Add(row);
        }
        NoFolders.Visibility = lib.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Theme = sender == ThemeLight ? ThemeMode.Light : sender == ThemeSystem ? ThemeMode.System : ThemeMode.Dark;
        SettingsService.Commit();
        Refresh();
    }

    private void Density_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Density = sender == DensityCompact ? TileDensity.Compact : sender == DensityLarge ? TileDensity.Large : TileDensity.Comfortable;
        SettingsService.Commit();
    }

    private void Behaviour_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.WatchFolders = WatchBox.IsChecked == true;
        S.ScanOnStart = ScanStartBox.IsChecked == true;
        S.FindPeopleAndThings = FindPeopleBox.IsChecked == true;
        SettingsService.Commit();
    }

    private void ForgetPeople_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.Confirm("Forget every face and person, including the names you gave them?\n\nShutterbox looks through your photos again and groups faces from scratch. Your photos aren't touched.", "Forget all people")) return;
        PeopleStore.Current?.ForgetPeople();
        PeopleStore.Current?.Save();
        Analyzer.Poke();
        Refresh();
    }

    private void SlideBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SlideBox.SelectedItem is not ComboBoxItem item) return;
        S.SlideshowSeconds = int.Parse((string)item.Tag);
        SettingsService.Save();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _ = _main.StartScan(null);
        _main.ShowToast("Checking your folders for new, changed and deleted photos…");
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder with photos", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var f in dlg.FolderNames) _ = _main.AddFolderToLibrary(f);
        Refresh();
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Copy imported photos into" };
        if (Directory.Exists(S.ImportFolder)) dlg.InitialDirectory = S.ImportFolder;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        S.ImportFolder = dlg.FolderName;
        SettingsService.Save();
        Refresh();
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        Thumbnails.ClearDiskCache();
        Refresh();
        _main.ShowToast("Thumbnail cache cleared");
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsService.Folder);
        Shell.Open(SettingsService.Folder);
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => Shell.Open(AppInfo.RepoUrl);

    private void Changes_Click(object sender, RoutedEventArgs e)
    {
        if (ChangesBox.Visibility == Visibility.Visible) { ChangesBox.Visibility = Visibility.Collapsed; return; }
        ChangesText.Text = AppInfo.ReadChangelog().Replace("\r", "").Trim();
        ChangesBox.Visibility = Visibility.Visible;
    }
}
