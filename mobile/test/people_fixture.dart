import 'dart:convert';
import 'dart:io';

import 'package:shutterbox/models.dart';
import 'package:shutterbox/vision/people.dart';
import 'package:shutterbox/vision/vision.dart';

import 'fake_source.dart';

/// What tools/vision_ref.py found in tests/vision (faces with their 128 numbers, and things).
Map<String, dynamic> visionExpected() => jsonDecode(File('../tests/vision/expected.json').readAsStringSync()) as Map<String, dynamic>;

VisionWords visionWords() => VisionWords(File('assets/models/vision_words.txt').readAsStringSync());

/// The reference's findings as if the phone had found them.
VisionResult resultFor(Map<String, dynamic> e) {
  final size = e['size'] as List;
  final r = VisionResult((size[0] as num).toInt(), (size[1] as num).toInt());
  for (final f in e['faces'] as List) {
    final m = f as Map<String, dynamic>;
    final b = [for (final v in m['box'] as List) (v as num).toDouble()];
    final face = FaceFound((m['score'] as num).toDouble(), b[0], b[1], b[2], b[3], [for (final p in m['landmarks'] as List) for (final v in p as List) (v as num).toDouble()]);
    face.embedding = [for (final v in m['embedding'] as List) (v as num).toDouble()];
    r.faces.add(face);
  }
  r.labels = [for (final l in e['labels'] as List) (((l as Map)['id'] as num).toInt(), (l['p'] as num).toDouble())];
  return r;
}

/// Adds the test pictures to [source] and returns items for them (two of each, so People and Things have something
/// to show), with a PeopleStore that has "looked at" them. The astronaut is named Eileen.
(List<Item>, PeopleStore) samplePeople(FakeSource source, {DateTime? now}) {
  final exp = visionExpected();
  final items = <Item>[];
  final t = now ?? DateTime(2026, 11, 22, 12);
  var n = 0;
  for (final name in exp.keys) {
    source.bytes['v-$name'] = File('../tests/vision/$name').readAsBytesSync();
    final size = (exp[name] as Map<String, dynamic>)['size'] as List;
    for (var copy = 0; copy < (name.startsWith('astronaut') ? 1 : 2); copy++) {
      items.add(Item(
        id: 'v${n++}',
        name: 'IMG_${7100 + n}.jpg',
        taken: t.subtract(Duration(days: n * 3)),
        folder: 'Camera',
        folderId: 'f0',
        width: (size[0] as num).toInt(),
        height: (size[1] as num).toInt(),
        source: 'v-$name',
      ));
    }
  }
  final store = PeopleStore(visionWords());
  for (final i in items) {
    final name = '${i.source}'.substring(2);
    store.setResult(i.id, 'test', resultFor(exp[name] as Map<String, dynamic>));
  }
  final eileen = store.peopleIn(items.first.id).single;
  store.rename(eileen.id, 'Eileen');
  return (items, store);
}
