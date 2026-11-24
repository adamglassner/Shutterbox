using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Shutterbox.Library;

/// <summary>A face found in a picture: where (in the analyzed picture's pixels), how sure, and its 128 numbers.</summary>
public sealed class FaceFound
{
    public float Score, X, Y, W, H;
    public float[] Landmarks; // 5 points: x0, y0, x1, y1, ...
    public float[] Embedding; // length 1
}

public sealed class VisionResult
{
    public int Width, Height;
    public List<FaceFound> Faces = new();
    public List<(int Id, float P)> Labels = new();
}

/// <summary>An RGB picture in memory (3 bytes a pixel, no padding).</summary>
public sealed class Rgb
{
    public readonly byte[] Data;
    public readonly int W, H;
    public Rgb(byte[] data, int w, int h) { Data = data; W = w; H = h; }

    public static Rgb From(BitmapSource src, int maxLongSide)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Rgb24, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 3;
        var data = new byte[stride * h];
        conv.CopyPixels(data, stride, 0);
        var img = new Rgb(data, w, h);
        if (Math.Max(w, h) > maxLongSide)
        {
            double s = (double)maxLongSide / Math.Max(w, h);
            img = img.Resize(Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
        }
        return img;
    }

    /// <summary>Channel c of the pixel at (x, y), or 0 outside the picture.</summary>
    private double At(int x, int y, int c) => x < 0 || y < 0 || x >= W || y >= H ? 0 : Data[(y * W + x) * 3 + c];

    /// <summary>Bilinear sample (pixels outside count as black), the same as tools/vision_ref.py.</summary>
    public void Sample(double sx, double sy, Span<double> rgb)
    {
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
        double fx = sx - x0, fy = sy - y0;
        for (int c = 0; c < 3; c++)
        {
            double top = At(x0, y0, c) * (1 - fx) + At(x0 + 1, y0, c) * fx;
            double bot = At(x0, y0 + 1, c) * (1 - fx) + At(x0 + 1, y0 + 1, c) * fx;
            rgb[c] = top * (1 - fy) + bot * fy;
        }
    }

    /// <summary>Half-pixel-centred bilinear resize.</summary>
    public Rgb Resize(int nw, int nh)
    {
        var o = new byte[nw * nh * 3];
        Span<double> px = stackalloc double[3];
        for (int y = 0; y < nh; y++)
        {
            double sy = Math.Clamp((y + 0.5) * H / nh - 0.5, 0, H - 1);
            for (int x = 0; x < nw; x++)
            {
                double sx = Math.Clamp((x + 0.5) * W / nw - 0.5, 0, W - 1);
                Sample(sx, sy, px);
                int i = (y * nw + x) * 3;
                o[i] = (byte)Math.Clamp(px[0], 0, 255);
                o[i + 1] = (byte)Math.Clamp(px[1], 0, 255);
                o[i + 2] = (byte)Math.Clamp(px[2], 0, 255);
            }
        }
        return new Rgb(o, nw, nh);
    }
}

