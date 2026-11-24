using System.Windows.Controls;
using System.Windows.Input;
using Shutterbox.Services;

namespace Shutterbox.Views;

/// <summary>Asks for a PIN or password (an album with its own PIN). Built in code: it's small.</summary>
public sealed class SecretPrompt : Window
{
    private readonly PasswordBox _box = new() { Height = 34, FontSize = 15, Padding = new Thickness(8, 6, 8, 6) };
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Func<string, bool> _check;

    private SecretPrompt(string title, string text, Func<string, bool> check)
    {
        _check = check;
        Title = title;
        Style = (Style)Application.Current.FindResource("DialogWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.Attach(this);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 360 };
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        panel.Children.Add(heading);
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        panel.Children.Add(hint);
        panel.Children.Add(_box);
        panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var ok = new Button { Content = "Open", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton") };
        ok.Click += async (_, _) =>
        {
            var s = _box.Password;
            if (s.Length == 0) return;
            IsEnabled = false;
            bool good;
            try { good = await Task.Run(() => _check(s)); }
            finally { IsEnabled = true; }
            if (good) { DialogResult = true; return; }
            _box.Clear();
            _error.Text = "That isn't right.";
            _error.Visibility = Visibility.Visible;
            _box.Focus();
        };
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Loaded += (_, _) => _box.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    /// <summary>True when <paramref name="check"/> accepted what was typed.</summary>
    public static bool Ask(Window owner, string title, string text, Func<string, bool> check) =>
        new SecretPrompt(title, text, check) { Owner = owner }.ShowDialog() == true;
}
