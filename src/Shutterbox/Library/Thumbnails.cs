using System.Security.Cryptography;
using System.Text;

namespace Shutterbox.Library;

/// <summary>
/// Square-ish thumbnails for the timeline. Each one is made once, saved as a small JPEG in
/// %LOCALAPPDATA%\Shutterbox\thumbs and kept in memory while it's likely to be on screen again.
/// Requests are served newest-first, so scrolling quickly doesn't queue up work for rows already gone.
/// </summary>
public static class Thumbnails
{
    /// <summary>Shortest side of a cached thumbnail, in pixels.</summary>
    public const int CachePixels = 320;

    public static string Folder { get; set; }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, LinkedListNode<(string key, BitmapSource img)>> Memory = new();
    private static readonly LinkedList<(string key, BitmapSource img)> Lru = new();
    private const int MemoryCapacity = 420;

    private static readonly List<(string key, Photo photo, Action<BitmapSource> done)> Queue = new();
    private static readonly Dictionary<string, List<Action<BitmapSource>>> Waiting = new();
    private static readonly HashSet<string> Failed = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static bool _started;

    public static string KeyOf(Photo p) => $"{p.Id}:{p.Rotation}:{p.ModifiedTicks}";

    /// <summary>Returns the thumbnail if it's in memory; otherwise starts making it and calls <paramref name="done"/> on the UI thread.</summary>
    public static BitmapSource Get(Photo p, Action<BitmapSource> done)
    {
        var key = KeyOf(p);
        lock (Lock)
        {
            if (Memory.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                return node.Value.img;
            }
            if (Failed.Contains(key)) return null;
            if (Waiting.TryGetValue(key, out var list))
            {
                if (done != null) list.Add(done);
                // move to the front of the queue: it's wanted again
                int i = Queue.FindIndex(q => q.key == key);
                if (i >= 0) { var item = Queue[i]; Queue.RemoveAt(i); Queue.Add(item); }
                return null;
            }
            Waiting[key] = done != null ? new List<Action<BitmapSource>> { done } : new List<Action<BitmapSource>>();
            Queue.Add((key, p, null));
            // Drop the oldest requests when scrolling fast
            while (Queue.Count > 300)
            {
                var old = Queue[0];
                Queue.RemoveAt(0);
                Waiting.Remove(old.key);
            }
            EnsureWorkers();
        }
        Signal.Release();
        return null;
    }

    public static bool HasFailed(Photo p)
    {
        lock (Lock) return Failed.Contains(KeyOf(p));
    }

    public static int Pending
    {
        get { lock (Lock) return Queue.Count; }
    }

    /// <summary>Forgets cached thumbnails for these photos (after a rotation, for example).</summary>
    public static void Forget(Photo p)
    {
        lock (Lock)
        {
            foreach (var k in Memory.Keys.Where(k => k.StartsWith(p.Id + ":")).ToList())
            {
                Lru.Remove(Memory[k]);
                Memory.Remove(k);
            }
            Failed.RemoveWhere(k => k.StartsWith(p.Id + ":"));
        }
    }

    public static void ClearMemory()
    {
        lock (Lock) { Memory.Clear(); Lru.Clear(); Failed.Clear(); }
    }

