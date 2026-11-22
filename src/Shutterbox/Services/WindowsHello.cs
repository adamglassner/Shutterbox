using System.Security.Cryptography;
using Shutterbox.Library;
using Windows.Security.Credentials.UI;

namespace Shutterbox.Services;

/// <summary>
/// Unlocking the Locked album with Windows Hello (fingerprint, face or the Windows PIN), the same way WillowWallet does.
///
/// Turning it on keeps a copy of the album's key on this PC, protected for your Windows account (DPAPI), in
/// %APPDATA%\Shutterbox\hello.bin. It's only used after Windows Hello confirms it's you. Your Shutterbox PIN or
/// password and recovery key keep working.
/// </summary>
public static class WindowsHello
{
    private static readonly byte[] Entropy = "Shutterbox Locked album Windows Hello v1"u8.ToArray();

    private static string FilePath => IOPath.Combine(SettingsService.Folder, "hello.bin");

    public static bool IsOn => Vault.IsSetUp && File.Exists(FilePath);

    public static async Task<bool> IsAvailableAsync()
    {
        try { return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available; }
        catch { return false; }
    }

    /// <summary>Asks Windows Hello. Returns null when it's you, or a message saying why not.</summary>
    public static async Task<string> VerifyAsync(string message)
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result switch
            {
                UserConsentVerificationResult.Verified => null,
                UserConsentVerificationResult.Canceled => "Canceled.",
                UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser =>
                    "Windows Hello isn't set up on this PC. Set it up in Windows Settings → Accounts → Sign-in options.",
                UserConsentVerificationResult.DisabledByPolicy => "Windows Hello is turned off on this PC by policy.",
                UserConsentVerificationResult.RetriesExhausted => "Too many tries. Use your PIN or password.",
                _ => "Windows Hello couldn't confirm it's you.",
            };
        }
        catch (Exception ex)
        {
            return $"Windows Hello isn't available ({ex.Message}).";
        }
    }

    /// <summary>Turns it on (the Locked album must be unlocked). Returns null, or why it couldn't.</summary>
    public static async Task<string> TurnOnAsync()
    {
        var key = Vault.CopyKey();
        if (key == null) return "Unlock the Locked album first.";
        try
        {
            if (!await IsAvailableAsync()) return "Windows Hello isn't set up on this PC. Set it up in Windows Settings → Accounts → Sign-in options.";
            var problem = await VerifyAsync("Turn on Windows Hello for Shutterbox's Locked album");
            if (problem != null) return problem;
            Directory.CreateDirectory(SettingsService.Folder);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't turn it on ({ex.Message}).";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void TurnOff()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    /// <summary>Unlocks with Windows Hello. Returns null when unlocked, or a message.</summary>
    public static async Task<string> UnlockAsync()
    {
        if (!IsOn) return "Windows Hello isn't turned on for the Locked album.";
        var problem = await VerifyAsync("Unlock Shutterbox's Locked album");
        if (problem != null) return problem;
        byte[] key;
        try
        {
            key = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        }
        catch
        {
            TurnOff();
            return "Windows Hello needs to be turned on again. Unlock with your PIN or password, then turn it on in Settings.";
        }
        if (Vault.UnlockWithKey(key)) return null;
        TurnOff();
        return "The Locked album was reset since Windows Hello was turned on. Unlock with your PIN or password, then turn it on again in Settings.";
    }
}
