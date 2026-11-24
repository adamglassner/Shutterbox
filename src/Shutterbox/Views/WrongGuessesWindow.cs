using System.Windows.Controls;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox.Views;

/// <summary>Every wrong PIN, password or recovery key (from every device that uses the album), and the photos taken of them.</summary>
public sealed class WrongGuessesWindow : Window
{
    public WrongGuessesWindow()
    {
        Title = "Wrong guesses";
        Style = (Style)Application.Current.FindResource("DialogWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.Attach(this);
        Width = 560;
        Height = 600;
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        var list = new StackPanel();
        var heading = new TextBlock { Text = "Wrong guesses", FontSize = 18, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        list.Children.Add(heading);

        var photos = Vault.IsUnlocked ? Vault.IntruderPhotos : new List<IntruderPhoto>();
        if (photos.Count > 0)
        {
            list.Children.Add(Hint("Photos taken after wrong guesses (right-click to delete)", 14));
            var wrap = new WrapPanel();
            foreach (var p in photos)
            {
                var card = new StackPanel { Margin = new Thickness(0, 0, 10, 10), Width = 120 };
                var img = new Image { Width = 120, Height = 90, Stretch = Stretch.UniformToFill };
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(Vault.ReadIntruderPhoto(p));
                    bmp.DecodePixelWidth = 240;
                    bmp.EndInit();
                    bmp.Freeze();
                    img.Source = bmp;
                }
                catch { }
                card.Children.Add(img);
                card.Children.Add(Hint(p.Time?.ToString("MMM d, h:mm tt") ?? "", 4));
                var menu = new ContextMenu();
                var del = new MenuItem { Header = "Delete" };
                var photo = p;
                del.Click += (_, _) => { Vault.DeleteIntruderPhoto(photo); wrap.Children.Remove(card); };
                menu.Items.Add(del);
                card.ContextMenu = menu;
                wrap.Children.Add(card);
            }
            list.Children.Add(wrap);
        }

        var attempts = Vault.FailedAttempts;
        list.Children.Add(Hint(attempts.Count == 0 ? "No wrong guesses logged." : $"{attempts.Count:N0} wrong {(attempts.Count == 1 ? "guess" : "guesses")}, newest first", 14));
        foreach (var a in attempts)
            list.Children.Add(new TextBlock
            {
                Text = $"Wrong {(a.What == "Recovery key" ? "recovery key" : a.What)}  ·  {a.Time:dddd, MMMM d, yyyy, h:mm tt}  ·  {a.Device}",
                Margin = new Thickness(0, 6, 0, 0),
            });
        list.Children.Add(Hint("After 5 wrong guesses in a row on a PC, Shutterbox makes you wait, longer each time. What really protects the album is that " +
                               "each guess costs 600,000 rounds of PBKDF2, even for someone with a copy of the folder.", 18));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static TextBlock Hint(string text, double top)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return t;
    }
}
