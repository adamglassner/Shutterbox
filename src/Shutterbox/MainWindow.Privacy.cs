using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Shutterbox.Library;
using Shutterbox.Services;

namespace Shutterbox;

/// <summary>
/// The Locked album's privacy on Windows: keeping the window out of screen capture while it's unlocked, locking when you
/// leave, the Ctrl+Alt+L panic key, a hidden sidebar entry, and "Lock with Shutterbox" from File Explorer.
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int PanicHotkeyId = 0x5B0C, WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    private bool _hotkeyOn;
    private bool _privacyHooked;

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>Called when the window has a handle (and from Settings): applies everything below.</summary>
    public void ApplyLockedPrivacy()
    {
        if (Hwnd == IntPtr.Zero) return;
        if (!_privacyHooked)
        {
            _privacyHooked = true;
            HwndSource.FromHwnd(Hwnd)?.AddHook(PrivacyHook);
            Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LeftShutterbox));
            StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && S.LockOnLeave && !App.IsSelfTest) LockNow(); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            Closed += (_, _) => { SystemEvents.SessionSwitch -= OnSessionSwitch; if (_hotkeyOn) UnregisterHotKey(Hwnd, PanicHotkeyId); };
        }
        // out of screenshots, recordings and screen sharing while the Locked album is open (black there; normal on screen)
        SetWindowDisplayAffinity(Hwnd, Vault.IsUnlocked && S.ProtectScreen ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        if (S.PanicHotkey && !_hotkeyOn && !App.IsSelfTest) _hotkeyOn = RegisterHotKey(Hwnd, PanicHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4C /* L */);
        else if (!S.PanicHotkey && _hotkeyOn) { UnregisterHotKey(Hwnd, PanicHotkeyId); _hotkeyOn = false; }
        NavLocked.Visibility = S.HideLocked && Vault.IsSetUp && _view != LibraryView.Locked ? Visibility.Collapsed : Visibility.Visible;
    }

    private IntPtr PrivacyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == PanicHotkeyId) { Panic(); handled = true; }
        return IntPtr.Zero;
    }

    /// <summary>Ctrl+Alt+L: lock, go back to Photos and hide Shutterbox.</summary>
    public void Panic()
    {
        LockNow();
        if (_view == LibraryView.Locked) ShowView(LibraryView.Photos);
        WindowState = WindowState.Minimized;
    }

    /// <summary>Another app became the active one (not one of Shutterbox's own dialogs or file pickers).</summary>
    private void LeftShutterbox()
    {
        if (!S.LockOnLeave || !Vault.IsUnlocked || App.IsSelfTest || IsActive) return;
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fg, out var pid);
            if (pid == (uint)Environment.ProcessId) return;
        }
        LockNow();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect && S.LockOnLeave)
            Dispatcher.BeginInvoke(new Action(LockNow));
    }

    /// <summary>Ctrl+Shift+L opens the Locked album even when it's hidden in the sidebar.</summary>
    private bool PrivacyKey(KeyEventArgs e)
    {
        if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ShowView(LibraryView.Locked);
            e.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>With Locked hidden, its PIN or password typed in search (and Enter) opens it.</summary>
    private async Task<bool> TrySearchUnlockAsync(string text)
    {
        if (!S.HideLocked || !Vault.IsSetUp || string.IsNullOrEmpty(text) || text.Length > 128) return false;
        if (!await Task.Run(() => Vault.Unlock(text, log: false))) return false;
        SearchBox.Text = "";
        ShowView(LibraryView.Locked);
        return true;
    }

    // ================================================================== "Lock with Shutterbox" in File Explorer

    /// <summary>Files handed over by File Explorer's "Lock with Shutterbox" (possibly from other Shutterbox processes).</summary>
    public void LockFilesFromExplorer(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var photos = files.Select(f => Lib.GetByPath(f) ?? MetadataReader.Read(f)).Where(p => p != null).ToList();
        if (photos.Count == 0) return;
        var m = NewMenu(this);
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        Item(m, "", $"Copy {(photos.Count == 1 ? "it" : $"{photos.Count:N0} items")} to the Locked album", () => AddToLocked(photos, false));
        Item(m, "", $"Move {(photos.Count == 1 ? "it" : $"{photos.Count:N0} items")} to the Locked album (the originals are deleted)", () => AddToLocked(photos, true));
        m.IsOpen = true;
    }
}
