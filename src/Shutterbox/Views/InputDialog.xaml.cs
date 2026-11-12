using System.Windows.Input;
using Shutterbox.Services;

namespace Shutterbox.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
    }

    /// <summary>Asks for a line of text. Returns null if cancelled.</summary>
    public static string Ask(Window owner, string title, string label, string hint, string initial)
    {
        var d = new InputDialog { Owner = owner, Title = title };
        d.HeadingText.Text = title;
        d.LabelText.Text = label;
        if (!string.IsNullOrEmpty(hint)) { d.HintText.Text = hint; d.HintText.Visibility = Visibility.Visible; }
        d.Input.Text = initial ?? "";
        d.Loaded += (_, _) => { d.Input.Focus(); d.Input.SelectAll(); };
        return d.ShowDialog() == true ? d.Input.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Input.Text.Trim().Length == 0) { Input.Focus(); return; }
        DialogResult = true;
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Ok_Click(null, null); e.Handled = true; }
    }
}
