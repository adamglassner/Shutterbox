import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:shutterbox/vision/analyzer.dart';
import 'package:shutterbox/vision/people.dart';
import 'package:shutterbox/vision/vision.dart';

import 'fake_source.dart';
import 'people_fixture.dart';

void main() {
  group('Vision steps', () {
    test('the similarity transform matches the reference', () {
      // tools/vision_ref.py's Umeyama for astronaut.jpg's landmarks
      final m = Vision.similarity([200.2, 101.6, 247.2, 102.4, 220.2, 124.4, 198.5, 142.8, 239.5, 143.7]);
      expect(m[0], closeTo(0.844842, 1e-5));
      expect(m[1], closeTo(0.064114, 1e-5));
      expect(m[2], closeTo(-138.67, 1e-2));
      expect(m[3], closeTo(-0.064114, 1e-5));
      expect(m[5], closeTo(-17.821, 1e-2));
    });

    test('decoding the detector', () {
      Map<String, Float32List> empty() => {
            for (final s in [8, 16, 32]) ...{
              'cls_$s': Float32List((640 ~/ s) * (640 ~/ s)),
              'obj_$s': Float32List((640 ~/ s) * (640 ~/ s)),
              'bbox_$s': Float32List((640 ~/ s) * (640 ~/ s) * 4),
              'kps_$s': Float32List((640 ~/ s) * (640 ~/ s) * 10),
            }
          };
      final o = empty();
      // a face in the stride-16 grid at row 10, column 20
      const i = 10 * 40 + 20;
      o['cls_16']![i] = 0.95;
      o['obj_16']![i] = 0.9;
      o['bbox_16']!.setAll(i * 4, [0.5, 0.25, log(4.0), log(5.0)]);
      o['kps_16']!.setAll(i * 10, [0, 0, 1, 0, 0.5, 0.5, 0, 1, 1, 1]);
      // a weaker overlapping one (suppressed) and one below the threshold
      o['cls_16']![i + 1] = 0.85;
      o['obj_16']![i + 1] = 0.85;
      o['bbox_16']!.setAll((i + 1) * 4, [-0.4, 0.25, log(4.0), log(5.0)]);
      o['cls_8']![5] = 0.5;
      o['obj_8']![5] = 0.5;
      final faces = Vision.decodeFaces(o, 0.5);
      expect(faces.length, 1);
      final f = faces.first;
      expect(f.score, closeTo(sqrt(0.95 * 0.9), 1e-6));
      // centre (20.5 * 16, 10.25 * 16) = (328, 164), 64 x 80, then / scale 0.5
      expect(f.x, closeTo((328 - 32) / 0.5, 1e-3));
      expect(f.y, closeTo((164 - 40) / 0.5, 1e-3));
      expect(f.w, closeTo(128, 1e-3));
      expect(f.h, closeTo(160, 1e-3));
      expect(f.landmarks.sublist(0, 4), [640, 320, 672, 320]);
    });

    test('pictures: resize, fit, rotate, the detector and classifier inputs', () {
      final img = Rgb(Uint8List.fromList(List.generate(4 * 2 * 3, (i) => i * 10)), 4, 2);
      final r = rotate(img, 1);
      expect([r.w, r.h], [2, 4]);
      // the top-left pixel of the turned picture is the bottom-left of the original
      expect(r.data.sublist(0, 3), img.data.sublist((1 * 4 + 0) * 3, (1 * 4 + 0) * 3 + 3));
      expect(rotate(rotate(img, 2), 2).data, img.data);
      expect(rotate(rotate(img, 1), 3).data, img.data);
      final big = Rgb(Uint8List(2000 * 1000 * 3), 2000, 1000).fit(1280);
      expect([big.w, big.h], [1280, 640]);
      final solid = Rgb(Uint8List.fromList([for (var i = 0; i < 100 * 50; i++) ...[200, 100, 50]]), 100, 50);
      expect(solid.resize(37, 11).data.toSet(), {200, 100, 50});
      final (det, scale) = Vision.detectorInput(solid);
      expect(scale, 1.0);
      expect(det.length, 3 * 640 * 640);
      expect([det[0], det[640 * 640], det[2 * 640 * 640]], [50, 100, 200]); // BGR
      expect(det[60], 0); // outside the picture: black
      final cls = Vision.classifierInput(solid);
      expect(cls[0], closeTo((200 / 255 - 0.485) / 0.229, 1e-4));
      final top = Vision.topLabels(Float32List.fromList([for (var i = 0; i < 1000; i++) i == 7 ? 10.0 : (i == 3 ? 8.0 : 0.0)]));
      expect(top.first.$1, 7);
      expect(top[1].$1, 3);
      expect(top.length, 2);
    });

    test('words for each thing', () {
      final w = visionWords();
      expect(w.names.length, 1000);
      expect(w.name(207), 'golden retriever');
      expect(w.broader(207), containsAll(['dog', 'animal', 'pet']));
      expect(w.broader(978), contains('beach'));
      expect(w.broader(967), contains('coffee'));
    });
  });

  group('People', () {
    test('faces of one person are grouped, others aren\'t', () {
      final (items, store) = samplePeople(FakeSource());
      final list = store.list((_) => true);
      expect(list.length, 2, reason: 'Eileen (2 photos), and the cameraman (2 copies of one picture)');
      expect(list.first.person.name, 'Eileen');
      expect(list.first.photoIds.length, 2);
      expect(items.length, 8);
    });

    test('grouping, names, search, corrections', () async {
      final words = visionWords();
      final exp = visionExpected();
      final store = PeopleStore(words);
      for (final name in exp.keys) {
        store.setResult(name, 'r1', resultFor(exp[name] as Map<String, dynamic>));
      }
      expect(store.isAnalyzed('cat.jpg', 'r1'), isTrue);
      expect(store.isAnalyzed('cat.jpg', 'r2'), isFalse);
      final her = store.peopleIn('astronaut.jpg').single;
      expect(store.peopleIn('astronaut2.jpg').single.id, her.id);
      final him = store.peopleIn('cameraman.jpg').single;
      expect(him.id, isNot(her.id));
      // listed: 2 photos, or a name
      expect(store.list((_) => true).map((v) => v.person.id), [her.id]);
      store.rename(him.id, 'Sam');
      expect(store.list((_) => true).map((v) => v.person.id), [him.id, her.id], reason: 'named first');

      // search
      expect(store.matches('cat.jpg', 'cat'), isTrue);
      expect(store.matches('cat.jpg', 'cats'), isTrue);
      expect(store.matches('cat.jpg', 'kitten'), isTrue);
      expect(store.matches('coffee.jpg', 'coffee'), isTrue);
      expect(store.matches('coffee.jpg', 'cat'), isFalse);
      expect(store.matches('cameraman.jpg', 'sam'), isTrue);
      expect(store.matches('cameraman.jpg', 'tripod'), isTrue);
      expect(store.matches('astronaut.jpg', 'person'), isTrue);
      expect(PeopleStore.wordMatches({'puppy'}, 'puppies'), isTrue);
      expect(PeopleStore.wordMatches({'beach'}, 'beaches'), isTrue);

      // a name someone has already: put together
      final kept = store.rename(her.id, 'sam');
      expect(kept!.id, him.id);
      expect(store.photosOf(him.id).toSet(), {'astronaut.jpg', 'astronaut2.jpg', 'cameraman.jpg'});
      // not them: goes to someone new, never back
      store.notThisPerson(him.id, ['astronaut.jpg', 'astronaut2.jpg']);
      expect(store.photosOf(him.id), ['cameraman.jpg']);
      final again = store.peopleIn('astronaut.jpg').single;
      expect(store.peopleIn('astronaut2.jpg').single.id, again.id, reason: 'the two faces still find each other');
      // same person as
      store.merge(again.id, him.id);
      expect(store.photosOf(him.id).length, 3);
      store.setHidden(him.id, true);
      expect(store.list((_) => true), isEmpty);
      expect(store.list((_) => true, includeHidden: true).length, 1);
      store.setHidden(him.id, false);
      store.setCover(him.id, 'astronaut2.jpg');
      expect(store.faces[store.person(him.id)!.cover]!.photo, 'astronaut2.jpg');

      // photos that go away take their faces with them
      store.prune((id) => id != 'cameraman.jpg');
      expect(store.photos.containsKey('cameraman.jpg'), isFalse);
      expect(store.faces.values.where((f) => f.photo == 'cameraman.jpg'), isEmpty);

      // saved and loaded (the same layout as the Windows app's people.json)
      final json = store.toJsonString();
      expect(json, contains('"Photos"'));
      expect(json, contains('"People"'));
      final f = File('${Directory.systemTemp.createTempSync('sb-people-').path}/people.json')..writeAsStringSync(json);
      final c = PeopleStore(words, file: f);
      await c.load();
      expect(c.faces.length, store.faces.length);
      expect(c.person(him.id)!.name, 'Sam');
      expect(c.photosOf(him.id).toSet(), store.photosOf(him.id).toSet());
      // forget everything
      c.forgetPeople();
      expect(c.people, isEmpty);
      expect(c.isAnalyzed('astronaut.jpg', 'r1'), isFalse, reason: 'faces are found again');
      expect(c.isAnalyzed('coffee.jpg', 'r1'), isTrue, reason: 'photos without faces keep their things');
    });

    test('embeddings survive being stored as bytes', () {
      final e = [for (final v in (((visionExpected()['astronaut.jpg'] as Map)['faces'] as List).first as Map)['embedding'] as List) (v as num).toDouble()];
      final back = PeopleStore.decode(PeopleStore.encode(e));
      expect(Vision.cosine(back, e), greaterThan(0.995));
    });

    test('things: suggestions from what was found', () {
      final (_, store) = samplePeople(FakeSource());
      final things = store.things((_) => true);
      expect(things.map((t) => t.$1), containsAll(['cat', 'coffee']));
    });
  });
}
