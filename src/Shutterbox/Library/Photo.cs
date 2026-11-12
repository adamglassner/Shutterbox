using System.Globalization;
using System.Text.Json.Serialization;

namespace Shutterbox.Library;

/// <summary>One picture file on disk plus everything Shutterbox knows or keeps about it.</summary>
public sealed class Photo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";

    // ---- file ----
    public long Size { get; set; }
    /// <summary>Last write time of the file (UTC ticks) when it was last read. Used to notice changes.</summary>
    public long ModifiedTicks { get; set; }

    // ---- read from the picture ----
    /// <summary>When the picture was taken (local time, as the camera recorded it).</summary>
    public DateTime Taken { get; set; }
    /// <summary>Where <see cref="Taken"/> came from: "exif", "name" (file name) or "file" (file date).</summary>
    public string TakenSource { get; set; } = "file";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Make { get; set; }
    public string Model { get; set; }
    public string Lens { get; set; }
    public double? ExposureTime { get; set; }
    public double? FNumber { get; set; }
    public double? FocalLength { get; set; }
    public int? Iso { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>EXIF orientation (1-8). 1 = as stored.</summary>
    public int Orientation { get; set; } = 1;
    /// <summary>True when Windows couldn't decode the file (for example HEIC without the HEIF extension).</summary>
    public bool Unreadable { get; set; }

    // ---- yours ----
    /// <summary>Extra clockwise rotation chosen in Shutterbox (0, 90, 180, 270). The file itself is never changed.</summary>
    public int Rotation { get; set; }
    public bool Favorite { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Caption { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? Trashed { get; set; }

    // ---- helpers (not saved) ----
    [JsonIgnore] public string FileName => IOPath.GetFileName(Path);
    [JsonIgnore] public string FolderPath => IOPath.GetDirectoryName(Path) ?? "";
    [JsonIgnore] public string FolderName => IOPath.GetFileName(FolderPath);
    [JsonIgnore] public bool IsTrashed => Trashed != null;
    [JsonIgnore] public bool HasLocation => Latitude.HasValue && Longitude.HasValue;

    /// <summary>EXIF orientations 5-8 store the picture on its side.</summary>
    [JsonIgnore] public bool StoredSideways => Orientation >= 5 && Orientation <= 8;
    [JsonIgnore] public bool ShownSideways => StoredSideways ^ (Rotation % 180 != 0);
    [JsonIgnore] public int DisplayWidth => ShownSideways ? Height : Width;
    [JsonIgnore] public int DisplayHeight => ShownSideways ? Width : Height;

    [JsonIgnore] public string CameraText
    {
        get
        {
            var make = (Make ?? "").Trim();
            var model = (Model ?? "").Trim();
            if (model.Length == 0) return make;
            // "Canon" + "Canon EOS R6" -> "Canon EOS R6"
            if (make.Length > 0 && !model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) return make + " " + model;
            return model;
        }
    }

    [JsonIgnore] public string ExposureText
    {
        get
        {
            var parts = new List<string>();
            if (ExposureTime is double t && t > 0)
                parts.Add(t >= 1 ? t.ToString("0.#", CultureInfo.InvariantCulture) + " s" : "1/" + Math.Round(1 / t).ToString(CultureInfo.InvariantCulture));
            if (FNumber is double f && f > 0) parts.Add("f/" + f.ToString("0.#", CultureInfo.InvariantCulture));
            if (Iso is int iso && iso > 0) parts.Add("ISO " + iso);
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore] public string LocationText => HasLocation
        ? $"{Math.Abs(Latitude.Value):0.0000}° {(Latitude.Value >= 0 ? "N" : "S")}, {Math.Abs(Longitude.Value):0.0000}° {(Longitude.Value >= 0 ? "E" : "W")}"
        : "";

    public static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} bytes" : (v >= 100 ? $"{v:0} {units[u]}" : $"{v:0.#} {units[u]}");
    }

    /// <summary>The flip and clockwise rotation that turn the stored pixels into the picture as it should be shown.</summary>
    public (bool flip, int angle) DisplayTransform()
    {
        (bool flip, int angle) t = Orientation switch
        {
            2 => (true, 0),
            3 => (false, 180),
            4 => (true, 180),
            5 => (true, 270),
            6 => (false, 90),
            7 => (true, 90),
            8 => (false, 270),
            _ => (false, 0),
        };
        return (t.flip, (t.angle + Rotation) % 360);
    }
}

public sealed class Album
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> PhotoIds { get; set; } = new();
    public string CoverId { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>What's saved in library.json.</summary>
public sealed class LibraryData
{
    public int Version { get; set; } = 1;
    public List<string> Folders { get; set; } = new();
    public List<Photo> Photos { get; set; } = new();
    public List<Album> Albums { get; set; } = new();
}
