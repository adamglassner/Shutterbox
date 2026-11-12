using Shutterbox.Services;

namespace Shutterbox.Library;

public sealed class ImportItem
{
    public string Source { get; init; }
    public long Size { get; init; }
    public DateTime Taken { get; init; }
    /// <summary>Already in the library (same size and the same name or the same date taken).</summary>
    public bool Duplicate { get; set; }
}

public sealed class ImportPlan
{
    public List<ImportItem> Items { get; } = new();
    public int Count => Items.Count;
    public int Duplicates => Items.Count(i => i.Duplicate);
    public long Bytes => Items.Sum(i => i.Size);
    public DateTime? From => Items.Count == 0 ? null : Items.Min(i => i.Taken);
    public DateTime? To => Items.Count == 0 ? null : Items.Max(i => i.Taken);
}

public sealed class ImportResult
{
    public int Copied, Skipped, Failed;
    public long Bytes;
    public List<string> NewFiles = new();
    public List<string> Errors = new();
}

/// <summary>
/// Copies pictures from a camera card, phone or any folder into the import folder, organized into
/// date folders, skipping ones you already have. Originals are only deleted when you ask, and only
/// after the copy has been checked.
/// </summary>
public static class Importer
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Removable drives (SD cards, USB sticks, some cameras) that have a DCIM folder.</summary>
    public static List<(string label, string path)> FindCameraCards()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType != DriveType.Removable) continue;
                    var dcim = IOPath.Combine(d.RootDirectory.FullName, "DCIM");
                    var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Removable drive" : d.VolumeLabel;
                    list.Add(($"{label} ({d.Name.TrimEnd('\\')})", Directory.Exists(dcim) ? dcim : d.RootDirectory.FullName));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Finds the pictures in the given files and folders and works out which are already in the library.</summary>
    public static async Task<ImportPlan> AnalyzeAsync(LibraryStore lib, IEnumerable<string> sources,
        IProgress<int> progress = null, CancellationToken ct = default)
    {
        // Snapshot of the library, taken on the UI thread: size -> (name, taken)
        var bySize = lib.Photos.GroupBy(p => p.Size)
            .ToDictionary(g => g.Key, g => g.Select(p => (name: p.FileName, taken: p.Taken, src: p.TakenSource)).ToList());
        var libPaths = new HashSet<string>(lib.Photos.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        var srcList = sources.ToList();

        return await Task.Run(() =>
        {
            var plan = new ImportPlan();
            var files = new List<string>();
            foreach (var s in srcList)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(s)) { if (MetadataReader.IsPicture(s)) files.Add(s); }
                else if (Directory.Exists(s))
                {
                    try { files.AddRange(Directory.EnumerateFiles(s, "*", Walk).Where(MetadataReader.IsPicture)); }
                    catch { }
                }
            }
            int n = 0;
            foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                FileInfo fi;
                try { fi = new FileInfo(f); if (fi.Length == 0) continue; } catch { continue; }
                var meta = MetadataReader.Read(f, fi);
                var item = new ImportItem { Source = fi.FullName, Size = fi.Length, Taken = meta.Taken };
                if (libPaths.Contains(fi.FullName)) item.Duplicate = true;
                else if (bySize.TryGetValue(fi.Length, out var same))
                    item.Duplicate = same.Any(x =>
                        string.Equals(x.name, fi.Name, StringComparison.OrdinalIgnoreCase) ||
                        (x.src == "exif" && meta.TakenSource == "exif" && Math.Abs((x.taken - meta.Taken).TotalSeconds) < 1));
                plan.Items.Add(item);
                if (++n % 20 == 0) progress?.Report(n);
            }
            progress?.Report(n);
            plan.Items.Sort((a, b) => a.Taken.CompareTo(b.Taken));
            return plan;
        }, ct);
    }

    public static string TargetFolder(string dest, ImportLayout layout, DateTime taken) => layout switch
    {
        ImportLayout.YearAndDay => IOPath.Combine(dest, taken.ToString("yyyy"), taken.ToString("yyyy-MM-dd")),
        ImportLayout.YearAndMonth => IOPath.Combine(dest, taken.ToString("yyyy"), taken.ToString("yyyy-MM")),
        _ => dest,
    };

    public static async Task<ImportResult> ImportAsync(ImportPlan plan, string dest, ImportLayout layout, bool skipDuplicates,
        bool deleteOriginals, IProgress<(int done, int total)> progress = null, CancellationToken ct = default)
    {
        var items = plan.Items.Where(i => !(skipDuplicates && i.Duplicate)).ToList();
        var result = new ImportResult { Skipped = plan.Count - items.Count };
        await Task.Run(() =>
        {
            int done = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var folder = TargetFolder(dest, layout, item.Taken);
                    Directory.CreateDirectory(folder);
                    var name = IOPath.GetFileName(item.Source);
                    var target = IOPath.Combine(folder, name);
                    if (string.Equals(IOPath.GetFullPath(target), IOPath.GetFullPath(item.Source), StringComparison.OrdinalIgnoreCase))
                    {
                        result.Skipped++;
                        continue;
                    }
                    if (File.Exists(target) && new FileInfo(target).Length == item.Size)
                    {
                        result.Skipped++; // the same file is already there
                    }
                    else
                    {
                        for (int i = 2; File.Exists(target); i++)
                            target = IOPath.Combine(folder, $"{IOPath.GetFileNameWithoutExtension(name)} ({i}){IOPath.GetExtension(name)}");
                        var tmp = target + ".importing";
                        File.Copy(item.Source, tmp, true);
                        if (new FileInfo(tmp).Length != item.Size) { File.Delete(tmp); throw new IOException("The copy is a different size from the original."); }
                        File.Move(tmp, target);
                        try
                        {
                            File.SetLastWriteTime(target, File.GetLastWriteTime(item.Source));
                            File.SetCreationTime(target, File.GetCreationTime(item.Source));
                        }
                        catch { }
                        result.Copied++;
                        result.Bytes += item.Size;
                        result.NewFiles.Add(target);
                    }
                    if (deleteOriginals)
                    {
                        try { File.Delete(item.Source); } catch (Exception ex) { result.Errors.Add($"Couldn't delete {item.Source}: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Errors.Add($"{IOPath.GetFileName(item.Source)}: {ex.Message}");
                }
                progress?.Report((++done, items.Count));
            }
        }, CancellationToken.None);
        return result;
    }
}
