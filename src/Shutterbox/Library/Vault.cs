using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shutterbox.Services;

namespace Shutterbox.Library;

/// <summary>An item in the Locked album, as kept in its encrypted index.</summary>
public sealed class VaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTime Taken { get; set; }
    public string TakenSource { get; set; } = "file";
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSeconds { get; set; }
    public string Make { get; set; }
    public string Model { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int Orientation { get; set; } = 1;
    public int Rotation { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public bool HasThumb { get; set; }
}

public sealed class VaultInfo
{
    public int Version { get; set; } = 1;
    /// <summary>"PIN" or "Password".</summary>
    public string Kind { get; set; } = "PIN";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = Vault.Iterations;
    public string WrappedKey { get; set; } = "";
    public string RecoverySalt { get; set; } = "";
    public string RecoveryWrappedKey { get; set; } = "";
    /// <summary>Minutes without activity before the Locked album locks itself (0 = only when you lock it or close Shutterbox).</summary>
    public int AutoLockMinutes { get; set; } = 5;
}

/// <summary>
/// The Locked album: copies of photos and videos that Shutterbox encrypts so they can't be opened outside it.
///
/// It's the same design as WillowWallet's data lock. A random 256-bit key encrypts every file with AES-256-GCM, in
/// 1 MB pieces so long videos never have to fit in memory; each piece is authenticated with its position and whether
/// it's the last, so a file can't be cut short or reordered without it being noticed. Names, dates and thumbnails are
/// in an encrypted index too, and the files on disk have random names. The key is stored only wrapped: once with
/// a key derived from your PIN or password (PBKDF2-SHA256, 600,000 rounds), once with a recovery key shown when you
/// set it up, and optionally for Windows Hello (see <see cref="WindowsHello"/>). The PIN or password itself is never stored.
/// </summary>
public static class Vault
{
    public const int Iterations = 600_000;
    private const int ChunkSize = 1 << 20;
    private const int NonceSize = 12, TagSize = 16;
    private static readonly byte[] Magic = "SBOX1"u8.ToArray();

    private static byte[] _key;
    private static List<VaultItem> _items = new();
    private static List<Photo> _photos;
    private static int _busy;

    public static event Action Changed;

    public static string DefaultFolder => IOPath.Combine(SettingsService.Folder, "Locked");

    /// <summary>The Locked album's folder. It's self-contained, so it can live in OneDrive, Dropbox, on a USB drive or another PC.</summary>
    public static string Folder => string.IsNullOrWhiteSpace(SettingsService.Current.LockedFolder) ? DefaultFolder : SettingsService.Current.LockedFolder;

    /// <summary>The name Shutterbox gives the folder when you move the album somewhere.</summary>
    public const string PortableFolderName = "Shutterbox Locked album";

    /// <summary>True if the folder holds a Locked album (from this PC or another).</summary>
    public static bool IsVaultFolder(string folder) =>
        !string.IsNullOrEmpty(folder) && File.Exists(IOPath.Combine(folder, "vault.json")) && File.Exists(IOPath.Combine(folder, "index.sbox"));

    /// <summary>
    /// Moves the Locked album into a folder you choose (it goes in a "Shutterbox Locked album" folder there), for example
    /// your OneDrive folder so it's backed up and reaches your other PCs. Everything is copied and checked before the old
    /// copy is deleted. Returns null when done, or why it couldn't.
    /// </summary>
    public static async Task<string> MoveToAsync(string parent, IProgress<(int done, int total)> progress = null)
    {
        if (!IsSetUp) return "There's no Locked album to move yet.";
        if (IsBusy) return "Wait until Shutterbox finishes adding or restoring.";
        var from = IOPath.GetFullPath(Folder);
        var to = IOPath.GetFullPath(IOPath.Combine(parent, PortableFolderName));
        if (string.Equals(from.TrimEnd('\\'), to.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "The Locked album is already there.";
        if (LibraryStore.IsUnder(to, from)) return "Choose a folder outside the Locked album.";
        if (IsVaultFolder(to)) return $"There's already a Locked album in {to}. Open it instead, or choose another folder.";
        Interlocked.Increment(ref _busy);
        try
        {
            var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList();
            await Task.Run(() =>
            {
                int done = 0;
                foreach (var f in files)
                {
                    var rel = IOPath.GetRelativePath(from, f);
                    var dest = IOPath.Combine(to, rel);
                    Directory.CreateDirectory(IOPath.GetDirectoryName(dest));
                    File.Copy(f, dest, true);
                    if (new FileInfo(dest).Length != new FileInfo(f).Length) throw new IOException($"{rel} didn't copy completely.");
                    progress?.Report((++done, files.Count));
                }
            });
            SettingsService.Current.LockedFolder = to;
            SettingsService.Save();
            try { Directory.Delete(from, true); } catch { /* the copy is in use; harmless leftovers */ }
            return null;
        }
        catch (Exception ex)
        {
            // the original is untouched; remove the partial copy
            try { if (IsVaultFolder(from) && Directory.Exists(to)) Directory.Delete(to, true); } catch { }
            return "Couldn't move it: " + ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Raise();
        }
    }

    /// <summary>
    /// Uses a Locked album folder made on another PC (or synced from the cloud). The one this PC used before is kept
    /// where it is. Windows Hello is turned off, since it belongs to the other album; turn it on again after unlocking.
    /// </summary>
    public static string OpenFolder(string folder)
    {
        if (IsVaultFolder(IOPath.Combine(folder, PortableFolderName))) folder = IOPath.Combine(folder, PortableFolderName);
        if (!IsVaultFolder(folder)) return "That folder doesn't have a Shutterbox Locked album in it. Choose the \"Shutterbox Locked album\" folder.";
        if (IsBusy) return "Wait until Shutterbox finishes adding or restoring.";
        Lock();
        WindowsHello.TurnOff();
        SettingsService.Current.LockedFolder = IOPath.GetFullPath(folder);
        SettingsService.Save();
        Raise();
        return null;
    }
    private static string InfoFile => IOPath.Combine(Folder, "vault.json");
    private static string IndexFile => IOPath.Combine(Folder, "index.sbox");
    private static string ItemsDir => IOPath.Combine(Folder, "items");
    private static string ThumbsDir => IOPath.Combine(Folder, "thumbs");
    /// <summary>Videos are decrypted here only while they play (a player needs a file); emptied on lock, close and start.</summary>
    public static string TempDir => IOPath.Combine(SettingsService.CacheFolder, "LockedPlayback");

    public static bool IsSetUp => File.Exists(InfoFile);
    public static bool IsUnlocked => _key != null;
    public static bool IsBusy => _busy > 0;
    public static int Count => _items.Count;
    public static string ItemPath(string id) => IOPath.Combine(ItemsDir, id + ".sbox");

    public static VaultInfo Info
    {
        get
        {
            try { return IsSetUp ? JsonSerializer.Deserialize<VaultInfo>(File.ReadAllText(InfoFile)) : null; }
            catch { return null; }
        }
    }

    /// <summary>The items as photos the timeline and viewer can show (empty while locked).</summary>
    public static IReadOnlyList<Photo> Photos
    {
        get
        {
            if (_key == null) return Array.Empty<Photo>();
            return _photos ??= _items.Select(ToPhoto).ToList();
        }
    }

    private static Photo ToPhoto(VaultItem i) => new()
    {
        Id = "locked-" + i.Id,
        VaultId = i.Id,
        OriginalName = i.Name,
        Path = ItemPath(i.Id),
        Size = i.Size,
        Taken = i.Taken,
        TakenSource = i.TakenSource,
        Width = i.Width,
        Height = i.Height,
        DurationSeconds = i.DurationSeconds,
        Make = i.Make,
        Model = i.Model,
        Latitude = i.Latitude,
        Longitude = i.Longitude,
        Orientation = i.Orientation,
        Rotation = i.Rotation,
        Added = i.Added,
    };

    private static void Raise()
    {
        _photos = null;
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Changed?.Invoke();
        else d.BeginInvoke(new Action(() => Changed?.Invoke()));
    }

    // ================================================================== set up, unlock, lock

    /// <summary>A PIN is 4–12 digits; a password is at least 6 characters.</summary>
    public static string Validate(string secret, string kind) => kind == "PIN"
        ? (secret.Length is >= 4 and <= 12 && secret.All(char.IsDigit) ? null : "A PIN is 4 to 12 digits.")
        : (secret.Length >= 6 ? null : "Use at least 6 characters for your password.");

    /// <summary>Sets up the Locked album and unlocks it. Returns the recovery key to show once.</summary>
    public static string SetUp(string secret, string kind)
    {
        if (IsSetUp) throw new InvalidOperationException("The Locked album is already set up.");
        var key = RandomNumberGenerator.GetBytes(32);
        var recovery = NewRecoveryKey();
        var salt = RandomNumberGenerator.GetBytes(16);
        var rsalt = RandomNumberGenerator.GetBytes(16);
        var info = new VaultInfo
        {
            Kind = kind,
            Salt = Convert.ToBase64String(salt),
            RecoverySalt = Convert.ToBase64String(rsalt),
            WrappedKey = Wrap(key, Derive(secret, salt, Iterations)),
            RecoveryWrappedKey = Wrap(key, Derive(NormalizeRecovery(recovery), rsalt, Iterations)),
        };
        Directory.CreateDirectory(ItemsDir);
        Directory.CreateDirectory(ThumbsDir);
        _key = key;
        _items = new List<VaultItem>();
        SaveIndex();
        SaveInfo(info);
        Raise();
        return recovery;
    }

    public static bool Unlock(string secret)
    {
        var info = Info;
        if (info == null) return false;
        var key = Unwrap(info.WrappedKey, Derive(secret, Convert.FromBase64String(info.Salt), info.Iterations));
        return key != null && Open(key);
    }

    public static bool UnlockWithRecoveryKey(string recoveryKey)
    {
        var info = Info;
        if (info == null) return false;
        var key = Unwrap(info.RecoveryWrappedKey, Derive(NormalizeRecovery(recoveryKey), Convert.FromBase64String(info.RecoverySalt), info.Iterations));
        return key != null && Open(key);
    }

    /// <summary>Opens with a key kept elsewhere (Windows Hello). False if it doesn't fit.</summary>
    internal static bool UnlockWithKey(byte[] key) => key is { Length: 32 } && Open(key);

    private static bool Open(byte[] key)
    {
        try
        {
            var items = File.Exists(IndexFile)
                ? JsonSerializer.Deserialize<List<VaultItem>>(DecryptBytes(File.ReadAllBytes(IndexFile), key)) ?? new()
                : new List<VaultItem>();
            _items = items;
            _key = key;
            LastActivity = DateTime.Now;
            Raise();
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>Forgets the key: nothing in the Locked album can be seen until it's unlocked again.</summary>
    public static void Lock()
    {
        if (_key == null) return;
        if (IsBusy) { _lockWhenDone = true; return; }
        foreach (var p in Photos) Thumbnails.Forget(p);
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _items = new();
        CleanTemp();
        Raise();
    }

    private static bool _lockWhenDone;

    /// <summary>Set when you use Shutterbox, for auto-lock.</summary>
    public static DateTime LastActivity { get; set; } = DateTime.Now;

    public static void ChangeSecret(string secret, string kind)
    {
        var info = Info ?? throw new InvalidOperationException("The Locked album isn't set up.");
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        var salt = RandomNumberGenerator.GetBytes(16);
        info.Kind = kind;
        info.Salt = Convert.ToBase64String(salt);
        info.Iterations = Iterations;
        info.WrappedKey = Wrap(_key, Derive(secret, salt, Iterations));
        SaveInfo(info);
    }

    public static void SetAutoLock(int minutes)
    {
        var info = Info;
        if (info == null) return;
        info.AutoLockMinutes = minutes;
        SaveInfo(info);
    }

    /// <summary>Deletes the Locked album and everything in it, for good (Windows Hello is turned off too).</summary>
    public static void Delete()
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        Lock();
        WindowsHello.TurnOff();
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
        Raise();
    }

    /// <summary>A copy of the open key (for Windows Hello), or null when locked.</summary>
    internal static byte[] CopyKey() => _key?.ToArray();

    // ================================================================== adding, opening, restoring

    /// <summary>
    /// Copies photos and videos into the Locked album, encrypted. The originals aren't touched.
    /// Items already in the album (same name and size) are skipped. Returns how many were added and what failed.
    /// </summary>
    public static async Task<(int added, int skipped, List<string> errors)> AddAsync(IReadOnlyList<Photo> photos, IProgress<(int done, int total)> progress = null)
    {
        if (_key == null) throw new InvalidOperationException("Unlock the Locked album first.");
        var key = _key.ToArray();
        Interlocked.Increment(ref _busy);
        var errors = new List<string>();
        int added = 0, skipped = 0;
        try
        {
            var have = new HashSet<(string, long)>(_items.Select(i => (i.Name.ToLowerInvariant(), i.Size)));
            Directory.CreateDirectory(ItemsDir);
            Directory.CreateDirectory(ThumbsDir);
            int done = 0;
            foreach (var p in photos)
            {
                progress?.Report((done, photos.Count));
                if (p.IsLocked || !File.Exists(p.Path)) { skipped++; done++; continue; }
                if (!have.Add((p.FileName.ToLowerInvariant(), p.Size))) { skipped++; done++; continue; }
                var item = new VaultItem
                {
                    Name = p.FileName, Size = p.Size, Taken = p.Taken, TakenSource = p.TakenSource, Width = p.Width, Height = p.Height,
                    DurationSeconds = p.DurationSeconds, Make = p.Make, Model = p.Model, Latitude = p.Latitude, Longitude = p.Longitude,
                    Orientation = p.Orientation, Rotation = p.Rotation,
                };
                var dest = ItemPath(item.Id);
                try
                {
                    await Task.Run(() =>
                    {
                        using var input = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                        using var output = File.Create(dest + ".tmp");
                        EncryptStream(input, output, key);
                    });
                    File.Move(dest + ".tmp", dest, true);
                    // a small encrypted thumbnail so the album opens quickly
                    var upright = p.Copy();
                    upright.Rotation = 0; // the album applies your rotation itself
                    var thumb = await Thumbnails.LoadAsync(upright, Thumbnails.CachePixels, 0);
                    if (thumb != null)
                    {
                        var jpg = EncodeJpeg(thumb);
                        File.WriteAllBytes(IOPath.Combine(ThumbsDir, item.Id + ".sbox"), EncryptBytes(jpg, key));
                        item.HasThumb = true;
                    }
                    if (_key != null) { _items.Add(item); SaveIndex(key); }
                    added++;
                }
                catch (Exception ex)
                {
                    try { File.Delete(dest + ".tmp"); File.Delete(dest); } catch { }
                    errors.Add($"{p.FileName}: {ex.Message}");
                }
                done++;
            }
            progress?.Report((done, photos.Count));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Interlocked.Decrement(ref _busy);
            Raise();
            if (_lockWhenDone && !IsBusy) { _lockWhenDone = false; Lock(); }
        }
        return (added, skipped, errors);
    }

    private static byte[] EncodeJpeg(BitmapSource img)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The whole decrypted file, in memory (photos).</summary>
    public static byte[] ReadBytes(string id)
    {
        var key = _key ?? throw new InvalidOperationException("The Locked album is locked.");
        using var input = File.OpenRead(ItemPath(id));
        using var output = new MemoryStream();
        DecryptStream(input, output, key);
        return output.ToArray();
    }

    public static byte[] ReadThumb(string id)
    {
        var key = _key;
        var path = IOPath.Combine(ThumbsDir, id + ".sbox");
        if (key == null || !File.Exists(path)) return null;
        try { return DecryptBytes(File.ReadAllBytes(path), key); }
        catch { return null; }
    }

    /// <summary>Decrypts a video to a temporary file so it can play. The file is deleted when you move on, lock or close Shutterbox.</summary>
    public static async Task<string> DecryptForPlaybackAsync(Photo p)
    {
        var key = _key?.ToArray() ?? throw new InvalidOperationException("The Locked album is locked.");
        Directory.CreateDirectory(TempDir);
        var path = IOPath.Combine(TempDir, Guid.NewGuid().ToString("N") + IOPath.GetExtension(p.FileName));
        try
        {
            await Task.Run(() =>
            {
                using var input = File.OpenRead(ItemPath(p.VaultId));
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.None);
                DecryptStream(input, output, key);
            });
            try { File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary); } catch { }
            return path;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var f in Directory.EnumerateFiles(TempDir))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Decrypts items back to ordinary files in a folder and takes them out of the Locked album.</summary>
    public static async Task<(int restored, List<string> errors)> RestoreAsync(IReadOnlyList<Photo> photos, string folder, IProgress<(int, int)> progress = null)
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        var key = _key.ToArray();
        Interlocked.Increment(ref _busy);
        var errors = new List<string>();
        int restored = 0, done = 0;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var p in photos.Where(x => x.IsLocked))
            {
                progress?.Report((done++, photos.Count));
                try
                {
                    var name = p.FileName;
                    var target = IOPath.Combine(folder, name);
                    for (int i = 2; File.Exists(target); i++)
                        target = IOPath.Combine(folder, $"{IOPath.GetFileNameWithoutExtension(name)} ({i}){IOPath.GetExtension(name)}");
                    await Task.Run(() =>
                    {
                        using var input = File.OpenRead(ItemPath(p.VaultId));
                        using var output = new FileStream(target, FileMode.CreateNew);
                        DecryptStream(input, output, key);
                    });
                    try { File.SetLastWriteTime(target, p.Taken); File.SetCreationTime(target, p.Taken); } catch { }
                    RemoveItem(p.VaultId, key);
                    restored++;
                }
                catch (Exception ex) { errors.Add($"{p.FileName}: {ex.Message}"); }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Interlocked.Decrement(ref _busy);
            Raise();
            if (_lockWhenDone && !IsBusy) { _lockWhenDone = false; Lock(); }
        }
        return (restored, errors);
    }

    /// <summary>Deletes items from the Locked album for good (they're encrypted, so there's no Recycle Bin for them).</summary>
    public static void Remove(IEnumerable<Photo> photos)
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        foreach (var p in photos.Where(x => x.IsLocked).ToList())
        {
            Thumbnails.Forget(p);
            RemoveItem(p.VaultId, _key);
        }
        Raise();
    }

    public static void Rotate(Photo p, int degrees)
    {
        var item = _items.FirstOrDefault(i => i.Id == p.VaultId);
        if (item == null || _key == null) return;
        item.Rotation = ((item.Rotation + degrees) % 360 + 360) % 360;
        SaveIndex();
        Raise();
    }

    private static void RemoveItem(string id, byte[] key)
    {
        _items.RemoveAll(i => i.Id == id);
        SaveIndex(key);
        try { File.Delete(ItemPath(id)); } catch { }
        try { File.Delete(IOPath.Combine(ThumbsDir, id + ".sbox")); } catch { }
    }

    private static void SaveIndex(byte[] key = null)
    {
        key ??= _key;
        if (key == null) return;
        var bytes = EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(_items), key);
        File.WriteAllBytes(IndexFile + ".tmp", bytes);
        File.Move(IndexFile + ".tmp", IndexFile, true);
    }

    private static void SaveInfo(VaultInfo info)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(InfoFile, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ================================================================== crypto

    private static byte[] Derive(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static string Wrap(byte[] key, byte[] wrappingKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[key.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(wrappingKey, TagSize)) aes.Encrypt(nonce, key, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    private static byte[] Unwrap(string wrapped, byte[] wrappingKey)
    {
        try
        {
            var data = Convert.FromBase64String(wrapped);
            var plain = new byte[data.Length - NonceSize - TagSize];
            using var aes = new AesGcm(wrappingKey, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
            return plain;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    private static byte[] ChunkNonce(byte[] baseNonce, long index)
    {
        var n = baseNonce.ToArray();
        for (int i = 0; i < 8; i++) n[NonceSize - 1 - i] ^= (byte)(index >> (8 * i));
        return n;
    }

    private static byte[] ChunkAad(long index, bool final)
    {
        var aad = new byte[9];
        BitConverter.TryWriteBytes(aad, index);
        aad[8] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static int ReadFull(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Encrypts a stream in 1 MB pieces: "SBOX1", a 12-byte nonce, then for each piece its 16-byte tag and the encrypted bytes.</summary>
    public static void EncryptStream(Stream input, Stream output, byte[] key)
    {
        var baseNonce = RandomNumberGenerator.GetBytes(NonceSize);
        output.Write(Magic);
        output.Write(baseNonce);
        using var aes = new AesGcm(key, TagSize);
        var buf = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        int n = ReadFull(input, buf, ChunkSize);
        for (long index = 0; ; index++)
        {
            int m = n == ChunkSize ? ReadFull(input, next, ChunkSize) : 0;
            bool final = m == 0;
            aes.Encrypt(ChunkNonce(baseNonce, index), buf.AsSpan(0, n), cipher.AsSpan(0, n), tag, ChunkAad(index, final));
            output.Write(tag);
            output.Write(cipher, 0, n);
            if (final) break;
            (buf, next) = (next, buf);
            n = m;
        }
        CryptographicOperations.ZeroMemory(buf);
        CryptographicOperations.ZeroMemory(next);
    }

    public static void DecryptStream(Stream input, Stream output, byte[] key)
    {
        var head = new byte[Magic.Length + NonceSize];
        if (ReadFull(input, head, head.Length) != head.Length || !head.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("This isn't a Shutterbox Locked album file.");
        var baseNonce = head.AsSpan(Magic.Length, NonceSize).ToArray();
        using var aes = new AesGcm(key, TagSize);
        var tag = new byte[TagSize];
        var cipher = new byte[ChunkSize];
        var plain = new byte[ChunkSize];
        for (long index = 0; ; index++)
        {
            if (ReadFull(input, tag, TagSize) != TagSize) throw new InvalidDataException("The file is cut short.");
            int n = ReadFull(input, cipher, ChunkSize);
            bool final = input.Position >= input.Length;
            aes.Decrypt(ChunkNonce(baseNonce, index), cipher.AsSpan(0, n), tag, plain.AsSpan(0, n), ChunkAad(index, final));
            output.Write(plain, 0, n);
            if (final) break;
        }
        CryptographicOperations.ZeroMemory(plain);
    }

    public static byte[] EncryptBytes(byte[] data, byte[] key)
    {
        using var input = new MemoryStream(data, false);
        using var output = new MemoryStream();
        EncryptStream(input, output, key);
        return output.ToArray();
    }

    public static byte[] DecryptBytes(byte[] data, byte[] key)
    {
        using var input = new MemoryStream(data, false);
        using var output = new MemoryStream();
        DecryptStream(input, output, key);
        return output.ToArray();
    }

    /// <summary>"ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2": 25 letters and digits, easy to copy down.</summary>
    private static string NewRecoveryKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = Enumerable.Range(0, 25).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
        return string.Join("-", Enumerable.Range(0, 5).Select(i => new string(chars, i * 5, 5)));
    }

    private static string NormalizeRecovery(string key) => new((key ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}