    private static void EnsureWorkers()
    {
        if (_started) return;
        _started = true;
        int n = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "Thumbnail worker", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    private static void Worker()
    {
        while (true)
        {
            Signal.Wait();
            (string key, Photo photo, Action<BitmapSource> done) job;
            lock (Lock)
            {
                if (Queue.Count == 0) continue;
                job = Queue[^1];
                Queue.RemoveAt(Queue.Count - 1);
            }
            BitmapSource img = null;
            try { img = Make(job.photo, job.key); }
            catch (Exception ex) { LastError ??= $"{job.photo.FileName}: {ex.GetType().Name}: {ex.Message}"; }
            List<Action<BitmapSource>> callbacks;
            lock (Lock)
            {
                if (img != null)
                {
                    var node = Lru.AddFirst((job.key, img));
                    Memory[job.key] = node;
                    while (Lru.Count > MemoryCapacity)
                    {
                        var last = Lru.Last;
                        Lru.RemoveLast();
                        Memory.Remove(last.Value.key);
                    }
                }
                else Failed.Add(job.key);
                Waiting.Remove(job.key, out callbacks);
            }
            if (callbacks != null && callbacks.Count > 0)
            {
                var disp = Application.Current?.Dispatcher;
                disp?.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    foreach (var cb in callbacks) { try { cb(img); } catch { } }
                }));
            }
        }
    }

    private static string CachePath(Photo p, string key)
    {
        var raw = $"{p.Path.ToLowerInvariant()}|{p.Size}|{p.ModifiedTicks}|{p.Rotation}|{CachePixels}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return IOPath.Combine(Folder ?? IOPath.GetTempPath(), hash[..2], hash[2..22] + ".jpg");
    }

    private static BitmapSource Make(Photo p, string key)
    {
        var cache = CachePath(p, key);
        if (File.Exists(cache))
        {
            try { return LoadFile(cache, 0, 0); } catch { try { File.Delete(cache); } catch { } }
        }
        var bmp = Load(p, CachePixels);
        if (bmp == null) return null;
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(cache));
            var enc = new JpegBitmapEncoder { QualityLevel = 84 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var tmp = cache + ".tmp";
            using (var fs = File.Create(tmp)) enc.Save(fs);
            File.Move(tmp, cache, true);
        }
        catch (Exception ex) { LastError ??= $"saving thumbnail: {ex.GetType().Name}: {ex.Message}"; }
        return bmp;
    }

    /// <summary>
    /// Decodes a photo, upright (EXIF orientation and your rotation applied), with its shortest side about
    /// <paramref name="shortSide"/> pixels (0 = full size). Safe to call from any thread; the result is frozen.
    /// </summary>
    public static BitmapSource Load(Photo p, int shortSide, int maxLongSide = 0)
    {
        int decodeW = 0, decodeH = 0;
        if (shortSide > 0 && p.Width > 0 && p.Height > 0)
        {
            int sw = p.Width, sh = p.Height;
            if (Math.Min(sw, sh) > shortSide)
            {
                // Very wide panoramas: limit the long side instead
                double scale = (double)shortSide / Math.Min(sw, sh);
                if (maxLongSide > 0 && Math.Max(sw, sh) * scale > maxLongSide) scale = (double)maxLongSide / Math.Max(sw, sh);
                else if (Math.Max(sw, sh) * scale > shortSide * 4) scale = shortSide * 4.0 / Math.Max(sw, sh);
                if (sw >= sh) decodeW = Math.Max(1, (int)(sw * scale));
                else decodeH = Math.Max(1, (int)(sh * scale));
            }
        }
        else if (shortSide > 0)
        {
            decodeW = shortSide * 2;
        }
        BitmapSource src = LoadFile(p.Path, decodeW, decodeH);
        return Upright(src, p);
    }

    public static BitmapSource Upright(BitmapSource src, Photo p)
    {
        var (flip, angle) = p.DisplayTransform();
        if (!flip && angle == 0) return src;
        var tg = new TransformGroup();
        if (flip) tg.Children.Add(new ScaleTransform(-1, 1));
        if (angle != 0) tg.Children.Add(new RotateTransform(angle));
        var t = new TransformedBitmap(src, tg);
        t.Freeze();
        return t;
    }

    /// <summary>The last decoding error, for diagnostics (the self-test prints it).</summary>
    public static string LastError { get; private set; }

    private static BitmapSource LoadFile(string path, int decodeW, int decodeH)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes, false);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeW > 0) bi.DecodePixelWidth = decodeW;
            else if (decodeH > 0) bi.DecodePixelHeight = decodeH;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception first)
        {
            // Some codecs don't support scaled decoding through BitmapImage: decode, then scale.
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                BitmapSource frame = dec.Frames[0];
                double scale = decodeW > 0 ? (double)decodeW / frame.PixelWidth : decodeH > 0 ? (double)decodeH / frame.PixelHeight : 1;
                if (scale > 0 && scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                frame.Freeze();
                return frame;
            }
            catch (Exception second)
            {
                LastError = $"{IOPath.GetFileName(path)}: {first.GetType().Name}: {first.Message} / {second.GetType().Name}: {second.Message}";
                throw;
            }
        }
    }

    /// <summary>Runs work on a background STA thread (WPF imaging is happiest there).</summary>
    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Picture loader" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <summary>Loads a bigger picture off the UI thread (info panel preview, viewer).</summary>
    public static Task<BitmapSource> LoadAsync(Photo p, int shortSide, int maxLongSide, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<BitmapSource>(ct);
        return RunSta(() =>
        {
            if (ct.IsCancellationRequested) return null;
            try { return Load(p, shortSide, maxLongSide); }
            catch (Exception ex) { LastError ??= ex.Message; return null; }
        });
    }

    public static long CacheSize()
    {
        try
        {
            if (Folder == null || !Directory.Exists(Folder)) return 0;
            return new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    public static void ClearDiskCache()
    {
        ClearMemory();
        try { if (Folder != null && Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
    }
}
