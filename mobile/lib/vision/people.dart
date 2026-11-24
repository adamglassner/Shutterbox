import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';

import 'vision.dart';

class PersonInfo {
  PersonInfo({String? id, this.name, this.hidden = false, this.cover}) : id = id ?? _newId();
  final String id;
  String? name;
  bool hidden;

  /// The face shown for this person.
  String? cover;

  bool get named => name != null && name!.isNotEmpty;

  Map<String, dynamic> toJson() => {'Id': id, if (named) 'Name': name, if (hidden) 'Hidden': true, if (cover != null) 'Cover': cover};
  factory PersonInfo.fromJson(Map<String, dynamic> j) => PersonInfo(id: j['Id'] as String, name: j['Name'] as String?, hidden: j['Hidden'] == true, cover: j['Cover'] as String?);
}

class FaceInfo {
  FaceInfo({String? id, required this.photo, required this.box, required this.score, required this.e, this.person, List<String>? not})
      : id = id ?? _newId(),
        not = not ?? [],
        vector = PeopleStore.decode(e);
  final String id;
  final String photo;

  /// x, y, width, height as fractions of the upright picture.
  final List<double> box;
  final double score;

  /// 128 numbers, each times 127 as a signed byte, base64.
  final String e;
  String? person;
  final List<String> not;
  final List<double> vector;

  Map<String, dynamic> toJson() => {
        'Photo': photo,
        'Box': box,
        'Score': score,
        'E': e,
        if (person != null) 'Person': person,
        if (not.isNotEmpty) 'Not': not,
      };
  factory FaceInfo.fromJson(String id, Map<String, dynamic> j) => FaceInfo(
        id: id,
        photo: j['Photo'] as String,
        box: [for (final v in j['Box'] as List) (v as num).toDouble()],
        score: (j['Score'] as num?)?.toDouble() ?? 0,
        e: j['E'] as String,
        person: j['Person'] as String?,
        not: [for (final v in j['Not'] as List? ?? const []) '$v'],
      );
}

class AnalyzedPhoto {
  AnalyzedPhoto(this.rev, {List<List<int>>? labels, List<String>? faces})
      : labels = labels ?? [],
        faces = faces ?? [];
  String? rev;

  /// [class id, percent].
  final List<List<int>> labels;
  final List<String> faces;

  Map<String, dynamic> toJson() => {'Rev': rev, 'Labels': labels, 'Faces': faces};
  factory AnalyzedPhoto.fromJson(Map<String, dynamic> j) => AnalyzedPhoto(
        j['Rev'] as String?,
        labels: [for (final l in j['Labels'] as List? ?? const []) [for (final v in l as List) (v as num).toInt()]],
        faces: [for (final f in j['Faces'] as List? ?? const []) '$f'],
      );
}

String _newId() {
  final r = Random.secure();
  return List.generate(12, (_) => '0123456789abcdef'[r.nextInt(16)]).join();
}

class PersonView {
  PersonView(this.person, this.cover, this.photoIds);
  final PersonInfo person;
  final FaceInfo cover;
  final List<String> photoIds;
}

/// People (faces grouped by who they are) and what's in each photo, worked out on the phone. Kept in people.json
/// (the same layout as the Windows app's). A new face joins the person whose average face is closest when the cosine
/// is at least [threshold], or starts a new person. People show once they have a name or 2 photos.
class PeopleStore extends ChangeNotifier {
  PeopleStore(this.words, {this.file});

  static const threshold = 0.40;
  static const minFacePixels = 32.0;
  static const minSearchPercent = 12;

  final VisionWords words;

  /// Null in tests: nothing is saved.
  final File? file;

  final photos = <String, AnalyzedPhoto>{};
  final faces = <String, FaceInfo>{};
  final people = <PersonInfo>[];
  final _sums = <String, Float64List>{};

