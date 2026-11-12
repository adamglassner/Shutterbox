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
            ImportFolderBox.Text = S.ImportFolder;
            SlideBox.SelectedItem = SlideBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.SlideshowSeconds.ToString())
                                    ?? SlideBox.Items[1];
            VersionText.Text = "Shutterbox " + AppInfo.Version;
            BuildFolders();
            var lib = LibraryStore.Current;
            StorageText.Text =
                $"Your library ({lib.Count:N0} photos, albums and tags) is kept in {MainWindow.ShortPath(SettingsService.Folder)}.\n" +
                $"Thumbnails take {Photo.FormatSize(Thumbnails.CacheSize())} in {MainWindow.ShortPath(SettingsService.CacheFolder)}.";
        }
        finally { _loading = false; }
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
        SettingsService.Commit();
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
