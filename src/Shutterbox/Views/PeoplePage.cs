using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox.Views;

/// <summary>
/// People found in your photos (a face for each, with their name or "Add a name") and the things Shutterbox
/// recognised, as search suggestions. Built in code; <see cref="MainWindow"/> shows it for the People view.
/// </summary>
public sealed class PeoplePage : ScrollViewer
{
    private readonly MainWindow _win;
    private readonly StackPanel _root = new() { Margin = new Thickness(28, 8, 28, 28) };
    private bool _showHidden;

    public PeoplePage(MainWindow win)
    {
        _win = win;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Focusable = false;
        Content = _root;
    }

    private static TextBlock Text(string s, double size = 13, string brush = "TextSecondary", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>A round crop of the face, from the photo's cached thumbnail.</summary>
    public static FrameworkElement FaceImage(Photo p, FaceInfo f, double size)
    {
        var e = new Ellipse { Width = size, Height = size };
        e.SetResourceReference(Shape.FillProperty, "TileBg");
        if (p == null || f?.Box == null) return e;
        void Apply(BitmapSource img)
        {
            if (img == null) return;
            double dw = Math.Max(1, img.PixelWidth), dh = Math.Max(1, img.PixelHeight);
            double cx = (f.Box[0] + f.Box[2] / 2) * dw, cy = (f.Box[1] + f.Box[3] / 2) * dh;
            double side = Math.Max(f.Box[2] * dw, f.Box[3] * dh) * 1.45;
            e.Fill = new ImageBrush(img)
            {
                Viewbox = new Rect((cx - side / 2) / dw, (cy - side / 2) / dh, side / dw, side / dh),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.UniformToFill,
            };
        }
        var thumb = Thumbnails.Get(p, Apply);
        if (thumb != null) Apply(thumb);
        return e;
    }

    public void Refresh()
    {
        _root.Children.Clear();
        var store = PeopleStore.Current;
        var lib = LibraryStore.Current;
        if (store == null || lib == null) return;
        bool Shown(string id) => lib.Get(id) is Photo p && !p.IsTrashed;

        // what's happening
        var status = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        string line;
        if (!SettingsService.Current.FindPeopleAndThings)
        {
            line = "Finding people and things is turned off.";
            var on = new Button { Content = "Turn on", Style = (Style)FindResource("ToolAccentButton"), Height = 30, Margin = new Thickness(12, 0, 0, 0) };
            on.Click += (_, _) => { SettingsService.Current.FindPeopleAndThings = true; SettingsService.Save(); Analyzer.Poke(); Refresh(); };
            DockPanel.SetDock(on, Dock.Right);
            status.Children.Add(on);
        }
        else if (Analyzer.Error != null && !Analyzer.Busy) line = "Shutterbox couldn't look through your photos: " + Analyzer.Error;
        else if (Analyzer.Total > 0 && Analyzer.Done < Analyzer.Total)
            line = $"Looking through your photos on this PC: {Analyzer.Done:N0} of {Analyzer.Total:N0}. People and things appear as they're found.";
        else line = "Found on this PC; nothing is uploaded. Click a person to see their photos and give them a name.";
        status.Children.Add(Text(line));
        _root.Children.Add(status);

        // people
        var people = store.People(Shown, _showHidden);
        if (people.Count == 0)
        {
            _root.Children.Add(Text(Analyzer.Done < Analyzer.Total ? "No one yet." : "No people yet. Someone shows up here once they're in two photos or more.", 15, "Text"));
        }
        else
        {
            var wrap = new WrapPanel();
            foreach (var v in people) wrap.Children.Add(PersonTile(v));
            _root.Children.Add(wrap);
        }
        if (_showHidden || store.People(Shown, true).Any(v => v.Person.Hidden))
        {
            var toggle = new Button { Content = _showHidden ? "Don't show hidden people" : "Show hidden people", Style = (Style)FindResource("LinkButton"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            toggle.Click += (_, _) => { _showHidden = !_showHidden; Refresh(); };
            _root.Children.Add(toggle);
        }

        // things
        var things = store.Things(Shown);
        if (things.Count > 0)
        {
            var h = Text("Things", 17, "Text", FontWeights.SemiBold);
            h.Margin = new Thickness(0, 28, 0, 4);
            _root.Children.Add(h);
            _root.Children.Add(Text("What Shutterbox recognised in your photos. Click one to search for it, or type what you're looking for (beach, dog, cake…) in search."));
            var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var (word, count) in things)
            {
                var b = new Button { Content = $"{char.ToUpper(word[0])}{word[1..]}  ·  {count:N0}", Style = (Style)FindResource("ToolButton"), Height = 32, Margin = new Thickness(0, 0, 8, 8) };
                var w = word;
                b.Click += (_, _) => _win.SearchFor(w);
                chips.Children.Add(b);
            }
            _root.Children.Add(chips);
        }
    }

    private FrameworkElement PersonTile(PeopleStore.PersonView v)
    {
        var lib = LibraryStore.Current;
        var panel = new StackPanel { Width = 132, Margin = new Thickness(0, 0, 16, 20), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var face = FaceImage(lib.Get(v.Cover?.Photo), v.Cover, 112);
        face.HorizontalAlignment = HorizontalAlignment.Center;
        if (v.Person.Hidden) face.Opacity = 0.45;
        panel.Children.Add(face);
        var name = Text(string.IsNullOrEmpty(v.Person.Name) ? "Add a name" : v.Person.Name, 14, string.IsNullOrEmpty(v.Person.Name) ? "Accent" : "Text", FontWeights.SemiBold);
        name.TextAlignment = TextAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(name);
        var count = Text(v.PhotoIds.Count == 1 ? "1 photo" : $"{v.PhotoIds.Count:N0} photos", 12, "TextTertiary");
        count.TextAlignment = TextAlignment.Center;
        panel.Children.Add(count);
        panel.ToolTip = string.IsNullOrEmpty(v.Person.Name) ? "Click to see their photos and add a name" : null;
        panel.MouseLeftButtonUp += (_, _) => _win.ShowPerson(v.Person.Id);
        panel.MouseRightButtonUp += (s, e) => { _win.ShowPersonMenu(v.Person.Id, (FrameworkElement)s); e.Handled = true; };
        return panel;
    }
}