  Future<void> load() async {
    final f = file;
    if (f == null || !await f.exists()) return;
    try {
      final j = jsonDecode(await f.readAsString()) as Map<String, dynamic>;
      for (final e in (j['Photos'] as Map<String, dynamic>? ?? const {}).entries) {
        photos[e.key] = AnalyzedPhoto.fromJson(e.value as Map<String, dynamic>);
      }
      for (final e in (j['Faces'] as Map<String, dynamic>? ?? const {}).entries) {
        faces[e.key] = FaceInfo.fromJson(e.key, e.value as Map<String, dynamic>);
      }
      for (final p in j['People'] as List? ?? const []) {
        people.add(PersonInfo.fromJson(p as Map<String, dynamic>));
      }
    } catch (_) {
      photos.clear();
      faces.clear();
      people.clear();
    }
    _rebuildSums();
  }

  String toJsonString() => jsonEncode({
        'Version': 1,
        'Photos': {for (final e in photos.entries) e.key: e.value.toJson()},
        'Faces': {for (final e in faces.entries) e.key: e.value.toJson()},
        'People': [for (final p in people) p.toJson()],
      });

  bool _dirty = false;

  Future<void> save() async {
    final f = file;
    if (f == null || !_dirty) return;
    _dirty = false;
    try {
      final tmp = File('${f.path}.tmp');
      await tmp.writeAsString(toJsonString());
      await tmp.rename(f.path);
    } catch (_) {
      _dirty = true;
    }
  }

  void _touch() {
    _dirty = true;
    _words.clear();
    notifyListeners();
  }

  final _words = <String, Set<String>>{};

  /// Whether search word [w] matches what's in the photo or who's in it.
  bool matches(String photoId, String w) {
    if (!photos.containsKey(photoId)) return false;
    final set = _words.putIfAbsent(photoId, () => searchWords(photoId));
    return wordMatches(set, w);
  }

  // ------------------------------------------------------------------ embeddings

  static String encode(List<double> v) => base64Encode(Int8List.fromList([for (final x in v) (x * 127).round().clamp(-127, 127)]).buffer.asUint8List());

  static List<double> decode(String e) {
    final raw = base64Decode(e);
    final b = Int8List.view(raw.buffer, raw.offsetInBytes, raw.length);
    final v = [for (final x in b) x / 127.0];
    var len = 0.0;
    for (final x in v) {
      len += x * x;
    }
    len = sqrt(len) + 1e-12;
    return [for (final x in v) x / len];
  }

  void _rebuildSums() {
    _sums.clear();
    for (final f in faces.values) {
      if (f.person != null) _add(f.person!, f.vector, 1);
    }
  }

  void _add(String person, List<double> v, int sign) {
    final s = _sums.putIfAbsent(person, () => Float64List(v.length));
    for (var i = 0; i < v.length; i++) {
      s[i] += sign * v[i];
    }
  }

  double _cosineTo(String person, List<double> v) {
    final s = _sums[person];
    if (s == null) return -1;
    var dot = 0.0, len = 0.0;
    for (var i = 0; i < v.length; i++) {
      dot += s[i] * v[i];
      len += s[i] * s[i];
    }
    return len <= 1e-12 ? -1 : dot / sqrt(len);
  }

  void _assign(FaceInfo f) {
    String? best;
    var bestCos = threshold;
    for (final p in people) {
      if (f.not.contains(p.id)) continue;
      final c = _cosineTo(p.id, f.vector);
      if (c >= bestCos) {
        bestCos = c;
        best = p.id;
      }
    }
    if (best == null) {
      final np = PersonInfo(cover: f.id);
      people.add(np);
      best = np.id;
    }
    _setPerson(f, best);
  }

  void _setPerson(FaceInfo f, String? person) {
    if (f.person != null) _add(f.person!, f.vector, -1);
    f.person = person;
    if (person != null) _add(person, f.vector, 1);
  }

  // ------------------------------------------------------------------ analysis results

  /// What a photo's analysis depends on: the model version, the file and your rotation.
  static String revOf(String modified, int rotation) => '${Vision.version}:$modified:$rotation';

  bool isAnalyzed(String photoId, String rev) => photos[photoId]?.rev == rev;

  void setResult(String photoId, String rev, VisionResult r) {
    _forget(photoId);
    final a = AnalyzedPhoto(rev);
    for (final (id, p) in r.labels) {
      a.labels.add([id, (p * 100).round()]);
    }
    for (final ff in r.faces) {
      if (min(ff.w, ff.h) < minFacePixels) continue;
      double c(double v) => (v.clamp(0.0, 1.0) * 10000).round() / 10000;
      final f = FaceInfo(
        photo: photoId,
        box: [c(ff.x / r.width), c(ff.y / r.height), c(ff.w / r.width), c(ff.h / r.height)],
        score: (ff.score * 1000).round() / 1000,
        e: encode(ff.embedding),
      );
      faces[f.id] = f;
      a.faces.add(f.id);
      _assign(f);
    }
    photos[photoId] = a;
    _touch();
  }

