using System.Collections.Concurrent;

namespace Shutterbox.Library;

public sealed class ScanProgress
{
    public int Found { get; init; }
    public int Done { get; init; }
    public int ToRead { get; init; }
    public string Phase { get; init; } = "";
}

public sealed class ScanResult
{
    public int Added, Updated, Removed, Total;
    public List<string> OfflineFolders = new();
}

/// <summary>
/// Finds pictures in the library folders, reads new or changed ones in the background and hands the results to
/// <see cref="LibraryStore.Apply"/> on the UI thread in batches, so the timeline fills in while a big scan runs.
/// Photos whose files disappeared are removed, except under folders that are missing altogether
/// (an unplugged drive), which are reported as offline instead.
/// </summary>
public static class Scanner
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    public static async Task<ScanResult> ScanAsync(LibraryStore lib, IReadOnlyList<string> folders,
        IProgress<ScanProgress> progress = null, CancellationToken ct = default)
    {
        var disp = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        // Snapshot of what's known, taken on the UI thread
        var known = lib.Photos.ToDictionary(p => p.Path, p => (p.Size, p.ModifiedTicks, p.Unreadable), StringComparer.OrdinalIgnoreCase);
        var roots = folders.Select(LibraryStore.NormalizeFolder).ToList();
        var libFolders = lib.Folders.ToList();
        var result = new ScanResult();

        await Task.Run(async () =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toRead = new List<FileInfo>();
            var online = new List<string>();
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) { result.OfflineFolders.Add(root); continue; }
                online.Add(root);
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*", Walk); }
                catch { continue; }
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!MetadataReader.IsPicture(path)) continue;
                    if (!seen.Add(path)) continue;
                    FileInfo fi;
                    try { fi = new FileInfo(path); if (!fi.Exists || fi.Length == 0) continue; }
                    catch { continue; }
                    if (known.TryGetValue(path, out var k) && k.Size == fi.Length && k.ModifiedTicks == fi.LastWriteTimeUtc.Ticks) continue;
                    toRead.Add(fi);
                    if (seen.Count % 500 == 0)
                        progress?.Report(new ScanProgress { Found = seen.Count, Phase = "Looking for photos" });
                }
            }
            result.Total = seen.Count;
            progress?.Report(new ScanProgress { Found = seen.Count, ToRead = toRead.Count, Phase = toRead.Count > 0 ? "Reading photos" : "Up to date" });

            // Read new and changed files in parallel; hand results over in batches
            var batch = new ConcurrentQueue<Photo>();
            int done = 0;
            var lastFlush = Environment.TickCount64;
            async Task Flush()
            {
                var list = new List<Photo>();
                while (batch.TryDequeue(out var p)) list.Add(p);
                if (list.Count == 0) return;
                foreach (var p in list)
                    if (known.ContainsKey(p.Path)) Interlocked.Increment(ref result.Updated); else Interlocked.Increment(ref result.Added);
                await disp.InvokeAsync(() => lib.Apply(list, null));
            }

            // Newest files first, so recent photos appear right away on a first scan
            toRead.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            using var gate = new SemaphoreSlim(1, 1);
            await Parallel.ForEachAsync(toRead, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 6), CancellationToken = ct },
                async (fi, token) =>
                {
                    batch.Enqueue(MetadataReader.Read(fi.FullName, fi));
                    int n = Interlocked.Increment(ref done);
                    if (n % 25 == 0) progress?.Report(new ScanProgress { Found = seen.Count, ToRead = toRead.Count, Done = n, Phase = "Reading photos" });
                    if (batch.Count >= 200 || Environment.TickCount64 - lastFlush > 700)
                    {
                        if (await gate.WaitAsync(0, token))
                        {
                            try { lastFlush = Environment.TickCount64; await Flush(); }
                            finally { gate.Release(); }
                        }
                    }
                });
            await gate.WaitAsync(ct);
            try { await Flush(); } finally { gate.Release(); }

            // Forget photos whose files are gone (only under folders that are actually there)
            var removed = known.Keys.Where(path =>
                    !seen.Contains(path) &&
                    online.Any(r => LibraryStore.IsUnder(path, r)) &&
                    !File.Exists(path))
                .ToList();
            // ...and photos that no longer belong to any library folder
            var removedSet = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
            removed.AddRange(known.Keys.Where(path => !removedSet.Contains(path)
                                                      && !roots.Any(r => LibraryStore.IsUnder(path, r))
                                                      && !libFolders.Any(f => LibraryStore.IsUnder(path, f))));
            result.Removed = removed.Count;
            if (removed.Count > 0) await disp.InvokeAsync(() => lib.Apply(Array.Empty<Photo>(), removed));
            progress?.Report(new ScanProgress { Found = seen.Count, ToRead = toRead.Count, Done = toRead.Count, Phase = "Up to date" });
        }, ct);
        return result;
    }
}
