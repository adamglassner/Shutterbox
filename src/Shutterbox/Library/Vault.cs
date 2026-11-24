using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shutterbox.Services;

namespace Shutterbox.Library;

/// <summary>
/// An item in the Locked album: in the old index (0.2.0, 0.3.0) and, since 0.4.0, its own record file with more details.
/// See docs/locked-album-format.md.
/// </summary>
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

    // since 0.4.0 (records only)
    public string Caption { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    public List<string> AlbumIds { get; set; } = new();
    /// <summary>UTC. The newest record of an item wins when sync apps leave conflict copies.</summary>
    public DateTime Modified { get; set; } = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc);
    public bool Deleted { get; set; }

    /// <summary>The protected album it's in (not saved; null: the main album).</summary>
    [JsonIgnore] public string ProtectedIn { get; set; }

    public VaultItem Clone() { var c = (VaultItem)MemberwiseClone(); c.Tags = new(Tags); c.AlbumIds = new(AlbumIds); return c; }
}

/// <summary>The index.sbox form of an item (what 0.2.0 and 0.3.0 read).</summary>
public sealed class IndexItem
{
    public string Id { get; set; }
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
    public DateTime Added { get; set; }
    public bool HasThumb { get; set; }

    public static IndexItem From(VaultItem i) => new()
    {
        Id = i.Id, Name = i.Name, Size = i.Size, Taken = i.Taken, TakenSource = i.TakenSource, Width = i.Width, Height = i.Height,
        DurationSeconds = i.DurationSeconds, Make = i.Make, Model = i.Model, Latitude = i.Latitude, Longitude = i.Longitude,
        Orientation = i.Orientation, Rotation = i.Rotation, Added = i.Added, HasThumb = i.HasThumb,
    };
}

/// <summary>How an album with its own PIN or password unwraps its key.</summary>
public sealed class AlbumProtection
{
    public string Kind { get; set; } = "PIN";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = Vault.Iterations;
    public string WrappedKey { get; set; } = "";
}

/// <summary>An album inside the Locked album.</summary>
public sealed class VaultAlbum
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Only in the old index; since 0.4.0 each item lists its albums.</summary>
    public List<string> ItemIds { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc);
    public bool Deleted { get; set; }
    public string CoverId { get; set; }
    public AlbumProtection Protected { get; set; }

    [JsonIgnore] public bool IsProtected => Protected != null;
    [JsonIgnore] public bool IsOpen => Key != null;
    [JsonIgnore] internal byte[] Key { get; set; }
    [JsonIgnore] internal Dictionary<string, VaultItem> Records { get; set; } = new();
}

/// <summary>albums/&lt;id&gt;.sbox</summary>
internal sealed class AlbumRecord
{
    public string Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public bool Deleted { get; set; }
    public string CoverId { get; set; }
    public AlbumProtection Protected { get; set; }
}

/// <summary>The old index (version 1 was just the list of items).</summary>
public sealed class VaultIndex
{
    public int Version { get; set; } = 2;
    public List<IndexItem> Items { get; set; } = new();
    public List<IndexAlbum> Albums { get; set; } = new();
}

public sealed class IndexAlbum
{
    public string Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> ItemIds { get; set; } = new();
    public DateTime Created { get; set; }
}

/// <summary>Hashes what's written to it (to check an encrypted copy decrypts back exactly).</summary>
internal sealed class HashSink : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;
    public byte[] Finish() => _hash.GetHashAndReset();
    public override void Write(byte[] buffer, int offset, int count) { _hash.AppendData(buffer, offset, count); _length += count; }
    public override void Write(ReadOnlySpan<byte> buffer) { _hash.AppendData(buffer); _length += buffer.Length; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
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
    /// <summary>Never changes, even when the recovery key does (since 0.4.0).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Id { get; set; }
    /// <summary>Anything a newer version added, kept as it is.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
}

public sealed record FailedAttempt(DateTime Time, string What, string Device);
public sealed record IntruderPhoto(string Path, DateTime? Time);

/// <summary>
/// The Locked album: copies of photos and videos that Shutterbox encrypts so they can't be opened outside it.
///
/// A random 256-bit key encrypts every file with AES-256-GCM, in 1 MB pieces so long videos never have to fit in memory;
/// each piece is authenticated with its position and whether it's the last, so a file can't be cut short or reordered
/// without it being noticed. The key is stored only wrapped: with a key derived from your PIN or password
/// (PBKDF2-SHA256, 600,000 rounds), with a recovery key shown when you set it up, and optionally for Windows Hello.
/// Since 0.4.0 each item's details are in their own encrypted record, so a PC and a phone can share the album through a
/// cloud folder without overwriting each other's changes. The format is in docs/locked-album-format.md and is the same
/// on Android.
/// </summary>
public static class Vault
{
    public const int Iterations = 600_000;
    private const int ChunkSize = 1 << 20;
    private const int NonceSize = 12, TagSize = 16;
    private static readonly byte[] Magic = "SBOX1"u8.ToArray();
    private static readonly byte[] IntruderMagic = "SBXI1"u8.ToArray();

    private static byte[] _key;
    private static Dictionary<string, VaultItem> _records = new();
    private static Dictionary<string, VaultAlbum> _albums = new();
    private static List<Photo> _photos;
    private static int _busy;
    private static readonly object _gate = new();

    public static event Action Changed;

    public static string DefaultFolder => IOPath.Combine(SettingsService.Folder, "Locked");

    /// <summary>The Locked album's folder. It's self-contained, so it can live in OneDrive, Dropbox, on a USB drive or another PC.</summary>
    public static string Folder => string.IsNullOrWhiteSpace(SettingsService.Current.LockedFolder) ? DefaultFolder : SettingsService.Current.LockedFolder;

    /// <summary>The name Shutterbox gives the folder when you move the album somewhere.</summary>
    public const string PortableFolderName = "Shutterbox Locked album";

    /// <summary>The wrong guesses shown after the last unlock (since the one before it).</summary>
    public static IReadOnlyList<FailedAttempt> LastUnlockWarnings { get; private set; } = Array.Empty<FailedAttempt>();

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
    private static string Base(string protectedIn) => protectedIn == null ? Folder : IOPath.Combine(Folder, "protected", protectedIn);
    private static string ItemsDir => IOPath.Combine(Folder, "items");
    private static string ThumbsDir => IOPath.Combine(Folder, "thumbs");
    /// <summary>Videos are decrypted here only while they play, and items while they're shared; emptied on lock, close and start.</summary>
    public static string TempDir => IOPath.Combine(SettingsService.CacheFolder, "LockedPlayback");