  void setNothing(String photoId, String rev) {
    _forget(photoId);
    photos[photoId] = AnalyzedPhoto(rev);
    _touch();
  }

  /// Drops what was found in photos that aren't in the library any more.
  void prune(bool Function(String id) exists) {
    final gone = photos.keys.where((id) => !exists(id)).toList();
    if (gone.isEmpty) return;
    for (final id in gone) {
      _forget(id);
    }
    _touch();
  }

  void _forget(String photoId) {
    final a = photos.remove(photoId);
    if (a == null) return;
    for (final fid in a.faces) {
      final f = faces.remove(fid);
      if (f?.person != null) _add(f!.person!, f.vector, -1);
    }
    _dropEmpty();
  }

  void _dropEmpty() {
    final used = {for (final f in faces.values) if (f.person != null) f.person!};
    people.removeWhere((p) => !used.contains(p.id) && !p.named);
    for (final p in people) {
      final c = p.cover == null ? null : faces[p.cover!];
      if (c == null || c.person != p.id) {
        final mine = faces.values.where((f) => f.person == p.id).toList()..sort((a, b) => (b.score * min(1, b.box[2] * 4)).compareTo(a.score * min(1, a.box[2] * 4)));
        p.cover = mine.isEmpty ? null : mine.first.id;
      }
    }
    _sums.removeWhere((k, _) => !used.contains(k));
  }

  // ------------------------------------------------------------------ reading

  /// Whole lower-case words for search: what's in the photo (when sure enough) and who's in it.
  Set<String> searchWords(String photoId) {
    final a = photos[photoId];
    if (a == null) return const {};
    final parts = <String>[];
    for (final l in a.labels) {
      if (l[1] < minSearchPercent) continue;
      parts.add(words.name(l[0]));
      parts.addAll(words.broader(l[0]));
    }
    for (final fid in a.faces) {
      final f = faces[fid];
      if (f?.person == null) continue;
      final p = person(f!.person!);
      if (p?.named == true) parts.add(p!.name!);
    }
    if (a.faces.isNotEmpty) parts.add('person people face');
    return {for (final w in parts.join(' ').toLowerCase().split(RegExp(r'[\s|,\-]+'))) if (w.isNotEmpty) w};
  }

  /// True when the search word is one of [words], allowing plurals ("dogs", "beaches", "puppies").
  static bool wordMatches(Set<String> words, String t) {
    if (words.contains(t)) return true;
    if (t.length > 3 && t.endsWith('ies') && words.contains('${t.substring(0, t.length - 3)}y')) return true;
    if (t.length > 3 && t.endsWith('es') && words.contains(t.substring(0, t.length - 2))) return true;
    if (t.length > 2 && t.endsWith('s') && words.contains(t.substring(0, t.length - 1))) return true;
    return false;
  }

  PersonInfo? person(String id) => people.where((p) => p.id == id).firstOrNull;
  PersonInfo? findByName(String name) => people.where((p) => p.named && p.name!.toLowerCase() == name.trim().toLowerCase()).firstOrNull;

  /// People to show: named, or in 2 photos or more; named first, then most photos.
  List<PersonView> list(bool Function(String id) shown, {bool includeHidden = false}) {
    final byPerson = <String, Set<String>>{};
    for (final f in faces.values) {
      if (f.person != null && shown(f.photo)) byPerson.putIfAbsent(f.person!, () => {}).add(f.photo);
    }
    final out = <PersonView>[];
    for (final p in people) {
      if (p.hidden && !includeHidden) continue;
      final ids = byPerson[p.id]?.toList() ?? const <String>[];
      if (ids.isEmpty || (ids.length < 2 && !p.named)) continue;
      var cover = p.cover == null ? null : faces[p.cover!];
      if (cover == null || !shown(cover.photo) || cover.person != p.id) {
        final mine = faces.values.where((f) => f.person == p.id && shown(f.photo)).toList()..sort((a, b) => b.score.compareTo(a.score));
        cover = mine.first;
      }
      out.add(PersonView(p, cover, ids));
    }
    out.sort((a, b) {
      if (a.person.named != b.person.named) return a.person.named ? -1 : 1;
      return b.photoIds.length.compareTo(a.photoIds.length);
    });
    return out;
  }

