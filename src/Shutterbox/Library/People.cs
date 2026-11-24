using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shutterbox.Library;

public sealed class PersonInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; }
    public bool Hidden { get; set; }
    /// <summary>The face shown for this person (the best one unless you pick another).</summary>
    public string Cover { get; set; }
}

public sealed class FaceInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Photo { get; set; }
    /// <summary>Where the face is, as fractions of the upright picture: x, y, width, height.</summary>
    public float[] Box { get; set; }
    public float Score { get; set; }
    /// <summary>The face's 128 numbers, each times 127 as a signed byte, base64.</summary>
    public string E { get; set; }
    public string Person { get; set; }
    /// <summary>People you said this face isn't.</summary>
    public List<string> Not { get; set; }
    [JsonIgnore] public float[] Vector;
}

public sealed class AnalyzedPhoto
{
    public string Rev { get; set; }
    /// <summary>[class id, percent] for what's in the photo.</summary>
    public List<int[]> Labels { get; set; } = new();
    public List<string> Faces { get; set; } = new();
}

public sealed class PeopleData
{
    public int Version { get; set; } = 1;
    public Dictionary<string, AnalyzedPhoto> Photos { get; set; } = new();
    public Dictionary<string, FaceInfo> Faces { get; set; } = new();
    public List<PersonInfo> People { get; set; } = new();
}

/// <summary>
/// People (faces grouped by who they are) and what's in each photo, worked out on this PC by <see cref="Vision"/>.
/// Kept in %APPDATA%\Shutterbox\people.json. The background <see cref="Analyzer"/> adds to it; the UI reads it.
/// Grouping: a new face joins the person whose average face is closest if the cosine is at least
/// <see cref="Threshold"/>, otherwise it starts a new person. People show once they have a name or 2 faces.
/// </summary>
public sealed class PeopleStore
{
    public const double Threshold = 0.40;
    /// <summary>Faces smaller than this (in the analyzed picture, at most 1280 pixels) aren't kept: too small to tell apart.</summary>
    public const float MinFacePixels = 32;
    /// <summary>Things the model is less sure of than this aren't searchable (they'd mostly be wrong).</summary>
    public const int MinSearchPercent = 12;

    public static PeopleStore Current { get; set; }
    public string FilePath { get; }
    private PeopleData _d = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, double[]> _sums = new();
    private bool _dirty;

    public event Action Changed;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public PeopleStore(string file) => FilePath = file;