/// <summary>
/// Finds faces and recognises what's in a photo, on this PC, with three small ONNX models (YuNet, SFace and
/// MobileNetV2 from OpenCV Zoo; see mobile/assets/models/NOTICE.md). The steps match tools/vision_ref.py and the
/// Android app. Thread-safe; the models load on first use.
/// </summary>
public static class Vision
{
    public const string Version = "v1";
    public const int AnalyzeLongSide = 1280;
    private const int DetSize = 640;
    private static readonly double[,] Template = { { 38.2946, 51.6963 }, { 73.5318, 51.5014 }, { 56.0252, 71.7366 }, { 41.5493, 92.3655 }, { 70.7299, 92.2041 } };
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f }, Std = { 0.229f, 0.224f, 0.225f };

    private static readonly object Lock = new();
    private static InferenceSession _det, _rec, _cls;
    private static string[] _names;
    private static string[][] _words;
    public static string LoadError { get; private set; }

    private static byte[] Resource(string name)
    {
        using var s = typeof(Vision).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Loads the models (once). False if they couldn't be loaded; <see cref="LoadError"/> says why.</summary>
    public static bool EnsureLoaded()
    {
        lock (Lock)
        {
            if (_det != null) return true;
            if (LoadError != null) return false;
            try
            {
                var o = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4), InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                o.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
                _det = new InferenceSession(Resource("face_detection_yunet_2023mar.onnx"), o);
                _rec = new InferenceSession(Resource("face_recognition_sface_2021dec_int8.onnx"), o);
                _cls = new InferenceSession(Resource("image_classification_mobilenetv2_2022apr_int8.onnx"), o);
                LoadWords();
                return true;
            }
            catch (Exception ex)
            {
                LoadError = $"{ex.GetType().Name}: {ex.Message}";
                _det = null;
                return false;
            }
        }
    }

    private static void LoadWords()
    {
        if (_names != null) return;
        var lines = System.Text.Encoding.UTF8.GetString(Resource("vision_words.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _names = new string[lines.Length];
        _words = new string[lines.Length][];
        for (int i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].TrimEnd('\r').Split('|');
            _names[i] = parts[0];
            _words[i] = parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        }
    }

    /// <summary>The class's own name, e.g. "golden retriever".</summary>
    public static string LabelName(int id) { lock (Lock) LoadWords(); return id >= 0 && id < _names.Length ? _names[id] : ""; }
    /// <summary>Broader words for the class, e.g. dog, animal, pet.</summary>
    public static string[] LabelWords(int id) { lock (Lock) LoadWords(); return id >= 0 && id < _words.Length ? _words[id] : Array.Empty<string>(); }

    public static VisionResult Analyze(BitmapSource upright) => Analyze(Rgb.From(upright, AnalyzeLongSide));

    public static VisionResult Analyze(Rgb img)
    {
        if (!EnsureLoaded()) throw new InvalidOperationException("The models couldn't be loaded: " + LoadError);
        var r = new VisionResult { Width = img.W, Height = img.H };
        foreach (var f in Detect(img))
        {
            f.Embedding = Embed(img, f.Landmarks);
            r.Faces.Add(f);
        }
        r.Labels = Classify(img);
        return r;
    }

    // ------------------------------------------------------------------ faces

    public static List<FaceFound> Detect(Rgb img, float threshold = 0.8f, float nms = 0.3f)
    {
        double scale = Math.Min(1.0, Math.Min((double)DetSize / img.W, (double)DetSize / img.H));
        var small = scale < 1 ? img.Resize(Math.Max(1, (int)Math.Round(img.W * scale)), Math.Max(1, (int)Math.Round(img.H * scale))) : img;
        const int plane = DetSize * DetSize;
        var buf = new float[3 * plane];
        for (int y = 0; y < small.H; y++)
            for (int x = 0; x < small.W; x++)
            {
                int s = (y * small.W + x) * 3, d = y * DetSize + x;
                buf[d] = small.Data[s + 2];             // B
                buf[plane + d] = small.Data[s + 1];     // G
                buf[2 * plane + d] = small.Data[s];     // R
            }
        var outs = new Dictionary<string, float[]>();
        lock (_det)
        {
            using var res = _det.Run(new[] { NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(buf, new[] { 1, 3, DetSize, DetSize })) });
            foreach (var o in res) outs[o.Name] = o.AsEnumerable<float>().ToArray();
        }
        var faces = new List<FaceFound>();
        foreach (int st in new[] { 8, 16, 32 })
        {
            int cols = DetSize / st;
            float[] cls = outs[$"cls_{st}"], obj = outs[$"obj_{st}"], box = outs[$"bbox_{st}"], kps = outs[$"kps_{st}"];
            for (int i = 0; i < cls.Length; i++)
            {
                double score = Math.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (score < threshold) continue;
                int row = i / cols, col = i % cols;
                double cx = (col + box[i * 4]) * st, cy = (row + box[i * 4 + 1]) * st;
                double bw = Math.Exp(box[i * 4 + 2]) * st, bh = Math.Exp(box[i * 4 + 3]) * st;
                var lm = new float[10];
                for (int k = 0; k < 5; k++)
                {
                    lm[2 * k] = (float)((kps[i * 10 + 2 * k] + col) * st / scale);
                    lm[2 * k + 1] = (float)((kps[i * 10 + 2 * k + 1] + row) * st / scale);
                }
                faces.Add(new FaceFound { Score = (float)score, X = (float)((cx - bw / 2) / scale), Y = (float)((cy - bh / 2) / scale), W = (float)(bw / scale), H = (float)(bh / scale), Landmarks = lm });
            }
        }
        faces.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<FaceFound>();
        foreach (var f in faces)
            if (kept.All(k => Iou(f, k) < nms)) kept.Add(f);
        return kept;
    }

    private static double Iou(FaceFound a, FaceFound b)
    {
        double iw = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X));
        double ih = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        double inter = iw * ih;
        return inter / (a.W * a.H + b.W * b.H - inter + 1e-9);
    }

    /// <summary>The similarity transform (rotation, uniform scale, shift) taking the 5 landmarks onto the template: [a, -b, tx; b, a, ty].</summary>
    public static double[] Similarity(float[] lm)
    {
        double msx = 0, msy = 0, mdx = 0, mdy = 0;
        for (int k = 0; k < 5; k++) { msx += lm[2 * k]; msy += lm[2 * k + 1]; mdx += Template[k, 0]; mdy += Template[k, 1]; }
        msx /= 5; msy /= 5; mdx /= 5; mdy /= 5;
        double num_a = 0, num_b = 0, den = 0;
        for (int k = 0; k < 5; k++)
        {
            double sx = lm[2 * k] - msx, sy = lm[2 * k + 1] - msy, dx = Template[k, 0] - mdx, dy = Template[k, 1] - mdy;
            num_a += sx * dx + sy * dy;
            num_b += sx * dy - sy * dx;
            den += sx * sx + sy * sy;
        }
        double a = num_a / den, b = num_b / den;
        return new[] { a, -b, mdx - (a * msx - b * msy), b, a, mdy - (b * msx + a * msy) };
    }

    public static float[] Embed(Rgb img, float[] landmarks)
    {
        var m = Similarity(landmarks);
        double a = m[0], b = m[3], tx = m[2], ty = m[5], det = a * a + b * b;
        const int n = 112, plane = n * n;
        var buf = new float[3 * plane];
        Span<double> px = stackalloc double[3];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double u = x - tx, v = y - ty;
                double sx = (a * u + b * v) / det, sy = (-b * u + a * v) / det;
                img.Sample(sx, sy, px);
                int d = y * n + x;
                buf[d] = (float)px[0]; buf[plane + d] = (float)px[1]; buf[2 * plane + d] = (float)px[2];
            }
        float[] e;
        lock (_rec)
        {
            using var res = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor("data", new DenseTensor<float>(buf, new[] { 1, 3, n, n })) });
            e = res.First().AsEnumerable<float>().ToArray();
        }
        double len = Math.Sqrt(e.Sum(v => (double)v * v)) + 1e-12;
        for (int i = 0; i < e.Length; i++) e[i] = (float)(e[i] / len);
        return e;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }

    // ------------------------------------------------------------------ things

    public static List<(int Id, float P)> Classify(Rgb img, int top = 5, float minimum = 0.06f)
    {
        double scale = 256.0 / Math.Min(img.W, img.H);
        int nw = Math.Max(224, (int)Math.Round(img.W * scale)), nh = Math.Max(224, (int)Math.Round(img.H * scale));
        var r = img.Resize(nw, nh);
        int x0 = (nw - 224) / 2, y0 = (nh - 224) / 2;
        const int plane = 224 * 224;
        var buf = new float[3 * plane];
        for (int y = 0; y < 224; y++)
            for (int x = 0; x < 224; x++)
            {
                int s = ((y + y0) * nw + x + x0) * 3, d = y * 224 + x;
                for (int c = 0; c < 3; c++) buf[c * plane + d] = (r.Data[s + c] / 255f - Mean[c]) / Std[c];
            }
        float[] logits;
        lock (_cls)
        {
            using var res = _cls.Run(new[] { NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(buf, new[] { 1, 3, 224, 224 })) });
            logits = res.First().AsEnumerable<float>().ToArray();
        }
        double max = logits.Max(), sum = 0;
        var p = new double[logits.Length];
        for (int i = 0; i < p.Length; i++) { p[i] = Math.Exp(logits[i] - max); sum += p[i]; }
        return Enumerable.Range(0, p.Length).OrderByDescending(i => p[i]).Take(top)
            .Where(i => p[i] / sum >= minimum).Select(i => (i, (float)(p[i] / sum))).ToList();
    }
}