  List<String> photosOf(String personId) => {for (final f in faces.values) if (f.person == personId) f.photo}.toList();

  List<PersonInfo> peopleIn(String photoId) {
    final a = photos[photoId];
    if (a == null) return const [];
    final ids = {for (final fid in a.faces) if (faces[fid]?.person != null) faces[fid]!.person!};
    return [for (final id in ids) if (person(id) != null) person(id)!];
  }

  List<(int, int)> labelsOf(String photoId) => [for (final l in photos[photoId]?.labels ?? const <List<int>>[]) (l[0], l[1])];

  /// Broader words ("dog", "beach") found in at least 2 shown photos, most first: search suggestions.
  List<(String, int)> things(bool Function(String id) shown, {int max = 24}) {
    const skip = {'animal', 'nature', 'landscape', 'vehicle', 'pet', 'clothes', 'puppy', 'kitten', 'bunny', 'bug'};
    final counts = <String, int>{};
    for (final e in photos.entries) {
      if (e.value.labels.isEmpty || !shown(e.key)) continue;
      final ws = <String>{};
      for (final l in e.value.labels) {
        if (l[1] >= minSearchPercent) ws.addAll(words.broader(l[0]));
      }
      for (final w in ws) {
        counts[w] = (counts[w] ?? 0) + 1;
      }
    }
    final list = [for (final e in counts.entries) if (!skip.contains(e.key) && e.value >= 2) (e.key, e.value)]..sort((a, b) => b.$2.compareTo(a.$2));
    return list.take(max).toList();
  }

  // ------------------------------------------------------------------ changes you make

  /// Names a person; if someone else has that name already the two are merged. Returns the person kept.
  PersonInfo? rename(String personId, String name) {
    final p = person(personId);
    if (p == null) return null;
    final n = name.trim();
    final other = n.isEmpty ? null : people.where((x) => x.id != personId && x.named && x.name!.toLowerCase() == n.toLowerCase()).firstOrNull;
    if (other != null) {
      _merge(p.id, other.id);
      _touch();
      return other;
    }
    p.name = n.isEmpty ? null : n;
    _touch();
    return p;
  }

  void merge(String from, String into) {
    _merge(from, into);
    _touch();
  }

  void _merge(String from, String into) {
    if (from == into) return;
    for (final f in faces.values.where((f) => f.person == from).toList()) {
      _setPerson(f, into);
    }
    final target = person(into), gone = person(from);
    if (target != null && gone != null && !target.named) target.name = gone.name;
    people.removeWhere((p) => p.id == from);
    for (final f in faces.values) {
      if (f.not.remove(from) && !f.not.contains(into)) f.not.add(into);
    }
    _dropEmpty();
  }

  void setHidden(String personId, bool hidden) {
    person(personId)?.hidden = hidden;
    _touch();
  }

  void setCover(String personId, String photoId) {
    final f = faces.values.where((f) => f.person == personId && f.photo == photoId).firstOrNull;
    if (f == null) return;
    person(personId)?.cover = f.id;
    _touch();
  }

  /// "Not them": the person's faces in these photos are taken away and regrouped, never back to this person.
  void notThisPerson(String personId, Iterable<String> photoIds) {
    final set = photoIds.toSet();
    for (final f in faces.values.where((f) => f.person == personId && set.contains(f.photo)).toList()) {
      f.not.add(personId);
      _setPerson(f, null);
      _assign(f);
    }
    _dropEmpty();
    _touch();
  }

  /// Forgets every face and person (what's in each photo is kept; faces are found again).
  void forgetPeople() {
    people.clear();
    faces.clear();
    _sums.clear();
    for (final a in photos.values) {
      if (a.faces.isNotEmpty) {
        a.faces.clear();
        a.rev = null;
      }
    }
    _touch();
  }
}