    public void Load()
    {
        lock (_lock)
        {
            try { if (File.Exists(FilePath)) _d = JsonSerializer.Deserialize<PeopleData>(File.ReadAllText(FilePath), Json) ?? new(); }
            catch { try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } _d = new(); }
            _d.Photos ??= new(); _d.Faces ??= new(); _d.People ??= new();
            foreach (var f in _d.Faces.Values) f.Vector = Decode(f.E);
            RebuildSums();
        }
    }

    public void Save()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_d, Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch { lock (_lock) _dirty = true; }
    }

    private void Touch() { _dirty = true; }
    public void RaiseChanged() => Changed?.Invoke();

    // ------------------------------------------------------------------ embeddings

    public static string Encode(float[] v)
    {
        var b = new byte[v.Length];
        for (int i = 0; i < v.Length; i++) b[i] = unchecked((byte)(sbyte)Math.Clamp((int)Math.Round(v[i] * 127), -127, 127));
        return Convert.ToBase64String(b);
    }

    public static float[] Decode(string e)
    {
        if (string.IsNullOrEmpty(e)) return null;
        var b = Convert.FromBase64String(e);
        var v = new float[b.Length];
        double len = 0;
        for (int i = 0; i < b.Length; i++) { v[i] = (sbyte)b[i] / 127f; len += v[i] * v[i]; }
        len = Math.Sqrt(len) + 1e-12;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / len);
        return v;
    }

    private void RebuildSums()
    {
        _sums.Clear();
        foreach (var f in _d.Faces.Values)
            if (f.Person != null && f.Vector != null) AddToSum(f.Person, f.Vector, 1);
    }

    private void AddToSum(string person, float[] v, int sign)
    {
        if (!_sums.TryGetValue(person, out var s)) _sums[person] = s = new double[v.Length];
        for (int i = 0; i < v.Length; i++) s[i] += sign * v[i];
    }

    private double CosineToPerson(string person, float[] v)
    {
        if (!_sums.TryGetValue(person, out var s)) return -1;
        double dot = 0, len = 0;
        for (int i = 0; i < v.Length; i++) { dot += s[i] * v[i]; len += s[i] * s[i]; }
        return len <= 1e-12 ? -1 : dot / Math.Sqrt(len);
    }

    private void Assign(FaceInfo f)
    {
        string best = null;
        double bestCos = Threshold;
        foreach (var p in _d.People)
        {
            if (f.Not?.Contains(p.Id) == true) continue;
            var c = CosineToPerson(p.Id, f.Vector);
            if (c >= bestCos) { bestCos = c; best = p.Id; }
        }
        if (best == null)
        {
            var np = new PersonInfo { Cover = f.Id };
            _d.People.Add(np);
            best = np.Id;
        }
        SetPerson(f, best);
    }

    private void SetPerson(FaceInfo f, string person)
    {
        if (f.Person != null) AddToSum(f.Person, f.Vector, -1);
        f.Person = person;
        if (person != null) AddToSum(person, f.Vector, 1);
    }

    // ------------------------------------------------------------------ analysis results

    public static string RevOf(Photo p) => $"{Vision.Version}:{p.ModifiedTicks}:{p.Rotation}";

    public bool IsAnalyzed(Photo p)
    {
        lock (_lock) return _d.Photos.TryGetValue(p.Id, out var a) && a.Rev == RevOf(p);
    }

    public int AnalyzedCount(IEnumerable<Photo> photos)
    {
        lock (_lock) return photos.Count(p => _d.Photos.TryGetValue(p.Id, out var a) && a.Rev == RevOf(p));
    }

    /// <summary>Stores what was found in a photo (replacing anything found in it before) and groups its faces.</summary>
    public void SetResult(Photo p, VisionResult r)
    {
        lock (_lock)
        {
            ForgetLocked(p.Id);
            var a = new AnalyzedPhoto { Rev = RevOf(p) };
            foreach (var (id, prob) in r.Labels) a.Labels.Add(new[] { id, (int)Math.Round(prob * 100) });
            foreach (var ff in r.Faces)
            {
                if (Math.Min(ff.W, ff.H) < MinFacePixels) continue;
                var f = new FaceInfo
                {
                    Photo = p.Id, Score = (float)Math.Round(ff.Score, 3), E = Encode(ff.Embedding),
                    Box = new[] { Clamp01(ff.X / r.Width), Clamp01(ff.Y / r.Height), Clamp01(ff.W / r.Width), Clamp01(ff.H / r.Height) },
                };
                f.Vector = Decode(f.E);
                _d.Faces[f.Id] = f;
                a.Faces.Add(f.Id);
                Assign(f);
            }
            _d.Photos[p.Id] = a;
            Touch();
        }
    }

    /// <summary>Marks a photo as looked at with nothing found (it couldn't be read, say), so it isn't tried again until it changes.</summary>
    public void SetNothing(Photo p)
    {
        lock (_lock)
        {
            ForgetLocked(p.Id);
            _d.Photos[p.Id] = new AnalyzedPhoto { Rev = RevOf(p) };
            Touch();
        }
    }

    private static float Clamp01(float v) => (float)Math.Round(Math.Clamp(v, 0, 1), 4);

    /// <summary>Drops what was found in photos that are no longer in the library.</summary>
    public void Prune(Func<string, bool> exists)
    {
        lock (_lock)
        {
            foreach (var id in _d.Photos.Keys.Where(id => !exists(id)).ToList()) ForgetLocked(id);
            Touch();
        }
    }

    private void ForgetLocked(string photoId)
    {
        if (!_d.Photos.Remove(photoId, out var a)) return;
        foreach (var fid in a.Faces)
            if (_d.Faces.Remove(fid, out var f) && f.Person != null) AddToSum(f.Person, f.Vector, -1);
        DropEmptyPeople();
    }

    private void DropEmptyPeople()
    {
        var used = _d.Faces.Values.Where(f => f.Person != null).Select(f => f.Person).ToHashSet();
        _d.People.RemoveAll(p => !used.Contains(p.Id) && string.IsNullOrEmpty(p.Name));
        foreach (var p in _d.People)
            if (p.Cover == null || !_d.Faces.TryGetValue(p.Cover, out var c) || c.Person != p.Id)
                p.Cover = _d.Faces.Values.Where(f => f.Person == p.Id).OrderByDescending(f => f.Score * Math.Min(1, f.Box[2] * 4)).FirstOrDefault()?.Id;
        foreach (var k in _sums.Keys.Where(k => !used.Contains(k)).ToList()) _sums.Remove(k);
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Whole lower-case words for search: what's in the photo and the names of the people in it.</summary>
    public HashSet<string> SearchWords(string photoId)
    {
        var set = new HashSet<string>();
        foreach (var w in SearchText(photoId).Split(new[] { ' ', '|', ',', '-' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(w);
        return set;
    }

    /// <summary>True when the search word is one of the words, allowing a plural ("dogs", "beaches").</summary>
    public static bool WordMatches(HashSet<string> words, string t)
    {
        if (words.Contains(t)) return true;
        if (t.Length > 3 && t.EndsWith("es") && words.Contains(t[..^2])) return true;
        if (t.Length > 2 && t.EndsWith('s') && words.Contains(t[..^1])) return true;
        if (t.Length > 3 && t.EndsWith("ies") && words.Contains(t[..^3] + "y")) return true;
        return false;
    }

    /// <summary>Lower-case words for search: what's in the photo and the names of the people in it.</summary>
    public string SearchText(string photoId)
    {
        lock (_lock)
        {
            if (!_d.Photos.TryGetValue(photoId, out var a)) return "";
            var parts = new List<string>();
            foreach (var l in a.Labels)
            {
                if (l[1] < MinSearchPercent) continue;
                parts.Add(Vision.LabelName(l[0]));
                parts.AddRange(Vision.LabelWords(l[0]));
            }
            foreach (var fid in a.Faces)
                if (_d.Faces.TryGetValue(fid, out var f) && f.Person != null)
                {
                    var p = _d.People.FirstOrDefault(x => x.Id == f.Person);
                    if (!string.IsNullOrEmpty(p?.Name)) parts.Add(p.Name);
                }
            if (a.Faces.Count > 0) parts.Add("person people face");
            return string.Join(" | ", parts).ToLowerInvariant();
        }
    }

    public List<(int id, int percent)> LabelsOf(string photoId)
    {
        lock (_lock) return _d.Photos.TryGetValue(photoId, out var a) ? a.Labels.Select(l => (l[0], l[1])).ToList() : new();
    }

    public sealed record PersonView(PersonInfo Person, FaceInfo Cover, List<string> PhotoIds);

    /// <summary>People to show: named, or seen in at least 2 photos; most photos first, named before unnamed.</summary>
    public List<PersonView> People(Func<string, bool> photoShown, bool includeHidden = false)
    {
        lock (_lock)
        {
            var byPerson = new Dictionary<string, HashSet<string>>();
            foreach (var f in _d.Faces.Values)
                if (f.Person != null && photoShown(f.Photo))
                {
                    if (!byPerson.TryGetValue(f.Person, out var s)) byPerson[f.Person] = s = new();
                    s.Add(f.Photo);
                }
            var list = new List<PersonView>();
            foreach (var p in _d.People)
            {
                if (p.Hidden && !includeHidden) continue;
                var ids = byPerson.TryGetValue(p.Id, out var s) ? s.ToList() : new List<string>();
                if (ids.Count == 0 || (ids.Count < 2 && string.IsNullOrEmpty(p.Name))) continue;
                var cover = p.Cover != null && _d.Faces.TryGetValue(p.Cover, out var c) && photoShown(c.Photo) ? c
                    : _d.Faces.Values.Where(f => f.Person == p.Id && photoShown(f.Photo)).OrderByDescending(f => f.Score).First();
                list.Add(new PersonView(p, cover, ids));
            }
            return list.OrderBy(v => string.IsNullOrEmpty(v.Person.Name)).ThenByDescending(v => v.PhotoIds.Count).ToList();
        }
    }

    public PersonInfo GetPerson(string id) { lock (_lock) return _d.People.FirstOrDefault(p => p.Id == id); }
    public PersonInfo FindByName(string name) { lock (_lock) return _d.People.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase)); }

    public List<string> PhotosOf(string personId)
    {
        lock (_lock) return _d.Faces.Values.Where(f => f.Person == personId).Select(f => f.Photo).Distinct().ToList();
    }

    public List<PersonInfo> PeopleIn(string photoId)
    {
        lock (_lock)
        {
            if (!_d.Photos.TryGetValue(photoId, out var a)) return new();
            return a.Faces.Select(fid => _d.Faces.TryGetValue(fid, out var f) ? f.Person : null).Where(x => x != null).Distinct()
                .Select(id => _d.People.FirstOrDefault(p => p.Id == id)).Where(p => p != null).ToList();
        }
    }

    /// <summary>The broader words ("dog", "beach") found in the shown photos, most photos first, for suggestions.</summary>
    public List<(string word, int count)> Things(Func<string, bool> photoShown, int max = 24)
    {
        var counts = new Dictionary<string, int>();
        lock (_lock)
            foreach (var (id, a) in _d.Photos)
            {
                if (a.Labels.Count == 0 || !photoShown(id)) continue;
                var words = new HashSet<string>();
                foreach (var l in a.Labels) if (l[1] >= MinSearchPercent) foreach (var w in Vision.LabelWords(l[0])) words.Add(w);
                foreach (var w in words) counts[w] = counts.GetValueOrDefault(w) + 1;
            }
        string[] skip = { "animal", "nature", "landscape", "vehicle", "pet", "clothes", "puppy", "kitten", "bunny", "bug" };
        return counts.Where(kv => !skip.Contains(kv.Key) && kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(max).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ------------------------------------------------------------------ changes you make

    /// <summary>Names a person. If someone else already has that name, the two are merged. Returns the person kept.</summary>
    public PersonInfo Rename(string personId, string name)
    {
        name = name?.Trim();
        lock (_lock)
        {
            var p = _d.People.FirstOrDefault(x => x.Id == personId);
            if (p == null) return null;
            var other = string.IsNullOrEmpty(name) ? null : _d.People.FirstOrDefault(x => x.Id != personId && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (other != null) { MergeLocked(p.Id, other.Id); Touch(); return other; }
            p.Name = string.IsNullOrEmpty(name) ? null : name;
            Touch();
            return p;
        }
    }

    /// <summary>Moves every face of <paramref name="from"/> to <paramref name="into"/>.</summary>
    public void Merge(string from, string into)
    {
        lock (_lock) { MergeLocked(from, into); Touch(); }
    }

    private void MergeLocked(string from, string into)
    {
        if (from == into) return;
        foreach (var f in _d.Faces.Values.Where(f => f.Person == from).ToList()) SetPerson(f, into);
        var target = _d.People.FirstOrDefault(p => p.Id == into);
        var gone = _d.People.FirstOrDefault(p => p.Id == from);
        if (target != null && gone != null && string.IsNullOrEmpty(target.Name)) target.Name = gone.Name;
        _d.People.RemoveAll(p => p.Id == from);
        foreach (var f in _d.Faces.Values) if (f.Not?.Remove(from) == true && !f.Not.Contains(into)) f.Not.Add(into);
        DropEmptyPeople();
    }

    public void SetHidden(string personId, bool hidden)
    {
        lock (_lock) { var p = _d.People.FirstOrDefault(x => x.Id == personId); if (p != null) { p.Hidden = hidden; Touch(); } }
    }

    public void SetCover(string personId, string photoId)
    {
        lock (_lock)
        {
            var p = _d.People.FirstOrDefault(x => x.Id == personId);
            var f = _d.Faces.Values.FirstOrDefault(x => x.Person == personId && x.Photo == photoId);
            if (p != null && f != null) { p.Cover = f.Id; Touch(); }
        }
    }

    /// <summary>"That's not them": the person's faces in these photos are taken away and regrouped (never back to this person).</summary>
    public void NotThisPerson(string personId, IEnumerable<string> photoIds)
    {
        var set = photoIds.ToHashSet();
        lock (_lock)
        {
            foreach (var f in _d.Faces.Values.Where(f => f.Person == personId && set.Contains(f.Photo)).ToList())
            {
                (f.Not ??= new()).Add(personId);
                SetPerson(f, null);
                Assign(f);
            }
            DropEmptyPeople();
            Touch();
        }
    }

    /// <summary>Forgets every face and person (what's in each photo is kept).</summary>
    public void ForgetPeople()
    {
        lock (_lock)
        {
            _d.People.Clear();
            _d.Faces.Clear();
            _sums.Clear();
            foreach (var a in _d.Photos.Values) if (a.Faces.Count > 0) { a.Faces.Clear(); a.Rev = null; }
            Touch();
        }
    }

    public (int photos, int faces, int people) Counts()
    {
        lock (_lock) return (_d.Photos.Count, _d.Faces.Count, _d.People.Count);
    }
}
