import 'dart:isolate';
import 'dart:math';
import 'dart:typed_data';

/// An RGB picture in memory (3 bytes a pixel, no padding).
class Rgb {
  Rgb(this.data, this.w, this.h);
  final Uint8List data;
  final int w, h;

  /// From the RGBA bytes dart:ui gives (Image.toByteData(format: rawRgba)).
  factory Rgb.fromRgba(Uint8List rgba, int w, int h) {
    final out = Uint8List(w * h * 3);
    for (var i = 0, j = 0; i < w * h; i++, j += 4) {
      out[i * 3] = rgba[j];
      out[i * 3 + 1] = rgba[j + 1];
      out[i * 3 + 2] = rgba[j + 2];
    }
    return Rgb(out, w, h);
  }

  double _at(int x, int y, int c) => (x < 0 || y < 0 || x >= w || y >= h) ? 0 : data[(y * w + x) * 3 + c].toDouble();

  /// Bilinear sample (pixels outside count as black), the same as tools/vision_ref.py; writes R, G, B into [out].
  void sample(double sx, double sy, Float64List out) {
    final x0 = sx.floor(), y0 = sy.floor();
    final fx = sx - x0, fy = sy - y0;
    for (var c = 0; c < 3; c++) {
      final top = _at(x0, y0, c) * (1 - fx) + _at(x0 + 1, y0, c) * fx;
      final bot = _at(x0, y0 + 1, c) * (1 - fx) + _at(x0 + 1, y0 + 1, c) * fx;
      out[c] = top * (1 - fy) + bot * fy;
    }
  }

  /// Half-pixel-centred bilinear resize.
  Rgb resize(int nw, int nh) {
    final o = Uint8List(nw * nh * 3);
    final px = Float64List(3);
    for (var y = 0; y < nh; y++) {
      final sy = ((y + 0.5) * h / nh - 0.5).clamp(0.0, h - 1.0);
      for (var x = 0; x < nw; x++) {
        final sx = ((x + 0.5) * w / nw - 0.5).clamp(0.0, w - 1.0);
        sample(sx, sy, px);
        final i = (y * nw + x) * 3;
        o[i] = px[0].round().clamp(0, 255);
        o[i + 1] = px[1].round().clamp(0, 255);
        o[i + 2] = px[2].round().clamp(0, 255);
      }
    }
    return Rgb(o, nw, nh);
  }

  /// No larger than [maxLongSide] on its long side.
  Rgb fit(int maxLongSide) {
    if (max(w, h) <= maxLongSide) return this;
    final s = maxLongSide / max(w, h);
    return resize(max(1, (w * s).round()), max(1, (h * s).round()));
  }
}

/// A face: where (in the analyzed picture's pixels), how sure, its 5 landmarks and its 128 numbers (length 1).
class FaceFound {
  FaceFound(this.score, this.x, this.y, this.w, this.h, this.landmarks);
  final double score, x, y, w, h;
  final List<double> landmarks; // x0, y0, x1, y1, ...
  List<double> embedding = const [];
}

class VisionResult {
  VisionResult(this.width, this.height);
  final int width, height;
  final faces = <FaceFound>[];
  List<(int, double)> labels = const [];
}

/// Runs an ONNX model: the Android app does it in Kotlin with ONNX Runtime (MainActivity, tool/patch_native.py).
abstract class VisionEngine {
  /// Feeds [data] (float32, [shape]) to [model]'s input [input]; returns every output by name.
  Future<Map<String, Float32List>> run(String model, String input, Float32List data, List<int> shape);
}

const detModel = 'face_detection_yunet_2023mar.onnx';
const recModel = 'face_recognition_sface_2021dec_int8.onnx';
const clsModel = 'image_classification_mobilenetv2_2022apr_int8.onnx';

/// Finds faces and recognises what's in a photo, on the phone, with the same three models and steps as the Windows
/// app (see tools/vision_ref.py, the reference both are checked against).
class Vision {
  Vision(this.engine);
  final VisionEngine engine;

  static const version = 'v1';
  static const analyzeLongSide = 1280;
  static const detSize = 640;
  static const template = [38.2946, 51.6963, 73.5318, 51.5014, 56.0252, 71.7366, 41.5493, 92.3655, 70.7299, 92.2041];
  static const mean = [0.485, 0.456, 0.406], std = [0.229, 0.224, 0.225];

