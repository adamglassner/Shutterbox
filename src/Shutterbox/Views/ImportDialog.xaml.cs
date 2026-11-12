using System.Globalization;
using System.Windows.Controls;
using Microsoft.Win32;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox.Views;

public partial class ImportDialog : Window
{
    private readonly MainWindow _main;
    private List<string> _sources = new();
    private ImportPlan _plan;
    private CancellationTokenSource _cts;
    private bool _importing;
    private static AppSettings S => SettingsService.Current;

    /// <summary>The current look-through of the source; the self-test waits on it.</summary>
    public Task AnalysisTask { get; private set; } = Task.CompletedTask;
    public ImportPlan Plan => _plan;

    public ImportDialog(MainWindow main, IEnumerable<string> sources)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _main = main;
        DestBox.Text = S.ImportFolder;
        DeleteBox.IsChecked = S.ImportDeleteOriginals;
        SkipDupBox.IsChecked = S.ImportSkipDuplicates;
        LayoutBox.SelectedIndex = (int)S.ImportLayout;

        foreach (var (label, path) in Importer.FindCameraCards())
        {
            var b = new Button { Style = (Style)FindResource("ToolButton"), Margin = new Thickness(0, 0, 8, 8), ToolTip = path };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
            var p = path;
            b.Click += (_, _) => SetSources(new[] { p });
            CardsPanel.Children.Add(b);
        }
        CardsPanel.Visibility = CardsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var list = sources?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (list?.Count > 0) SetSources(list);
        else if (CardsPanel.Children.Count > 0 && Importer.FindCameraCards() is { Count: > 0 } cards) SetSources(new[] { cards[0].path });
        UpdateExample();
        UpdateModes();
    }

    public void SetSources(IEnumerable<string> sources)
    {
        if (_importing) return;
        _sources = sources.ToList();
        SourceBox.Text = _sources.Count == 1 ? _sources[0] : $"{_sources.Count} items";
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _plan = null;
        ImportBtn.IsEnabled = false;
        SummaryTitle.Text = "Looking for photos…";
        SummaryText.Text = _sources.Count == 1 ? MainWindow.ShortPath(_sources[0]) : string.Join(", ", _sources.Take(3).Select(IOPath.GetFileName)) + (_sources.Count > 3 ? "…" : "");
        AnalyzeProgress.Visibility = Visibility.Visible;
        UpdateModes();
        AnalysisTask = AnalyzeAsync(_cts.Token);
    }

    private async Task AnalyzeAsync(CancellationToken ct)
    {
        try
        {
            var progress = new Progress<int>(n => SummaryTitle.Text = $"Looking for photos… {n:N0} found");
            var plan = await Importer.AnalyzeAsync(LibraryStore.Current, _sources, progress, ct);
            if (ct.IsCancellationRequested) return;
            _plan = plan;
            ShowSummary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SummaryTitle.Text = "Couldn't read that folder";
            SummaryText.Text = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) AnalyzeProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowSummary()
    {
        if (_plan == null) return;
        if (_plan.Count == 0)
        {
            SummaryTitle.Text = "No photos here";
            SummaryText.Text = "Shutterbox didn't find any pictures it can import in this folder.";
            ImportBtn.IsEnabled = false;
            return;
        }
        int dup = _plan.Duplicates;
        bool skip = SkipDupBox.IsChecked == true;
        int toCopy = skip ? _plan.Count - dup : _plan.Count;
        long bytes = _plan.Items.Where(i => !(skip && i.Duplicate)).Sum(i => i.Size);
        SummaryTitle.Text = $"{_plan.Count:N0} photo{(_plan.Count == 1 ? "" : "s")} · {Photo.FormatSize(_plan.Bytes)}";
        string range = _plan.From is DateTime f && _plan.To is DateTime t
            ? (f.Date == t.Date ? f.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture)
               : f.Year == t.Year ? $"{f:MMM d} – {t:MMM d, yyyy}" : $"{f:MMM d, yyyy} – {t:MMM d, yyyy}")
            : "";
        var parts = new List<string>();
        if (range.Length > 0) parts.Add("Taken " + range);
        if (dup > 0) parts.Add(dup == _plan.Count ? "You already have all of them" : $"{dup:N0} already in your library");
        SummaryText.Text = string.Join(" · ", parts);
        ImportBtn.Content = AddMode.IsChecked == true ? "Add folder" : toCopy == 0 ? "Nothing new" : $"Import {toCopy:N0}";
        ImportBtn.IsEnabled = AddMode.IsChecked == true || toCopy > 0;
    }

    private void UpdateModes()
    {
        bool oneFolder = _sources.Count == 1 && Directory.Exists(_sources[0]);
        bool alreadyIn = oneFolder && (LibraryStore.Current.IsInLibraryFolder(IOPath.Combine(_sources[0], "x")));
        bool removable = oneFolder && IsRemovable(_sources[0]);
        AddMode.IsEnabled = oneFolder && !alreadyIn && !removable;
        AddMode.ToolTip = alreadyIn ? "This folder is already in your library." : removable ? "Camera cards and USB drives come and go, so copy the photos instead." : null;
        if (!AddMode.IsEnabled && AddMode.IsChecked == true) CopyMode.IsChecked = true;
        CopyOptions.IsEnabled = CopyMode.IsChecked == true;
        CopyOptions.Opacity = CopyMode.IsChecked == true ? 1 : 0.5;
    }

    private static bool IsRemovable(string path)
    {
        try
        {
            var root = IOPath.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Removable;
        }
        catch { return false; }
    }

    private void UpdateExample()
    {
        var layout = (ImportLayout)Math.Max(0, LayoutBox.SelectedIndex);
        var sample = _plan?.To ?? DateTime.Today;
        var folder = Importer.TargetFolder(DestBox.Text, layout, sample);
        ExampleText.Text = "For example: " + MainWindow.ShortPath(IOPath.Combine(folder, "IMG_0042.jpg"));
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && CopyOptions == null) return;
        UpdateModes();
        ShowSummary();
    }

    private void Options_Changed(object sender, RoutedEventArgs e) => ShowSummary();

    private void LayoutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExampleText != null) UpdateExample();
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Import photos from" };
        if (_sources.Count == 1 && Directory.Exists(_sources[0])) dlg.InitialDirectory = _sources[0];
        else if (Directory.Exists(S.LastImportSource)) dlg.InitialDirectory = S.LastImportSource;
        if (dlg.ShowDialog(this) == true) SetSources(new[] { dlg.FolderName });
    }

    private void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Copy imported photos into" };
        if (Directory.Exists(DestBox.Text)) dlg.InitialDirectory = DestBox.Text;
        if (dlg.ShowDialog(this) == true) { DestBox.Text = dlg.FolderName; UpdateExample(); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (AddMode.IsChecked == true && _sources.Count == 1)
        {
            var folder = _sources[0];
            Close();
            await _main.AddFolderToLibrary(folder);
            return;
        }
        await RunImportAsync();
        if (IsVisible && !_failed) Close();
    }

    private bool _failed;

    /// <summary>Copies the photos and hands over to the main window. Returns null if there was nothing to do.</summary>
    public async Task<ImportResult> RunImportAsync()
    {
        if (_plan == null || _importing) return null;
        _importing = true;
        var dest = DestBox.Text;
        var layout = (ImportLayout)Math.Max(0, LayoutBox.SelectedIndex);
        bool skip = SkipDupBox.IsChecked == true, del = DeleteBox.IsChecked == true;
        S.ImportFolder = dest;
        S.ImportLayout = layout;
        S.ImportSkipDuplicates = skip;
        S.ImportDeleteOriginals = del;
        if (_sources.Count == 1 && Directory.Exists(_sources[0])) S.LastImportSource = _sources[0];
        SettingsService.Save();

        Root.IsEnabled = true;
        ImportBtn.IsEnabled = false;
        CopyMode.IsEnabled = AddMode.IsEnabled = CopyOptions.IsEnabled = false;
        CancelBtn.Content = "Stop";
        ProgressArea.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var progress = new Progress<(int done, int total)>(p =>
        {
            ImportProgress.Value = p.total == 0 ? 1 : (double)p.done / p.total;
            StatusText.Text = $"Copying {p.done:N0} of {p.total:N0}…";
        });
        ImportResult result;
        try
        {
            Directory.CreateDirectory(dest);
            result = await Importer.ImportAsync(_plan, dest, layout, skip, del, progress, _cts.Token);
        }
        catch (Exception ex)
        {
            _failed = true;
            StatusText.Text = "Import stopped: " + ex.Message;
            _importing = false;
            CancelBtn.Content = "Close";
            return null;
        }
        StatusText.Text = "Adding them to your library…";
        await _main.AfterImportAsync(dest, result);
        _importing = false;
        if (result.Errors.Count > 0 && result.Copied == 0)
        {
            _failed = true;
            StatusText.Text = "Nothing could be copied:\n" + string.Join("\n", result.Errors.Take(4));
            CancelBtn.Content = "Close";
        }
        return result;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_importing) { _cts?.Cancel(); return; }
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_importing) { _cts?.Cancel(); e.Cancel = true; return; }
        _cts?.Cancel();
    }
}