    public static bool IsSetUp => File.Exists(InfoFile);
    public static bool IsUnlocked => _key != null;
    public static bool IsBusy => _busy > 0;
    public static int Count => Live.Count();
    public static string ItemPath(string id, string protectedIn = null) => IOPath.Combine(Base(protectedIn), "items", id + ".sbox");
    public static string ThumbPath(string id, string protectedIn = null) => IOPath.Combine(Base(protectedIn), "thumbs", id + ".sbox");
    private static string RecordPath(string id, string protectedIn = null) => IOPath.Combine(Base(protectedIn), "records", id + ".sbox");

    private static IEnumerable<VaultItem> Live => _records.Values.Where(r => !r.Deleted);

    private static readonly JsonSerializerOptions Compact = new();

    public static VaultInfo Info
    {
        get
        {
            try { return IsSetUp ? JsonSerializer.Deserialize<VaultInfo>(File.ReadAllText(InfoFile)) : null; }
            catch { return null; }
        }
    }

    /// <summary>Never changes for this album (even when the recovery key does).</summary>
    public static string Identity => Info is { } i ? i.Id ?? i.RecoverySalt : null;

    /// <summary>The main album's items as photos the timeline and viewer can show (empty while locked).</summary>
    public static IReadOnlyList<Photo> Photos
    {
        get
        {
            if (_key == null) return Array.Empty<Photo>();
            return _photos ??= Live.Select(ToPhoto).ToList();
        }
    }

    private static Photo ToPhoto(VaultItem i) => new()
    {
        Id = "locked-" + i.Id,
        VaultId = i.Id,
        ProtectedIn = i.ProtectedIn,
        OriginalName = i.Name,
        Path = ItemPath(i.Id, i.ProtectedIn),
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
        Caption = i.Caption,
        Tags = new List<string>(i.Tags),
        Favorite = i.Favorite,
    };

    private static VaultItem RecordOf(Photo p)
    {
        if (p?.VaultId == null) return null;
        if (p.ProtectedIn != null) return _albums.TryGetValue(p.ProtectedIn, out var a) && a.Records.TryGetValue(p.VaultId, out var r) ? r : null;
        return _records.TryGetValue(p.VaultId, out var m) ? m : null;
    }