  Future<VisionResult> analyze(Rgb img) async {
    final r = VisionResult(img.w, img.h);
    final det = await Isolate.run(() => detectorInput(img));
    final outs = await engine.run(detModel, 'input', det.$1, const [1, 3, detSize, detSize]);
    final faces = decodeFaces(outs, det.$2);
    if (faces.isNotEmpty) {
      final crops = await Isolate.run(() => [for (final f in faces) faceInput(img, f.landmarks)]);
      for (var i = 0; i < faces.length; i++) {
        final e = (await engine.run(recModel, 'data', crops[i], const [1, 3, 112, 112])).values.first;
        faces[i].embedding = normalize(e);
        r.faces.add(faces[i]);
      }
    }
    final cls = await Isolate.run(() => classifierInput(img));
    final logits = (await engine.run(clsModel, 'input', cls, const [1, 3, 224, 224])).values.first;
    r.labels = topLabels(logits);
    return r;
  }

  // ------------------------------------------------------------------ faces

  /// The detector's input (BGR, 0-255, the picture scaled down to fit 640x640 at the top left) and the scale used.
  static (Float32List, double) detectorInput(Rgb img) {
    final scale = min(1.0, min(detSize / img.w, detSize / img.h));
    final small = scale < 1 ? img.resize(max(1, (img.w * scale).round()), max(1, (img.h * scale).round())) : img;
    const plane = detSize * detSize;
    final buf = Float32List(3 * plane);
    for (var y = 0; y < small.h; y++) {
      for (var x = 0; x < small.w; x++) {
        final s = (y * small.w + x) * 3, d = y * detSize + x;
        buf[d] = small.data[s + 2].toDouble();
        buf[plane + d] = small.data[s + 1].toDouble();
        buf[2 * plane + d] = small.data[s].toDouble();
      }
    }
    return (buf, scale);
  }

  /// YuNet's outputs to faces (score >= [threshold], then non-maximum suppression), in the picture's pixels.
  static List<FaceFound> decodeFaces(Map<String, Float32List> outs, double scale, {double threshold = 0.8, double nms = 0.3}) {
    final faces = <FaceFound>[];
    for (final st in const [8, 16, 32]) {
      final cols = detSize ~/ st;
      final cls = outs['cls_$st']!, obj = outs['obj_$st']!, box = outs['bbox_$st']!, kps = outs['kps_$st']!;
      for (var i = 0; i < cls.length; i++) {
        final score = sqrt(cls[i].clamp(0.0, 1.0) * obj[i].clamp(0.0, 1.0));
        if (score < threshold) continue;
        final row = i ~/ cols, col = i % cols;
        final cx = (col + box[i * 4]) * st, cy = (row + box[i * 4 + 1]) * st;
        final bw = exp(box[i * 4 + 2]) * st, bh = exp(box[i * 4 + 3]) * st;
        final lm = <double>[
          for (var k = 0; k < 5; k++) ...[(kps[i * 10 + 2 * k] + col) * st / scale, (kps[i * 10 + 2 * k + 1] + row) * st / scale],
        ];
        faces.add(FaceFound(score, (cx - bw / 2) / scale, (cy - bh / 2) / scale, bw / scale, bh / scale, lm));
      }
    }
    faces.sort((a, b) => b.score.compareTo(a.score));
    final kept = <FaceFound>[];
    for (final f in faces) {
      if (kept.every((k) => iou(f, k) < nms)) kept.add(f);
    }
    return kept;
  }

  static double iou(FaceFound a, FaceFound b) {
    final iw = max(0.0, min(a.x + a.w, b.x + b.w) - max(a.x, b.x));
    final ih = max(0.0, min(a.y + a.h, b.y + b.h) - max(a.y, b.y));
    final inter = iw * ih;
    return inter / (a.w * a.h + b.w * b.h - inter + 1e-9);
  }

