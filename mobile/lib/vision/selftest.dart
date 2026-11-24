import 'dart:convert';
import 'dart:io';

import 'package:flutter/services.dart';

import 'analyzer.dart';
import 'people.dart';
import 'vision.dart';

/// Checks the phone finds what tools/vision_ref.py finds in tests/vision (CI starts the release build on an emulator
/// with `--es selftest vision --es dir <folder>` and reads the "VISION" lines from the log).
Future<void> visionSelfTest(String dir) async {
  void say(String s) {
    // ignore: avoid_print
    print('VISION $s');
  }

  var fails = 0;
  void check(bool ok, String what) {
    if (!ok) fails++;
    say('${ok ? 'PASS' : 'FAIL'} $what');
  }

  try {
    final vision = Vision(NativeVisionEngine());
    final words = VisionWords(await rootBundle.loadString('assets/models/vision_words.txt'));
    check(words.name(207) == 'golden retriever' && words.broader(207).contains('dog'), 'words for each thing');
    final expected = jsonDecode(await File('$dir/expected.json').readAsString()) as Map<String, dynamic>;
    final found = <String, VisionResult>{};
    for (final e in expected.entries) {
      final sw = Stopwatch()..start();
      final rgb = await decodeForVision(await File('$dir/${e.key}').readAsBytes());
      final r = await vision.analyze(rgb);
      found[e.key] = r;
      final want = (e.value as Map<String, dynamic>)['faces'] as List;
      check(r.faces.length == want.length, '${e.key}: ${r.faces.length} faces (expected ${want.length}) in ${sw.elapsedMilliseconds} ms');
      for (var i = 0; i < want.length && i < r.faces.length; i++) {
        final box = [for (final v in (want[i] as Map)['box'] as List) (v as num).toDouble()];
        final f = r.faces[i];
        final tol = box[2] * 0.08 < 4 ? 4 : box[2] * 0.08;
        check((f.x - box[0]).abs() < tol && (f.y - box[1]).abs() < tol && (f.w - box[2]).abs() < tol, '${e.key}: face at ${f.x.round()},${f.y.round()} ${f.w.round()}x${f.h.round()}');
        final emb = [for (final v in (want[i] as Map)['embedding'] as List) (v as num).toDouble()];
        final cos = Vision.cosine(f.embedding, emb);
        check(cos >= 0.9, '${e.key}: embedding cosine with the reference ${cos.toStringAsFixed(3)}');
      }
      final labels = (e.value as Map<String, dynamic>)['labels'] as List;
      final top = labels.first as Map;
      say('INFO ${e.key}: ${r.labels.map((l) => '${words.name(l.$1)} ${l.$2.toStringAsFixed(2)}').join(', ')}');
      if ((top['p'] as num) >= 0.5) check(r.labels.isNotEmpty && r.labels.first.$1 == top['id'], '${e.key}: top thing ${words.name(top['id'] as int)}');
    }
    final same = Vision.cosine(found['astronaut.jpg']!.faces.first.embedding, found['astronaut2.jpg']!.faces.first.embedding);
    final other = Vision.cosine(found['astronaut.jpg']!.faces.first.embedding, found['cameraman.jpg']!.faces.first.embedding);
    check(same >= PeopleStore.threshold + 0.1 && other < PeopleStore.threshold - 0.05, 'same person ${same.toStringAsFixed(3)}, different people ${other.toStringAsFixed(3)}');
    final store = PeopleStore(words);
    for (final e in found.entries) {
      store.setResult(e.key, 'test', e.value);
    }
    final list = store.list((_) => true);
    check(list.length == 1 && list.first.photoIds.toSet().containsAll(['astronaut.jpg', 'astronaut2.jpg']), 'grouped into people');
    check(store.matches('cat.jpg', 'cat') && store.matches('coffee.jpg', 'espresso') && !store.matches('coffee.jpg', 'cat'), 'search words');
  } catch (e, st) {
    fails++;
    say('FAIL $e ${st.toString().split('\n').take(4).join(' | ')}');
  }
  say(fails == 0 ? 'DONE all passed' : 'DONE $fails failed');
}

/// Extras Android started Shutterbox with (only the self-test uses any).
Future<Map<String, String>> launchArgs() async {
  try {
    final m = await const MethodChannel('shutterbox/vision').invokeMapMethod<String, String>('launchArgs');
    return m ?? const {};
  } catch (_) {
    return const {};
  }
}