    private static byte[] KeyFor(string protectedIn)
    {
        var k = protectedIn == null ? _key : (_albums.TryGetValue(protectedIn, out var a) ? a.Key : null);
        return k ?? throw new InvalidOperationException(protectedIn == null ? "The Locked album is locked." : "That album is locked.");
    }

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
        var recovery = NewRecoveryKeyText();
        var salt = RandomNumberGenerator.GetBytes(16);
        var rsalt = RandomNumberGenerator.GetBytes(16);
        var info = new VaultInfo
        {
            Kind = kind,
            Id = Guid.NewGuid().ToString("N"),
            Salt = Convert.ToBase64String(salt),
            RecoverySalt = Convert.ToBase64String(rsalt),
            WrappedKey = Wrap(key, Derive(secret, salt, Iterations)),
            RecoveryWrappedKey = Wrap(key, Derive(NormalizeRecovery(recovery), rsalt, Iterations)),
        };
        foreach (var d in new[] { "items", "thumbs", "records", "albums" }) Directory.CreateDirectory(IOPath.Combine(Folder, d));
        _key = key;
        _records = new();
        _albums = new();
        SaveIndex();
        SaveInfo(info);
        NoteUnlock();
        Raise();
        return recovery;
    }

    /// <summary>Unlocks with the PIN or password. A wrong one is logged (see <see cref="FailedAttempts"/>) unless <paramref name="log"/> is false.</summary>
    public static bool Unlock(string secret, bool log = true)
    {
        var info = Info;
        if (info == null) return false;
        var key = Unwrap(info.WrappedKey, Derive(secret, Convert.FromBase64String(info.Salt), info.Iterations));
        var ok = key != null && Open(key);
        if (!ok && log) RecordFailure(info.Kind == "Password" ? "Password" : "PIN");
        return ok;
    }

    public static bool UnlockWithRecoveryKey(string recoveryKey)
    {
        var info = Info;
        if (info == null) return false;
        var key = Unwrap(info.RecoveryWrappedKey, Derive(NormalizeRecovery(recoveryKey), Convert.FromBase64String(info.RecoverySalt), info.Iterations));
        var ok = key != null && Open(key);
        if (!ok) RecordFailure("Recovery key");
        return ok;
    }

    /// <summary>Opens with a key kept elsewhere (Windows Hello). False if it doesn't fit.</summary>
    internal static bool UnlockWithKey(byte[] key) => key is { Length: 32 } && Open(key);

    private static bool Open(byte[] key)
    {
        try
        {
            var (records, albums, untidy) = Load(key);
            _records = records;
            _albums = albums;
            _key = key;
            LastActivity = DateTime.Now;
            EnsureId();
            if (untidy) Tidy();
            LastUnlockWarnings = FailedSinceLastUnlock();
            NoteUnlock();
            Raise();
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>Reads what other devices changed (through a cloud folder) while it's unlocked.</summary>
    public static void Reload()
    {
        var key = _key;
        if (key == null || IsBusy) return;
        try
        {
            var open = _albums.Values.Where(a => a.IsOpen).ToDictionary(a => a.Id, a => a.Key);
            var (records, albums, untidy) = Load(key);
            _records = records;
            _albums = albums;
            foreach (var (id, k) in open)
                if (_albums.TryGetValue(id, out var a) && a.IsProtected) { a.Key = k; a.Records = ReadRecords(IOPath.Combine(Base(id), "records"), k, id, out _); }
            if (untidy) Tidy();
            Raise();
        }
        catch { }
    }

    private static (Dictionary<string, VaultItem>, Dictionary<string, VaultAlbum>, bool) Load(byte[] key)
    {
        var records = ReadRecords(IOPath.Combine(Folder, "records"), key, null, out var untidy);
        var albums = new Dictionary<string, VaultAlbum>();
        var albumDir = IOPath.Combine(Folder, "albums");
        if (Directory.Exists(albumDir))
            foreach (var f in Directory.EnumerateFiles(albumDir, "*.sbox"))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<AlbumRecord>(DecryptBytes(File.ReadAllBytes(f), key));
                    if (r?.Id == null) continue;
                    if (IOPath.GetFileName(f) != r.Id + ".sbox") untidy = true;
                    var a = new VaultAlbum { Id = r.Id, Name = r.Name, Created = r.Created, Modified = r.Modified.ToUniversalTime(), Deleted = r.Deleted, CoverId = r.CoverId, Protected = r.Protected };
                    if (!albums.TryGetValue(a.Id, out var have) || a.Modified > have.Modified) albums[a.Id] = a;
                }
                catch { }
            }
        // the old index: items added by Shutterbox 0.2.0 or 0.3.0
        if (File.Exists(IndexFile))
        {
            var (items, oldAlbums) = ParseIndex(DecryptBytes(File.ReadAllBytes(IndexFile), key));
            foreach (var a in oldAlbums)
                if (!albums.ContainsKey(a.Id)) { a.Modified = DateTime.UtcNow; albums[a.Id] = a; untidy = true; }
            foreach (var i in items)
            {
                if (records.ContainsKey(i.Id) || !File.Exists(ItemPath(i.Id))) continue;
                i.AlbumIds = oldAlbums.Where(a => a.ItemIds.Contains(i.Id)).Select(a => a.Id).ToList();
                i.Modified = DateTime.UtcNow;
                records[i.Id] = i;
                untidy = true;
            }
        }
        return (records, albums, untidy);
    }

    /// <summary>Reads a records folder. Conflict copies from sync apps count too: the latest Modified wins.</summary>
    private static Dictionary<string, VaultItem> ReadRecords(string dir, byte[] key, string protectedIn, out bool strays)
    {
        strays = false;
        var outp = new Dictionary<string, VaultItem>();
        if (!Directory.Exists(dir)) return outp;
        foreach (var f in Directory.EnumerateFiles(dir, "*.sbox"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<VaultItem>(DecryptBytes(File.ReadAllBytes(f), key));
                if (r?.Id == null) continue;
                r.Modified = r.Modified.ToUniversalTime();
                r.Tags ??= new();
                r.AlbumIds ??= new();
                r.Caption ??= "";
                r.ProtectedIn = protectedIn;
                if (IOPath.GetFileName(f) != r.Id + ".sbox") strays = true;
                if (!outp.TryGetValue(r.Id, out var have) || r.Modified > have.Modified) outp[r.Id] = r;
            }
            catch { }
        }
        return outp;
    }

    /// <summary>Writes the merged records and albums under their own names and removes conflict copies.</summary>
    private static void Tidy()
    {
        foreach (var r in _records.Values) WriteRecord(r, touch: false);
        foreach (var a in _albums.Values) WriteAlbum(a, touch: false);
        foreach (var dir in new[] { IOPath.Combine(Folder, "records"), IOPath.Combine(Folder, "albums") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.sbox"))
                if (!System.Text.RegularExpressions.Regex.IsMatch(IOPath.GetFileName(f), "^[0-9a-f]{32}\\.sbox$"))
                    try { File.Delete(f); } catch { }
        }
        SaveIndex();
    }

    /// <summary>Forgets the key (and any open protected album's): nothing can be seen until it's unlocked again.</summary>
    public static void Lock()
    {
        if (_key == null) return;
        if (IsBusy) { _lockWhenDone = true; return; }
        foreach (var p in Photos) Thumbnails.Forget(p);
        foreach (var a in _albums.Values)
        {
            foreach (var p in PhotosIn(a)) Thumbnails.Forget(p);
            if (a.Key != null) CryptographicOperations.ZeroMemory(a.Key);
            a.Key = null;
            a.Records = new();
        }
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _records = new();
        _albums = new();
        CleanTemp();
        Raise();
    }

    private static bool _lockWhenDone;

    private static void MaybeLock()
    {
        if (_lockWhenDone && !IsBusy) { _lockWhenDone = false; Lock(); }
    }

    /// <summary>Set when you use Shutterbox, for auto-lock.</summary>
    public static DateTime LastActivity { get; set; } = DateTime.Now;

    private static void EnsureId()
    {
        var info = Info;
        if (info == null || info.Id != null) return;
        info.Id = string.IsNullOrEmpty(info.RecoverySalt) ? Guid.NewGuid().ToString("N") : info.RecoverySalt;
        SaveInfo(info);
    }

    public static void ChangeSecret(string secret, string kind)
    {
        var info = Info ?? throw new InvalidOperationException("The Locked album isn't set up.");
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        var salt = RandomNumberGenerator.GetBytes(16);
        // one Iterations for both the PIN and the recovery key (vault.json has one)
        info.Kind = kind;
        info.Salt = Convert.ToBase64String(salt);
        info.WrappedKey = Wrap(_key, Derive(secret, salt, info.Iterations));
        SaveInfo(info);
    }

    /// <summary>Replaces the recovery key (the old one stops working). Returns the new one to show once.</summary>
    public static string NewRecoveryKey()
    {
        var info = Info ?? throw new InvalidOperationException("The Locked album isn't set up.");
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        info.Id ??= info.RecoverySalt;
        var recovery = NewRecoveryKeyText();
        var rsalt = RandomNumberGenerator.GetBytes(16);
        info.RecoverySalt = Convert.ToBase64String(rsalt);
        info.RecoveryWrappedKey = Wrap(_key, Derive(NormalizeRecovery(recovery), rsalt, info.Iterations));
        SaveInfo(info);
        return recovery;
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

    // ================================================================== adding

    /// <summary>
    /// Copies photos and videos into the Locked album, encrypted. The originals aren't touched.
    /// Items already in it (same name and size) are skipped, but still go into the album.
    /// </summary>
    /// <param name="albumId">Also put them in this album inside the Locked album (a protected one, if open, gets them encrypted with its key).</param>
    /// <param name="verify">Check each encrypted copy decrypts back to exactly the original (for moving, before the original is deleted).</param>
    /// <returns>Also the photos that are now safely in the Locked album (added, and checked when <paramref name="verify"/> is set).</returns>
    public static async Task<(int added, int skipped, List<string> errors, List<Photo> secured)> AddAsync(IReadOnlyList<Photo> photos,
        IProgress<(int done, int total)> progress = null, string albumId = null, bool verify = false)
    {
        var secured = new List<Photo>();
        if (_key == null) throw new InvalidOperationException("Unlock the Locked album first.");
        var album = albumId == null ? null : GetAlbum(albumId);
        var protectedIn = album is { IsProtected: true } ? album.Id : null;
        var key = KeyFor(protectedIn).ToArray();
        var target = protectedIn == null ? _records : album.Records;
        Interlocked.Increment(ref _busy);
        var errors = new List<string>();
        int added = 0, skipped = 0;
        try
        {
            var have = new HashSet<(string, long)>(target.Values.Where(i => !i.Deleted).Select(i => (i.Name.ToLowerInvariant(), i.Size)));
            foreach (var d in new[] { "items", "thumbs", "records" }) Directory.CreateDirectory(IOPath.Combine(Base(protectedIn), d));
            int done = 0;
            foreach (var p in photos)
            {
                progress?.Report((done, photos.Count));
                if (p.IsLocked || !File.Exists(p.Path)) { skipped++; done++; continue; }
                if (!have.Add((p.FileName.ToLowerInvariant(), p.Size)))
                {
                    // already locked: it can still go into the album
                    var existing = target.Values.FirstOrDefault(i => !i.Deleted && i.Size == p.Size && string.Equals(i.Name, p.FileName, StringComparison.OrdinalIgnoreCase));
                    if (album != null && protectedIn == null && existing != null && !existing.AlbumIds.Contains(album.Id)) { existing.AlbumIds.Add(album.Id); WriteRecord(existing); }
                    skipped++; done++; continue;
                }
                var item = new VaultItem
                {
                    Name = p.FileName, Size = p.Size, Taken = p.Taken, TakenSource = p.TakenSource, Width = p.Width, Height = p.Height,
                    DurationSeconds = p.DurationSeconds, Make = p.Make, Model = p.Model, Latitude = p.Latitude, Longitude = p.Longitude,
                    Orientation = p.Orientation, Rotation = p.Rotation, Caption = p.Caption ?? "", Tags = new(p.Tags ?? new()), Favorite = p.Favorite,
                    ProtectedIn = protectedIn,
                };
                if (album != null && protectedIn == null) item.AlbumIds.Add(album.Id);
                var dest = ItemPath(item.Id, protectedIn);
                try
                {
                    await Task.Run(() =>
                    {
                        using var input = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                        using var output = File.Create(dest + ".tmp");
                        EncryptStream(input, output, key);
                    });
                    File.Move(dest + ".tmp", dest, true);
                    if (verify && !await Task.Run(() => SameContent(p.Path, dest, key)))
                        throw new IOException("the encrypted copy didn't check out, so nothing was changed");
                    // a small encrypted thumbnail so the album opens quickly
                    var upright = p.Copy();
                    upright.Rotation = 0; // the album applies your rotation itself
                    var thumb = await Thumbnails.LoadAsync(upright, Thumbnails.CachePixels, 0);
                    if (thumb != null)
                    {
                        File.WriteAllBytes(ThumbPath(item.Id, protectedIn), EncryptBytes(EncodeJpeg(thumb), key));
                        item.HasThumb = true;
                    }
                    if (_key != null)
                    {
                        target[item.Id] = item;
                        WriteRecord(item);
                    }
                    added++;
                    secured.Add(p);
                }
                catch (Exception ex)
                {
                    try { File.Delete(dest + ".tmp"); File.Delete(dest); } catch { }
                    errors.Add($"{p.FileName}: {ex.Message}");
                }
                done++;
            }
            progress?.Report((done, photos.Count));
            if (_key != null) SaveIndex();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Interlocked.Decrement(ref _busy);
            Raise();
            MaybeLock();
        }
        return (added, skipped, errors, secured);
    }

    /// <summary>True when the encrypted file decrypts to exactly the original (SHA-256 of both).</summary>
    private static bool SameContent(string original, string encrypted, byte[] key)
    {
        byte[] a, b;
        using (var fs = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            a = SHA256.HashData(fs);
        using (var input = File.OpenRead(encrypted))
        using (var sink = new HashSink())
        {
            DecryptStream(input, sink, key);
            b = sink.Finish();
        }
        return a.AsSpan().SequenceEqual(b);
    }

    // ================================================================== details and search

    /// <summary>Caption, tags and favorite, kept encrypted with the item.</summary>
    public static void SetDetails(Photo p, string caption = null, List<string> tags = null, bool? favorite = null)
    {
        var r = RecordOf(p);
        if (r == null) return;
        if (caption != null) r.Caption = caption.Trim();
        if (tags != null) r.Tags = tags;
        if (favorite != null) r.Favorite = favorite.Value;
        p.Caption = r.Caption;
        p.Tags = new(r.Tags);
        p.Favorite = r.Favorite;
        WriteRecord(r);
        Raise();
    }

    /// <summary>The words search looks at for a Locked album item.</summary>
    public static string SearchText(Photo p) => string.Join(" ", new[]
    {
        p.FileName, p.Caption ?? "", string.Join(" ", p.Tags ?? new()), p.Make ?? "", p.Model ?? "",
        string.Join(" ", AlbumsOf(p).Select(a => a.Name)),
        System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(p.Taken.Month), p.Taken.Year.ToString(),
        p.Favorite ? "favorite" : "", p.IsVideo ? "video" : "photo",
    }).ToLowerInvariant();

    // ================================================================== albums inside the Locked album

    /// <summary>The albums inside it, by name (none while locked).</summary>
    public static IReadOnlyList<VaultAlbum> Albums => _key == null
        ? Array.Empty<VaultAlbum>()
        : _albums.Values.Where(a => !a.Deleted).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static VaultAlbum GetAlbum(string id) => id == null || _key == null ? null : (_albums.TryGetValue(id, out var a) && !a.Deleted ? a : null);

    /// <summary>Reads the decrypted index; accepts version 1 (a plain list of items) too.</summary>
    internal static (List<VaultItem> items, List<VaultAlbum> albums) ParseIndex(byte[] json)
    {
        var text = Encoding.UTF8.GetString(json).TrimStart();
        if (text.StartsWith("["))
            return (JsonSerializer.Deserialize<List<VaultItem>>(text) ?? new(), new List<VaultAlbum>());
        var index = JsonSerializer.Deserialize<VaultIndexRead>(text) ?? new VaultIndexRead();
        index.Items ??= new();
        index.Albums ??= new();
        var ids = new HashSet<string>(index.Items.Select(i => i.Id));
        foreach (var a in index.Albums) { a.ItemIds ??= new(); a.ItemIds.RemoveAll(id => !ids.Contains(id)); }
        return (index.Items, index.Albums);
    }

    private sealed class VaultIndexRead
    {
        public int Version { get; set; }
        public List<VaultItem> Items { get; set; } = new();
        public List<VaultAlbum> Albums { get; set; } = new();
    }

    private static string UniqueAlbumName(string name, VaultAlbum except = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "New album" : name.Trim();
        var n = name;
        for (int i = 2; Albums.Any(a => a != except && string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} ({i})";
        return n;
    }

    public static VaultAlbum CreateAlbum(string name)
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        var a = new VaultAlbum { Name = UniqueAlbumName(name) };
        _albums[a.Id] = a;
        WriteAlbum(a);
        SaveIndex();
        Raise();
        return a;
    }

    public static void RenameAlbum(VaultAlbum a, string name)
    {
        if (_key == null) return;
        a.Name = UniqueAlbumName(name, a);
        WriteAlbum(a);
        SaveIndex();
        Raise();
    }

    /// <summary>Deletes the album. A normal album's items stay in the Locked album; a protected (open) album's go back into it.</summary>
    public static void DeleteAlbum(VaultAlbum a)
    {
        if (_key == null) return;
        if (a.IsProtected)
        {
            if (!a.IsOpen) throw new InvalidOperationException("Open the album first.");
            UnprotectAlbum(a);
        }
        a.Deleted = true;
        foreach (var r in _records.Values.Where(r => r.AlbumIds.Contains(a.Id)).ToList()) { r.AlbumIds.Remove(a.Id); WriteRecord(r); }
        WriteAlbum(a);
        SaveIndex();
        Raise();
    }

    public static void SetCover(VaultAlbum a, Photo p)
    {
        a.CoverId = p.VaultId;
        WriteAlbum(a);
        Raise();
    }

    /// <summary>Adds items to an album. Into a protected (open) album they're moved: re-encrypted with its key.</summary>
    public static int AddToAlbum(VaultAlbum a, IEnumerable<Photo> photos)
    {
        if (_key == null) return 0;
        var list = photos.Where(x => x.IsLocked).ToList();
        if (a.IsProtected) return MoveInto(a, list);
        int n = 0;
        foreach (var p in list)
        {
            var r = RecordOf(p);
            if (r == null || r.ProtectedIn != null || r.AlbumIds.Contains(a.Id)) continue;
            r.AlbumIds.Add(a.Id);
            WriteRecord(r);
            n++;
        }
        SaveIndex();
        Raise();
        return n;
    }

    public static void RemoveFromAlbum(VaultAlbum a, IEnumerable<Photo> photos)
    {
        if (_key == null) return;
        var list = photos.Where(x => x.IsLocked).ToList();
        if (a.IsProtected) MoveOut(a, list.Where(p => p.ProtectedIn == a.Id).ToList(), keepInAlbum: false);
        else
            foreach (var p in list)
                if (RecordOf(p) is { } r && r.AlbumIds.Remove(a.Id)) WriteRecord(r);
        SaveIndex();
        Raise();
    }

    public static IEnumerable<Photo> PhotosIn(VaultAlbum a)
    {
        if (a.IsProtected) return a.IsOpen ? a.Records.Values.Where(r => !r.Deleted).Select(ToPhoto).ToList() : Enumerable.Empty<Photo>();
        return Photos.Where(p => _records.TryGetValue(p.VaultId, out var r) && r.AlbumIds.Contains(a.Id));
    }

    public static IEnumerable<VaultAlbum> AlbumsOf(Photo p)
    {
        if (p?.IsLocked != true) return Enumerable.Empty<VaultAlbum>();
        if (p.ProtectedIn != null) return _albums.TryGetValue(p.ProtectedIn, out var pa) ? new[] { pa } : Enumerable.Empty<VaultAlbum>();
        var ids = _records.TryGetValue(p.VaultId, out var r) ? r.AlbumIds : new List<string>();
        return Albums.Where(a => ids.Contains(a.Id));
    }

    // ================================================================== albums with their own PIN

    /// <summary>Gives an album its own PIN or password: its items are re-encrypted with a key only that opens.</summary>
    public static void ProtectAlbum(VaultAlbum a, string secret, string kind)
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        if (a.IsProtected) throw new InvalidOperationException("It already has its own PIN or password.");
        var key = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var members = PhotosIn(a).ToList();
        a.Protected = new AlbumProtection { Kind = kind, Salt = Convert.ToBase64String(salt), WrappedKey = Wrap(key, Derive(secret, salt, Iterations)) };
        a.Key = key;
        a.Records = new();
        WriteAlbum(a);
        MoveInto(a, members);
        SaveIndex();
        Raise();
    }

    /// <summary>Opens a protected album with its PIN or password.</summary>
    public static bool UnlockAlbum(VaultAlbum a, string secret)
    {
        var pr = a.Protected;
        if (pr == null || _key == null) return false;
        var key = Unwrap(pr.WrappedKey, Derive(secret, Convert.FromBase64String(pr.Salt), pr.Iterations));
        if (key == null) { RecordFailure("Album PIN"); return false; }
        a.Key = key;
        a.Records = ReadRecords(IOPath.Combine(Base(a.Id), "records"), key, a.Id, out _);
        LastActivity = DateTime.Now;
        Raise();
        return true;
    }

    public static void LockAlbum(VaultAlbum a)
    {
        foreach (var p in PhotosIn(a)) Thumbnails.Forget(p);
        if (a.Key != null) CryptographicOperations.ZeroMemory(a.Key);
        a.Key = null;
        a.Records = new();
        Raise();
    }

    /// <summary>Takes the album's own PIN off: its items go back into the Locked album (in this album).</summary>
    public static void UnprotectAlbum(VaultAlbum a)
    {
        if (!a.IsProtected) return;
        if (!a.IsOpen) throw new InvalidOperationException("Open the album first.");
        MoveOut(a, PhotosIn(a).ToList(), keepInAlbum: true);
        a.Protected = null;
        LockAlbum(a);
        WriteAlbum(a);
        try { Directory.Delete(Base(a.Id), true); } catch { }
        SaveIndex();
        Raise();
    }

    public static void ChangeAlbumSecret(VaultAlbum a, string secret, string kind)
    {
        var key = a.Key ?? throw new InvalidOperationException("Open the album first.");
        var salt = RandomNumberGenerator.GetBytes(16);
        a.Protected = new AlbumProtection { Kind = kind, Salt = Convert.ToBase64String(salt), WrappedKey = Wrap(key, Derive(secret, salt, Iterations)) };
        WriteAlbum(a);
        Raise();
    }

    private static int MoveInto(VaultAlbum a, List<Photo> list)
    {
        var akey = a.Key ?? throw new InvalidOperationException("Open the album first.");
        int n = 0;
        Interlocked.Increment(ref _busy);
        try
        {
            foreach (var d in new[] { "items", "thumbs", "records" }) Directory.CreateDirectory(IOPath.Combine(Base(a.Id), d));
            foreach (var p in list)
            {
                if (!_records.TryGetValue(p.VaultId, out var r) || r.Deleted) continue;
                Reencrypt(r.Id, _key, null, akey, a.Id, r.HasThumb);
                var moved = r.Clone();
                moved.ProtectedIn = a.Id;
                moved.AlbumIds = new();
                a.Records[moved.Id] = moved;
                WriteRecord(moved);
                Tombstone(r);
                n++;
            }
        }
        finally { Interlocked.Decrement(ref _busy); }
        SaveIndex();
        Raise();
        return n;
    }

    private static void MoveOut(VaultAlbum a, List<Photo> list, bool keepInAlbum)
    {
        var akey = a.Key ?? throw new InvalidOperationException("Open the album first.");
        Interlocked.Increment(ref _busy);
        try
        {
            foreach (var p in list)
            {
                if (!a.Records.TryGetValue(p.VaultId, out var r) || r.Deleted) continue;
                Reencrypt(r.Id, akey, a.Id, _key, null, r.HasThumb);
                var moved = r.Clone();
                moved.ProtectedIn = null;
                moved.AlbumIds = keepInAlbum ? new() { a.Id } : new();
                _records[moved.Id] = moved;
                WriteRecord(moved);
                Tombstone(r);
            }
        }
        finally { Interlocked.Decrement(ref _busy); }
    }

    private static void Reencrypt(string id, byte[] fromKey, string fromAlbum, byte[] toKey, string toAlbum, bool thumb)
    {
        var dest = ItemPath(id, toAlbum);
        using (var input = File.OpenRead(ItemPath(id, fromAlbum)))
        using (var plain = new FileStream(IOPath.Combine(Directory.CreateDirectory(TempDir).FullName, "move-" + id), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose))
        {
            DecryptStream(input, plain, fromKey);
            plain.Position = 0;
            using var output = File.Create(dest + ".tmp");
            EncryptStream(plain, output, toKey);
        }
        File.Move(dest + ".tmp", dest, true);
        if (thumb && File.Exists(ThumbPath(id, fromAlbum)))
            File.WriteAllBytes(ThumbPath(id, toAlbum), EncryptBytes(DecryptBytes(File.ReadAllBytes(ThumbPath(id, fromAlbum)), fromKey), toKey));
    }

    /// <summary>Marks an item deleted (so devices that haven't caught up don't bring it back) and deletes its files.</summary>
    private static void Tombstone(VaultItem r)
    {
        r.Deleted = true;
        WriteRecord(r);
        try { File.Delete(ItemPath(r.Id, r.ProtectedIn)); } catch { }
        try { File.Delete(ThumbPath(r.Id, r.ProtectedIn)); } catch { }
    }

    // ================================================================== reading

    private static byte[] EncodeJpeg(BitmapSource img)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The whole decrypted file, in memory (photos).</summary>
    public static byte[] ReadBytes(string id, string protectedIn = null)
    {
        var key = KeyFor(protectedIn);
        using var input = File.OpenRead(ItemPath(id, protectedIn));
        using var output = new MemoryStream();
        DecryptStream(input, output, key);
        return output.ToArray();
    }

    public static byte[] ReadThumb(string id, string protectedIn = null)
    {
        var path = ThumbPath(id, protectedIn);
        try { return File.Exists(path) ? DecryptBytes(File.ReadAllBytes(path), KeyFor(protectedIn)) : null; }
        catch { return null; }
    }

    /// <summary>Decrypts a video to a temporary file so it can play. The file is deleted when you move on, lock or close Shutterbox.</summary>
    public static async Task<string> DecryptForPlaybackAsync(Photo p)
    {
        var key = KeyFor(p.ProtectedIn).ToArray();
        Directory.CreateDirectory(TempDir);
        var path = IOPath.Combine(TempDir, Guid.NewGuid().ToString("N") + IOPath.GetExtension(p.FileName));
        try
        {
            await Task.Run(() =>
            {
                using var input = File.OpenRead(ItemPath(p.VaultId, p.ProtectedIn));
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.None);
                DecryptStream(input, output, key);
            });
            try { File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary); } catch { }
            return path;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Decrypts items under their own names to share for a moment (copy, open with). Deleted when it locks, and when Shutterbox closes or starts.</summary>
    public static async Task<List<string>> DecryptToShareAsync(IReadOnlyList<Photo> photos)
    {
        var dir = Directory.CreateDirectory(IOPath.Combine(TempDir, "share-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var outp = new List<string>();
        foreach (var p in photos.Where(x => x.IsLocked))
        {
            var key = KeyFor(p.ProtectedIn).ToArray();
            var path = IOPath.Combine(dir, p.FileName);
            if (File.Exists(path)) path = IOPath.Combine(dir, p.VaultId + "-" + p.FileName);
            await Task.Run(() =>
            {
                using var input = File.OpenRead(ItemPath(p.VaultId, p.ProtectedIn));
                using var output = File.Create(path);
                DecryptStream(input, output, key);
            });
            CryptographicOperations.ZeroMemory(key);
            outp.Add(path);
        }
        return outp;
    }

    public static void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var f in Directory.EnumerateFiles(TempDir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
            foreach (var d in Directory.EnumerateDirectories(TempDir))
                try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>Decrypts items back to ordinary files in a folder and takes them out of the Locked album.</summary>
    public static async Task<(int restored, List<string> errors)> RestoreAsync(IReadOnlyList<Photo> photos, string folder, IProgress<(int, int)> progress = null)
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
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
                    var key = KeyFor(p.ProtectedIn).ToArray();
                    var name = p.FileName;
                    var target = IOPath.Combine(folder, name);
                    for (int i = 2; File.Exists(target); i++)
                        target = IOPath.Combine(folder, $"{IOPath.GetFileNameWithoutExtension(name)} ({i}){IOPath.GetExtension(name)}");
                    await Task.Run(() =>
                    {
                        using var input = File.OpenRead(ItemPath(p.VaultId, p.ProtectedIn));
                        using var output = new FileStream(target, FileMode.CreateNew);
                        DecryptStream(input, output, key);
                    });
                    CryptographicOperations.ZeroMemory(key);
                    try { File.SetLastWriteTime(target, p.Taken); File.SetCreationTime(target, p.Taken); } catch { }
                    Thumbnails.Forget(p);
                    if (RecordOf(p) is { } r) Tombstone(r);
                    restored++;
                }
                catch (Exception ex) { errors.Add($"{p.FileName}: {ex.Message}"); }
            }
            SaveIndex();
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Raise();
            MaybeLock();
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
            if (RecordOf(p) is { } r) Tombstone(r);
        }
        SaveIndex();
        Raise();
    }

    public static void Rotate(Photo p, int degrees)
    {
        var r = RecordOf(p);
        if (r == null || _key == null) return;
        r.Rotation = ((r.Rotation + degrees) % 360 + 360) % 360;
        p.Rotation = r.Rotation;
        WriteRecord(r);
        SaveIndex();
        Raise();
    }

    /// <summary>Decrypts every file to make sure it's all there and undamaged (after copying or syncing the folder).</summary>
    public static async Task<(int ok, List<string> problems)> CheckAsync(IProgress<(int, int)> progress = null)
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        var all = Live.Concat(_albums.Values.Where(a => a.IsOpen).SelectMany(a => a.Records.Values.Where(r => !r.Deleted))).ToList();
        var problems = new List<string>();
        int ok = 0, done = 0;
        Interlocked.Increment(ref _busy);
        try
        {
            foreach (var r in all)
            {
                progress?.Report((done++, all.Count));
                try
                {
                    var key = KeyFor(r.ProtectedIn);
                    if (!File.Exists(ItemPath(r.Id, r.ProtectedIn))) { problems.Add($"{r.Name}: its file is missing (still syncing?)"); continue; }
                    await Task.Run(() =>
                    {
                        using var input = File.OpenRead(ItemPath(r.Id, r.ProtectedIn));
                        using var sink = new HashSink();
                        DecryptStream(input, sink, key);
                        if (r.HasThumb) DecryptBytes(File.ReadAllBytes(ThumbPath(r.Id, r.ProtectedIn)), key);
                    });
                    ok++;
                }
                catch (Exception ex) { problems.Add($"{r.Name}: {(ex is CryptographicException ? "it's damaged or was changed" : ex.Message)}"); }
            }
            progress?.Report((done, all.Count));
        }
        finally { Interlocked.Decrement(ref _busy); Raise(); }
        return (ok, problems);
    }

    // ================================================================== wrong guesses

    private static string AttemptsFile => IOPath.Combine(Folder, "attempts", SettingsService.Current.DeviceId + ".json");

    private sealed class AttemptLog
    {
        public string Device { get; set; } = "";
        public DateTime? LastUnlock { get; set; }
        public List<AttemptEntry> Failed { get; set; } = new();
    }

    private sealed class AttemptEntry
    {
        public DateTime Time { get; set; }
        public string What { get; set; } = "PIN";
    }

    private static AttemptLog ReadLog(string path)
    {
        try { return JsonSerializer.Deserialize<AttemptLog>(File.ReadAllText(path)) ?? new(); } catch { return new(); }
    }

    private static void WriteLog(AttemptLog log)
    {
        try
        {
            log.Device = Environment.MachineName;
            Directory.CreateDirectory(IOPath.GetDirectoryName(AttemptsFile));
            File.WriteAllText(AttemptsFile + ".tmp", JsonSerializer.Serialize(log));
            File.Move(AttemptsFile + ".tmp", AttemptsFile, true);
        }
        catch { }
    }

    /// <summary>Logs a wrong PIN, password or recovery key (on this PC).</summary>
    public static void RecordFailure(string what)
    {
        if (!IsSetUp) return;
        var log = ReadLog(AttemptsFile);
        log.Failed ??= new();
        log.Failed.Add(new AttemptEntry { Time = DateTime.UtcNow, What = what });
        if (log.Failed.Count > 50) log.Failed.RemoveRange(0, log.Failed.Count - 50);
        WriteLog(log);
    }

    private static void NoteUnlock()
    {
        var log = ReadLog(AttemptsFile);
        log.LastUnlock = DateTime.UtcNow;
        WriteLog(log);
    }

    /// <summary>Every logged wrong guess, from all devices, newest first.</summary>
    public static List<FailedAttempt> FailedAttempts
    {
        get
        {
            var dir = IOPath.Combine(Folder, "attempts");
            if (!Directory.Exists(dir)) return new();
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(ReadLog)
                .SelectMany(l => (l.Failed ?? new()).Select(f => new FailedAttempt(f.Time.ToUniversalTime().ToLocalTime(), f.What, string.IsNullOrEmpty(l.Device) ? "Another device" : l.Device)))
                .OrderByDescending(a => a.Time).ToList();
        }
    }

    private static List<FailedAttempt> FailedSinceLastUnlock()
    {
        var dir = IOPath.Combine(Folder, "attempts");
        DateTime? last = null;
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                if (ReadLog(f).LastUnlock is DateTime t && (last == null || t.ToUniversalTime() > last)) last = t.ToUniversalTime();
        return FailedAttempts.Where(a => last == null || a.Time.ToUniversalTime() > last).ToList();
    }

    // ================================================================== photographing wrong guesses

    private static string IntruderInfoFile => IOPath.Combine(Folder, "intruder.json");
    private static string IntruderKeyFile => IOPath.Combine(Folder, "intruder-key.sbox");
    public static bool PhotographsWrongGuesses => File.Exists(IntruderInfoFile);

    private sealed class RsaKeyJson
    {
        public string N { get; set; }
        public string E { get; set; }
        public string D { get; set; }
        public string P { get; set; }
        public string Q { get; set; }
        public string DP { get; set; }
        public string DQ { get; set; }
        public string InverseQ { get; set; }
    }

    private sealed class IntruderInfo { public RsaKeyJson PublicKey { get; set; } }

    /// <summary>Turns "Photograph wrong guesses" on (a 2048-bit RSA key pair: the private half is encrypted with the album key).</summary>
    public static void EnableIntruderPhotos()
    {
        var key = _key ?? throw new InvalidOperationException("Unlock first.");
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        string B(byte[] b) => Convert.ToBase64String(b);
        var priv = new RsaKeyJson { N = B(p.Modulus), E = B(p.Exponent), D = B(p.D), P = B(p.P), Q = B(p.Q), DP = B(p.DP), DQ = B(p.DQ), InverseQ = B(p.InverseQ) };
        File.WriteAllBytes(IntruderKeyFile, EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(priv), key));
        File.WriteAllText(IntruderInfoFile, JsonSerializer.Serialize(new IntruderInfo { PublicKey = new RsaKeyJson { N = priv.N, E = priv.E } },
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        Raise();
    }

    /// <summary>Turns it off and deletes the photos taken.</summary>
    public static void DisableIntruderPhotos()
    {
        if (_key == null) throw new InvalidOperationException("Unlock first.");
        try { File.Delete(IntruderInfoFile); } catch { }
        try { File.Delete(IntruderKeyFile); } catch { }
        try { Directory.Delete(IOPath.Combine(Folder, "intruders"), true); } catch { }
        Raise();
    }

    /// <summary>Saves a photo of a wrong guess, encrypted so only someone who can unlock the album can see it.</summary>
    public static void SaveIntruderPhoto(byte[] jpeg)
    {
        if (!PhotographsWrongGuesses) return;
        var pub = JsonSerializer.Deserialize<IntruderInfo>(File.ReadAllText(IntruderInfoFile))?.PublicKey;
        if (pub == null) return;
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Unpad(Convert.FromBase64String(pub.N)), Exponent = Unpad(Convert.FromBase64String(pub.E)) });
        var fileKey = RandomNumberGenerator.GetBytes(32);
        var wrapped = rsa.Encrypt(fileKey, RSAEncryptionPadding.OaepSHA256);
        var body = EncryptBytes(jpeg, fileKey);
        var u = DateTime.UtcNow;
        var name = $"{u:yyyyMMdd'T'HHmmss'Z'}-{RandomNumberGenerator.GetInt32(65536):x4}.sbox";
        var dir = Directory.CreateDirectory(IOPath.Combine(Folder, "intruders")).FullName;
        using var f = File.Create(IOPath.Combine(dir, name));
        f.Write(IntruderMagic);
        f.WriteByte((byte)(wrapped.Length >> 8));
        f.WriteByte((byte)wrapped.Length);
        f.Write(wrapped);
        f.Write(body);
        CryptographicOperations.ZeroMemory(fileKey);
    }

    public static List<IntruderPhoto> IntruderPhotos
    {
        get
        {
            var dir = IOPath.Combine(Folder, "intruders");
            if (!Directory.Exists(dir)) return new();
            return Directory.EnumerateFiles(dir, "*.sbox").Select(f =>
            {
                var n = IOPath.GetFileName(f);
                DateTime? t = n.Length >= 16 && DateTime.TryParseExact(n[..16], "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime() : null;
                return new IntruderPhoto(f, t);
            }).OrderByDescending(p => p.Time ?? DateTime.MinValue).ToList();
        }
    }

    public static byte[] ReadIntruderPhoto(IntruderPhoto p)
    {
        var key = _key ?? throw new InvalidOperationException("Unlock first.");
        var k = JsonSerializer.Deserialize<RsaKeyJson>(DecryptBytes(File.ReadAllBytes(IntruderKeyFile), key));
        var n = Unpad(Convert.FromBase64String(k.N));
        int half = (n.Length + 1) / 2;
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = n, Exponent = Unpad(Convert.FromBase64String(k.E)), D = Pad(Convert.FromBase64String(k.D), n.Length),
            P = Pad(Convert.FromBase64String(k.P), half), Q = Pad(Convert.FromBase64String(k.Q), half), DP = Pad(Convert.FromBase64String(k.DP), half),
            DQ = Pad(Convert.FromBase64String(k.DQ), half), InverseQ = Pad(Convert.FromBase64String(k.InverseQ), half),
        });
        var blob = File.ReadAllBytes(p.Path);
        if (blob.Length < 7 || !blob.AsSpan(0, 5).SequenceEqual(IntruderMagic)) throw new InvalidDataException("Not a photo of a wrong guess.");
        int len = (blob[5] << 8) | blob[6];
        var fileKey = rsa.Decrypt(blob.AsSpan(7, len).ToArray(), RSAEncryptionPadding.OaepSHA256);
        return DecryptBytes(blob.AsSpan(7 + len).ToArray(), fileKey);
    }

    public static void DeleteIntruderPhoto(IntruderPhoto p) { try { File.Delete(p.Path); } catch { } Raise(); }

    private static byte[] Unpad(byte[] b) { int i = 0; while (i < b.Length - 1 && b[i] == 0) i++; return b[i..]; }
    private static byte[] Pad(byte[] b, int length)
    {
        b = Unpad(b);
        if (b.Length >= length) return b;
        var outp = new byte[length];
        Buffer.BlockCopy(b, 0, outp, length - b.Length, b.Length);
        return outp;
    }

    // ================================================================== writing

    private static void WriteRecord(VaultItem r, bool touch = true)
    {
        if (touch) r.Modified = DateTime.UtcNow;
        var key = KeyFor(r.ProtectedIn);
        var path = RecordPath(r.Id, r.ProtectedIn);
        Directory.CreateDirectory(IOPath.GetDirectoryName(path));
        File.WriteAllBytes(path + ".tmp", EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(r, Compact), key));
        File.Move(path + ".tmp", path, true);
    }

    private static void WriteAlbum(VaultAlbum a, bool touch = true)
    {
        if (_key == null) return;
        if (touch) a.Modified = DateTime.UtcNow;
        var path = IOPath.Combine(Folder, "albums", a.Id + ".sbox");
        Directory.CreateDirectory(IOPath.GetDirectoryName(path));
        var rec = new AlbumRecord { Id = a.Id, Name = a.Name, Created = a.Created, Modified = a.Modified, Deleted = a.Deleted, CoverId = a.CoverId, Protected = a.Protected };
        File.WriteAllBytes(path + ".tmp", EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(rec), _key));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>The index for Shutterbox 0.2.0 and 0.3.0: the main album's items and its unprotected albums.</summary>
    private static void SaveIndex()
    {
        if (_key == null) return;
        var live = Live.ToList();
        var index = new VaultIndex
        {
            Items = live.Select(IndexItem.From).ToList(),
            Albums = _albums.Values.Where(a => !a.Deleted && !a.IsProtected)
                .Select(a => new IndexAlbum { Id = a.Id, Name = a.Name, Created = a.Created, ItemIds = live.Where(r => r.AlbumIds.Contains(a.Id)).Select(r => r.Id).ToList() }).ToList(),
        };
        File.WriteAllBytes(IndexFile + ".tmp", EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(index), _key));
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
    private static string NewRecoveryKeyText()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = Enumerable.Range(0, 25).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
        return string.Join("-", Enumerable.Range(0, 5).Select(i => new string(chars, i * 5, 5)));
    }

    private static string NormalizeRecovery(string key) => new((key ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}