  /// The similarity transform taking the 5 landmarks onto ArcFace's template: [a, -b, tx, b, a, ty].
  static List<double> similarity(List<double> lm) {
    var msx = 0.0, msy = 0.0, mdx = 0.0, mdy = 0.0;
    for (var k = 0; k < 5; k++) {
      msx += lm[2 * k];
      msy += lm[2 * k + 1];
      mdx += template[2 * k];
      mdy += template[2 * k + 1];
    }
    msx /= 5;
    msy /= 5;
    mdx /= 5;
    mdy /= 5;
    var na = 0.0, nb = 0.0, den = 0.0;
    for (var k = 0; k < 5; k++) {
      final sx = lm[2 * k] - msx, sy = lm[2 * k + 1] - msy, dx = template[2 * k] - mdx, dy = template[2 * k + 1] - mdy;
      na += sx * dx + sy * dy;
      nb += sx * dy - sy * dx;
      den += sx * sx + sy * sy;
    }
    final a = na / den, b = nb / den;
    return [a, -b, mdx - (a * msx - b * msy), b, a, mdy - (b * msx + a * msy)];
  }

  /// The face lined up on a 112x112 square, RGB 0-255, for the recogniser.
  static Float32List faceInput(Rgb img, List<double> landmarks) {
    final m = similarity(landmarks);
    final a = m[0], b = m[3], tx = m[2], ty = m[5], det = a * a + b * b;
    const n = 112, plane = n * n;
    final buf = Float32List(3 * plane);
    final px = Float64List(3);
    for (var y = 0; y < n; y++) {
      for (var x = 0; x < n; x++) {
        final u = x - tx, v = y - ty;
        img.sample((a * u + b * v) / det, (-b * u + a * v) / det, px);
        final d = y * n + x;
        buf[d] = px[0];
        buf[plane + d] = px[1];
        buf[2 * plane + d] = px[2];
      }
    }
    return buf;
  }

  static List<double> normalize(Float32List v) {
    var len = 0.0;
    for (final x in v) {
      len += x * x;
    }
    len = sqrt(len) + 1e-12;
    return [for (final x in v) x / len];
  }

  static double cosine(List<double> a, List<double> b) {
    var s = 0.0;
    for (var i = 0; i < min(a.length, b.length); i++) {
      s += a[i] * b[i];
    }
    return s;
  }

  // ------------------------------------------------------------------ things

  /// Short side to 256, the middle 224x224, RGB normalised with ImageNet's mean and spread.
  static Float32List classifierInput(Rgb img) {
    final scale = 256 / min(img.w, img.h);
    final nw = max(224, (img.w * scale).round()), nh = max(224, (img.h * scale).round());
    final r = img.resize(nw, nh);
    final x0 = (nw - 224) ~/ 2, y0 = (nh - 224) ~/ 2;
    const plane = 224 * 224;
    final buf = Float32List(3 * plane);
    for (var y = 0; y < 224; y++) {
      for (var x = 0; x < 224; x++) {
        final s = ((y + y0) * nw + x + x0) * 3, d = y * 224 + x;
        for (var c = 0; c < 3; c++) {
          buf[c * plane + d] = (r.data[s + c] / 255 - mean[c]) / std[c];
        }
      }
    }
    return buf;
  }

  /// Softmax, then the top [top] with probability >= [minimum].
  static List<(int, double)> topLabels(Float32List logits, {int top = 5, double minimum = 0.06}) {
    final mx = logits.reduce(max);
    final p = [for (final l in logits) exp(l - mx)];
    final sum = p.fold<double>(0, (a, b) => a + b);
    final order = List<int>.generate(p.length, (i) => i)..sort((a, b) => p[b].compareTo(p[a]));
    return [for (final i in order.take(top)) if (p[i] / sum >= minimum) (i, p[i] / sum)];
  }
}

/// The words for each of the 1000 things (assets/models/vision_words.txt, made by tools/make_vision_words.py).
class VisionWords {
  VisionWords(String text) {
    for (final line in text.split('\n')) {
      if (line.trim().isEmpty) continue;
      final parts = line.trimRight().split('|');
      names.add(parts[0]);
      words.add(parts.length > 1 && parts[1].isNotEmpty ? parts[1].split(',') : const []);
    }
  }
  final names = <String>[];
  final words = <List<String>>[];

  String name(int id) => id >= 0 && id < names.length ? names[id] : '';
  List<String> broader(int id) => id >= 0 && id < words.length ? words[id] : const [];
}
