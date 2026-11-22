using System.Windows.Input;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox.Views;

/// <summary>Sets up the Locked album (PIN or password, then the recovery key and Windows Hello), or changes the PIN/password.</summary>
public partial class LockSetupDialog : Window
{
    public enum Mode { SetUp, Change }

    private readonly Mode _mode;
    public string RecoveryKey { get; private set; }

    public LockSetupDialog(Mode mode)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _mode = mode;
        if (mode == Mode.Change)
        {
            HeadingText.Text = "Change PIN or password";
            IntroText.Text = "Choose a new PIN or password for the Locked album. Your recovery key stays the same.";
            NextBtn.Content = "Change";
            if (Vault.Info?.Kind == "Password") KindPassword.IsChecked = true;
        }
        Loaded += async (_, _) =>
        {
            Secret1.Focus();
            if (_mode == Mode.SetUp && !await WindowsHello.IsAvailableAsync())
            {
                HelloBox.IsEnabled = false;
                HelloBox.Content = "Windows Hello isn't set up on this PC (you can turn it on later in Settings)";
            }
            else if (_mode == Mode.SetUp) HelloBox.IsChecked = true;
        };
    }

    private string Kind => KindPassword.IsChecked == true ? "Password" : "PIN";

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (SecretLabel == null) return;
        SecretLabel.Text = Kind == "PIN" ? "PIN (4 to 12 digits)" : "Password (at least 6 characters)";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Secret2_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Next_Click(null, null); e.Handled = true; }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Checks the two entries. Returns null when they're fine.</summary>
    public static string Check(string a, string b, string kind) =>
        Vault.Validate(a, kind) ?? (a != b ? (kind == "PIN" ? "The PINs don't match." : "The passwords don't match.") : null);

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        var a = Secret1.Password;
        var b = Secret2.Password;
        var kind = Kind;
        var problem = Check(a, b, kind);
        if (problem != null) { ShowError(problem); return; }
        NextBtn.IsEnabled = false;
        try
        {
            if (_mode == Mode.Change)
            {
                await Task.Run(() => Vault.ChangeSecret(a, kind));
                DialogResult = true;
                return;
            }
            RecoveryKey = await Task.Run(() => Vault.SetUp(a, kind));
            RecoveryText.Text = RecoveryKey;
            StepSecret.Visibility = Visibility.Collapsed;
            StepRecovery.Visibility = Visibility.Visible;
            DoneBtn.Focus();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { NextBtn.IsEnabled = true; }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(RecoveryKey ?? ""); } catch { }
    }

    private void SavedBox_Click(object sender, RoutedEventArgs e) => DoneBtn.IsEnabled = SavedBox.IsChecked == true;

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (HelloBox.IsChecked == true && HelloBox.IsEnabled)
        {
            DoneBtn.IsEnabled = false;
            var problem = await WindowsHello.TurnOnAsync();
            Activate();
            DoneBtn.IsEnabled = true;
            if (problem != null && problem != "Canceled.")
            {
                HelloError.Text = problem + " You can try again later in Settings.";
                HelloError.Visibility = Visibility.Visible;
                HelloBox.IsChecked = false;
                return;
            }
        }
        DialogResult = true;
    }
}
