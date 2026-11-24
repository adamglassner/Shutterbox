import 'dart:async';
import 'dart:isolate';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import '../library.dart';
import '../models.dart';
import '../settings.dart';
import 'people.dart';
import 'vision.dart';

/// Runs the models with ONNX Runtime in Kotlin (MainActivity, written by tool/patch_native.py), off the UI thread.
class NativeVisionEngine implements VisionEngine {
  static const _channel = MethodChannel('shutterbox/vision');

  @override
  Future<Map<String, Float32List>> run(String model, String input, Float32List data, List<int> shape) async {
    final out = await _channel.invokeMapMethod<String, Uint8List>('run', {
      'model': 'assets/models/$model',
      'input': input,
      'data': data.buffer.asUint8List(data.offsetInBytes, data.lengthInBytes),
      'shape': Int32List.fromList(shape),
    });
    if (out == null) throw StateError('no output from $model');
    return {for (final e in out.entries) e.key: _floats(e.value)};
  }

  static Float32List _floats(Uint8List b) {
    final copy = Uint8List.fromList(b); // aligned
    return copy.buffer.asFloat32List(0, copy.length ~/ 4);
  }
}

/// A picture from JPEG bytes, turned by [quarterTurns] clockwise and at most [Vision.analyzeLongSide] on its long side.
Future<Rgb> decodeForVision(Uint8List bytes, {int quarterTurns = 0}) async {
  final codec = await ui.instantiateImageCodec(bytes);
  final frame = await codec.getNextFrame();
  final img = frame.image;
  final data = await img.toByteData(format: ui.ImageByteFormat.rawRgba);
  final w = img.width, h = img.height;
  img.dispose();
  codec.dispose();
  if (data == null) throw StateError('could not read the picture');
  final rgba = data.buffer.asUint8List(data.offsetInBytes, data.lengthInBytes);
  return Isolate.run(() => rotate(Rgb.fromRgba(rgba, w, h).fit(Vision.analyzeLongSide), quarterTurns));
}

/// Turns a picture clockwise by quarter turns.
Rgb rotate(Rgb img, int quarterTurns) {
  final q = quarterTurns % 4;
  if (q == 0) return img;
  final w = img.w, h = img.h;
  final nw = q == 2 ? w : h, nh = q == 2 ? h : w;
  final o = Uint8List(w * h * 3);
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      int nx, ny;
      if (q == 1) {
        nx = h - 1 - y;
        ny = x;
      } else if (q == 2) {
        nx = w - 1 - x;
        ny = h - 1 - y;
      } else {
        nx = y;
        ny = w - 1 - x;
      }
      final s = (y * w + x) * 3, d = (ny * nw + nx) * 3;
      o[d] = img.data[s];
      o[d + 1] = img.data[s + 1];
      o[d + 2] = img.data[s + 2];
    }
  }
  return Rgb(o, nw, nh);
}

/// Looks through the library while Shutterbox is open (newest photos first), finding faces and what's in each photo,
/// and keeps [people] up to date. Pauses when Shutterbox goes to the background.
class Analyzer extends ChangeNotifier {
  Analyzer({required this.library, required this.people, required this.vision, required this.settings});

  final Library library;
  final PeopleStore people;
  final Vision vision;
  final Settings settings;

  int done = 0, total = 0;
  bool busy = false;
  String? error;
  bool _running = false, _paused = false;

  static String revOf(Item i) => PeopleStore.revOf('${i.width}x${i.height}', i.rotation);

  bool get finished => total > 0 && done >= total;

  void pause() => _paused = true;

  void resume() {
    _paused = false;
    poke();
  }

  /// Something changed (new photos, the setting): look again.
  void poke() {
    if (!_running && !_paused) unawaited(_loop());
  }

  void _count(List<Item> all) {
    total = all.length;
    done = all.where((i) => people.isAnalyzed(i.id, revOf(i))).length;
  }

  Future<void> _loop() async {
    if (_running) return;
    _running = true;
    var lastNotify = DateTime.now();
    try {
      while (!_paused && settings.findPeople && library.loaded) {
        final all = library.photos.toList();
        _count(all);
        people.prune((id) => library.byId(id) != null);
        final todo = all.where((i) => !people.isAnalyzed(i.id, revOf(i))).toList()..sort((a, b) => b.taken.compareTo(a.taken));
        if (todo.isEmpty) break;
        busy = true;
        notifyListeners();
        for (final i in todo.take(40)) {
          if (_paused || !settings.findPeople) break;
          final rev = revOf(i);
          Rgb? rgb;
          try {
            final bytes = await library.source.analysisJpeg(i);
            if (bytes != null) rgb = await decodeForVision(bytes, quarterTurns: i.rotation ~/ 90);
          } catch (_) {}
          if (rgb == null) {
            people.setNothing(i.id, rev);
          } else {
            try {
              people.setResult(i.id, rev, await vision.analyze(rgb));
              error = null;
            } catch (e) {
              // the models themselves failed: stop rather than mark every photo as done
              error = '$e';
              _paused = true;
              break;
            }
          }
          done++;
          if (DateTime.now().difference(lastNotify).inMilliseconds > 700) {
            lastNotify = DateTime.now();
            notifyListeners();
          }
          await Future<void>.delayed(const Duration(milliseconds: 20));
        }
        await people.save();
      }
    } finally {
      _running = false;
      busy = false;
      await people.save();
      notifyListeners();
    }
  }
}
