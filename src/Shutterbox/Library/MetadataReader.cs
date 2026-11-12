using System.Globalization;
using System.Text.RegularExpressions;

namespace Shutterbox.Library;

/// <summary>
/// Reads a picture's size, date taken, camera, exposure, GPS and orientation with the Windows Imaging Component
/// (the same codecs File Explorer uses, so HEIC/WebP/RAW work when their Windows extensions are installed).
/// </summary>
public static class MetadataReader
{
    public static readonly string[] Extensions =
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".webp",
        ".heic", ".heif", ".avif", ".jxr", ".wdp", ".hdp", ".ico",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".srw", ".pef",
    };

    private static readonly HashSet<string> ExtSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsPicture(string path) => ExtSet.Contains(IOPath.GetExtension(path));

    /// <summary>Builds a <see cref="Photo"/> for a file. Never throws: unreadable files get the file date and Unreadable = true.</summary>
    public static Photo Read(string path, FileInfo info = null)
    {
        info ??= new FileInfo(path);
        var p = new Photo
        {
            Path = info.FullName,
            Size = info.Length,
            ModifiedTicks = info.LastWriteTimeUtc.Ticks,
        };
        DateTime? taken = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = dec.Frames[0];
            p.Width = frame.PixelWidth;
            p.Height = frame.PixelHeight;
            BitmapMetadata md = null;
            try { md = frame.Metadata as BitmapMetadata; } catch { }
            if (md != null) taken = ReadMetadata(md, p);
        }
        catch
        {
            p.Unreadable = true;
        }

        if (taken != null)
        {
            p.Taken = taken.Value;
            p.TakenSource = "exif";
        }
        else if (FromFileName(p.FileName) is DateTime fromName)
        {
            p.Taken = fromName;
            p.TakenSource = "name";
        }
        else
        {
            var c = info.CreationTime;
            var w = info.LastWriteTime;
            p.Taken = c < w && c.Year > 1980 ? c : w;
            p.TakenSource = "file";
        }
        return p;
    }

    private static DateTime? ReadMetadata(BitmapMetadata md, Photo p)
    {
        DateTime? taken = ParseDate(Q(md,
            "/app1/ifd/exif/{ushort=36867}", "/ifd/exif/{ushort=36867}",
            "/app1/ifd/exif/{ushort=36868}", "/ifd/exif/{ushort=36868}",
            "/app1/ifd/{ushort=306}", "/ifd/{ushort=306}",
            "/xmp/exif:DateTimeOriginal", "/xmp/xmp:CreateDate"));
        if (taken == null)
        {
            try
            {
                var s = md.DateTaken;
                if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) && Plausible(d))
                    taken = d;
            }
            catch { }
        }

        try { p.Make = Clean(md.CameraManufacturer); } catch { }
        try { p.Model = Clean(md.CameraModel); } catch { }
        p.Make ??= Clean(Q(md, "/app1/ifd/{ushort=271}", "/ifd/{ushort=271}") as string);
        p.Model ??= Clean(Q(md, "/app1/ifd/{ushort=272}", "/ifd/{ushort=272}") as string);
        p.Lens = Clean(Q(md, "/app1/ifd/exif/{ushort=42036}", "/ifd/exif/{ushort=42036}", "System.Photo.LensModel") as string);

        p.ExposureTime = Positive(ToDouble(Q(md, "/app1/ifd/exif/{ushort=33434}", "/ifd/exif/{ushort=33434}", "System.Photo.ExposureTime")));
        p.FNumber = Positive(ToDouble(Q(md, "/app1/ifd/exif/{ushort=33437}", "/ifd/exif/{ushort=33437}", "System.Photo.FNumber")));
        p.FocalLength = Positive(ToDouble(Q(md, "/app1/ifd/exif/{ushort=37386}", "/ifd/exif/{ushort=37386}", "System.Photo.FocalLength")));
        var iso = ToDouble(Q(md, "/app1/ifd/exif/{ushort=34855}", "/ifd/exif/{ushort=34855}", "System.Photo.ISOSpeed"));
        if (iso is double i && i > 0 && i < 1_000_000) p.Iso = (int)i;

        var orient = ToDouble(Q(md, "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "System.Photo.Orientation"));
        if (orient is double o && o >= 1 && o <= 8) p.Orientation = (int)o;

        var lat = ToDegrees(Q(md, "/app1/ifd/gps/{ushort=2}", "/ifd/gps/{ushort=2}", "System.GPS.Latitude"));
        var lon = ToDegrees(Q(md, "/app1/ifd/gps/{ushort=4}", "/ifd/gps/{ushort=4}", "System.GPS.Longitude"));
        if (lat is double la && lon is double lo && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180 && !(la == 0 && lo == 0))
        {
            var latRef = (Q(md, "/app1/ifd/gps/{ushort=1}", "/ifd/gps/{ushort=1}", "System.GPS.LatitudeRef") as string ?? "N").Trim();
            var lonRef = (Q(md, "/app1/ifd/gps/{ushort=3}", "/ifd/gps/{ushort=3}", "System.GPS.LongitudeRef") as string ?? "E").Trim();
            p.Latitude = latRef.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(la) : la;
            p.Longitude = lonRef.StartsWith("W", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(lo) : lo;
        }
        return taken;
    }

    /// <summary>First query that returns a value. Metadata blocks differ per format, so most queries simply fail.</summary>
    private static object Q(BitmapMetadata md, params string[] queries)
    {
        foreach (var q in queries)
        {
            try
            {
                var v = md.GetQuery(q);
                if (v != null) return v;
            }
            catch { }
        }
        return null;
    }

    private static string Clean(string s)
    {
        if (s == null) return null;
        s = s.Replace("\0", "").Trim();
        return s.Length == 0 ? null : s;
    }

    private static double? Positive(double? v) => v is double d && d > 0 && !double.IsInfinity(d) ? d : null;

    private static bool Plausible(DateTime d) => d.Year >= 1900 && d <= DateTime.Now.AddDays(2);

    public static DateTime? ParseDate(object v)
    {
        if (v is DateTime dt) return Plausible(dt) ? dt : null;
        if (v is not string s) return null;
        s = s.Replace("\0", "").Trim();
        if (s.Length == 0) return null;
        string[] formats =
        {
            "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.fff", "yyyy:MM:dd HH:mm", "yyyy:MM:dd",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:sszzz",
            "yyyy-MM-ddTHH:mm:ss.fffzzz", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd",
        };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && Plausible(d)) return d;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && Plausible(dto.DateTime)) return dto.DateTime;
        return null;
    }

    private static readonly Regex NameDate = new(
        @"(?<!\d)(?<y>19[7-9]\d|20\d\d)[-_.]?(?<mo>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?:[ _T\-]?(?:at[ _])?(?<h>[01]\d|2[0-3])[-_.:]?(?<mi>[0-5]\d)[-_.:]?(?<s>[0-5]\d))?(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Dates in names like IMG_20231014_183011.jpg, Screenshot 2023-10-14 183011.png or 2023-10-14 18.30.11.jpg.</summary>
    public static DateTime? FromFileName(string name)
    {
        var m = NameDate.Match(IOPath.GetFileNameWithoutExtension(name ?? ""));
        if (!m.Success) return null;
        try
        {
            int y = int.Parse(m.Groups["y"].Value), mo = int.Parse(m.Groups["mo"].Value), d = int.Parse(m.Groups["d"].Value);
            if (d > DateTime.DaysInMonth(y, mo)) return null;
            int h = 0, mi = 0, s = 0;
            if (m.Groups["h"].Success) { h = int.Parse(m.Groups["h"].Value); mi = int.Parse(m.Groups["mi"].Value); s = int.Parse(m.Groups["s"].Value); }
            var dt = new DateTime(y, mo, d, h, mi, s);
            return Plausible(dt) ? dt : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Converts what WIC hands back to a number. Unsigned rationals come back as a ulong with the numerator in the
    /// low 32 bits and the denominator in the high 32 bits; signed rationals as a long the same way.
    /// </summary>
    public static double? ToDouble(object v)
    {
        switch (v)
        {
            case null: return null;
            case double d: return d;
            case float f: return f;
            case ulong u:
            {
                uint num = (uint)(u & 0xFFFFFFFF), den = (uint)(u >> 32);
                return den == 0 ? null : (double)num / den;
            }
            case long l:
            {
                int num = (int)(l & 0xFFFFFFFF), den = (int)(l >> 32);
                return den == 0 ? null : (double)num / den;
            }
            case uint ui: return ui;
            case int i: return i;
            case ushort us: return us;
            case short sh: return sh;
            case byte b: return b;
            case decimal m: return (double)m;
            case string s: return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
            case Array a when a.Length > 0: return ToDouble(a.GetValue(0));
            default: return null;
        }
    }

    /// <summary>GPS degrees from degrees/minutes/seconds (three rationals) or a single number.</summary>
    public static double? ToDegrees(object v)
    {
        if (v is Array a && a.Length >= 1)
        {
            double deg = ToDouble(a.GetValue(0)) ?? double.NaN;
            double min = a.Length > 1 ? ToDouble(a.GetValue(1)) ?? 0 : 0;
            double sec = a.Length > 2 ? ToDouble(a.GetValue(2)) ?? 0 : 0;
            if (double.IsNaN(deg)) return null;
            return deg + min / 60 + sec / 3600;
        }
        return ToDouble(v);
    }
}
