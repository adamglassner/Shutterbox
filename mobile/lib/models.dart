/// A photo or video: on the phone (from the gallery) or in the Locked album.
class Item {
  Item({
    required this.id,
    required this.name,
    required this.taken,
    this.vaultId,
    this.folder = '',
    this.folderId = '',
    DateTime? added,
    this.width = 0,
    this.height = 0,
    this.duration = 0,
    bool? isVideo,
    this.latitude,
    this.longitude,
    this.make,
    this.model,
    this.rotation = 0,
    this.size = 0,
    this.orientation = 1,
    this.takenSource = 'file',
    this.source,
  })  : added = added ?? taken,
        isVideo = isVideo ?? isVideoName(name);

  /// The gallery's id, or "locked-<id>" for the Locked album.
  final String id;

  /// The id in the Locked album; null for items on the phone.
  final String? vaultId;
  String name;
  String folder;
  String folderId;
  DateTime taken;
  DateTime added;
  int width, height;

  /// Seconds (videos).
  double duration;
  final bool isVideo;
  double? latitude, longitude;
  String? make, model;

  /// 0, 90, 180 or 270: Shutterbox's own rotation (the file isn't changed).
  int rotation;

  /// Bytes (0 when not known yet).
  int size;
  int orientation;
  String takenSource;

  /// The gallery's object for this item (an AssetEntity on the phone).
  final Object? source;

  bool get locked => vaultId != null;
  String get camera => [make, model].whereType<String>().where((s) => s.trim().isNotEmpty).join(' ').trim();

  static const videoExtensions = {'mp4', 'mov', 'm4v', 'avi', 'wmv', 'mkv', 'webm', '3gp', 'mts', 'm2ts', 'mpg', 'mpeg'};
  static const photoExtensions = {'jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp', 'heic', 'heif', 'avif', 'tif', 'tiff', 'dng', 'jxr', 'ico'};

  static String extension(String name) {
    final i = name.lastIndexOf('.');
    return i < 0 ? '' : name.substring(i + 1).toLowerCase();
  }

  static bool isVideoName(String name) => videoExtensions.contains(extension(name));
}

/// An album of your own. Photos can be in any number of albums.
class Album {
  Album({required this.id, required this.name, List<String>? itemIds, this.coverId, DateTime? created})
      : itemIds = itemIds ?? [],
        created = created ?? DateTime.now();
  final String id;
  String name;
  final List<String> itemIds;
  String? coverId;
  final DateTime created;

  Map<String, dynamic> toJson() => {'id': id, 'name': name, 'itemIds': itemIds, 'coverId': coverId, 'created': created.toIso8601String()};
  static Album fromJson(Map<String, dynamic> j) => Album(
        id: '${j['id']}',
        name: '${j['name'] ?? 'Album'}',
        itemIds: [for (final x in (j['itemIds'] as List? ?? const [])) '$x'],
        coverId: j['coverId'] as String?,
        created: DateTime.tryParse('${j['created']}'),
      );
}

/// What you've added to an item: kept in Shutterbox's library, never in the file.
class ItemMeta {
  bool favorite = false;
  String caption = '';
  List<String> tags = [];
  int rotation = 0;
  DateTime? added;
  DateTime? trashed;
  String? make, model;
  bool exifRead = false;

  bool get isEmpty =>
      !favorite && caption.isEmpty && tags.isEmpty && rotation == 0 && added == null && trashed == null && make == null && model == null && !exifRead;

  Map<String, dynamic> toJson() => {
        if (favorite) 'fav': true,
        if (caption.isNotEmpty) 'cap': caption,
        if (tags.isNotEmpty) 'tags': tags,
        if (rotation != 0) 'rot': rotation,
        if (added != null) 'added': added!.toIso8601String(),
        if (trashed != null) 'trashed': trashed!.toIso8601String(),
        if (make != null) 'make': make,
        if (model != null) 'model': model,
        if (exifRead) 'exif': true,
      };

  static ItemMeta fromJson(Map<String, dynamic> j) => ItemMeta()
    ..favorite = j['fav'] == true
    ..caption = '${j['cap'] ?? ''}'
    ..tags = [for (final t in (j['tags'] as List? ?? const [])) '$t']
    ..rotation = (j['rot'] as num?)?.toInt() ?? 0
    ..added = DateTime.tryParse('${j['added']}')
    ..trashed = DateTime.tryParse('${j['trashed']}')
    ..make = j['make'] as String?
    ..model = j['model'] as String?
    ..exifRead = j['exif'] == true;
}

/// A folder on the phone (a gallery bucket: Camera, Screenshots, Downloads…).
class DeviceFolder {
  DeviceFolder(this.id, this.name, this.count);
  final String id;
  final String name;
  final int count;
}

// ------------------------------------------------------------------ dates

const monthNames = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const dayNames = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

String shortMonth(int m) => monthNames[m - 1].substring(0, 3);

/// "2:08 PM"
String formatTime(DateTime d) {
  final h = d.hour % 12 == 0 ? 12 : d.hour % 12;
  return '$h:${d.minute.toString().padLeft(2, '0')} ${d.hour < 12 ? 'AM' : 'PM'}';
}

/// "Wednesday, Oct 14, 2026"
String formatDay(DateTime d) => '${dayNames[d.weekday - 1]}, ${shortMonth(d.month)} ${d.day}, ${d.year}';

/// "Oct 14, 2026 at 2:08 PM"
String formatDateTime(DateTime d) => '${shortMonth(d.month)} ${d.day}, ${d.year} at ${formatTime(d)}';

/// "1:05" or "1:02:03"
String formatDuration(double seconds) {
  final s = seconds.round();
  final h = s ~/ 3600, m = (s % 3600) ~/ 60, sec = s % 60;
  return h > 0 ? '$h:${m.toString().padLeft(2, '0')}:${sec.toString().padLeft(2, '0')}' : '$m:${sec.toString().padLeft(2, '0')}';
}

/// "3.4 MB"
String formatSize(int bytes) {
  if (bytes < 1024) return '$bytes bytes';
  const units = ['KB', 'MB', 'GB', 'TB'];
  double v = bytes / 1024;
  var u = 0;
  while (v >= 1024 && u < units.length - 1) {
    v /= 1024;
    u++;
  }
  return '${v >= 100 ? v.toStringAsFixed(0) : v.toStringAsFixed(1)} ${units[u]}';
}

/// The date in a file name, like the Windows app: IMG_20261014_091230.jpg, PXL_20261014_..., 2026-10-14 09.12.30.jpg,
/// WhatsApp's IMG-20261014-WA0001.jpg, Screenshot_2026-10-14-09-12-30.png. Null when there isn't one.
DateTime? dateFromName(String name) {
  final m = RegExp(r'(?<!\d)(20\d\d|19\d\d)[-_.]?([01]\d)[-_.]?([0-3]\d)(?:[-_ T.]?([0-2]\d)[-_.:]?([0-5]\d)[-_.:]?([0-5]\d))?').firstMatch(name);
  if (m == null) return null;
  final y = int.parse(m[1]!), mo = int.parse(m[2]!), d = int.parse(m[3]!);
  if (mo < 1 || mo > 12 || d < 1 || d > 31) return null;
  final h = int.tryParse(m[4] ?? '') ?? 0, mi = int.tryParse(m[5] ?? '') ?? 0, s = int.tryParse(m[6] ?? '') ?? 0;
  if (h > 23) return DateTime(y, mo, d);
  final dt = DateTime(y, mo, d, h, mi, s);
  if (dt.month != mo || dt.isAfter(DateTime.now().add(const Duration(days: 2)))) return null;
  return dt;
}
