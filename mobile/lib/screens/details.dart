import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../app.dart';
import '../library.dart';
import '../models.dart';
import '../theme.dart';
import '../vault/vault.dart';

/// Date, camera, exposure, size, location (with a map link), folder, caption, tags and albums.
class DetailsSheet extends StatefulWidget {
  const DetailsSheet({super.key, required this.item, this.scroll});
  final Item item;
  final ScrollController? scroll;

  static Future<void> show(BuildContext context, Item item) => showModalBottomSheet<void>(
        context: context,
        isScrollControlled: true,
        builder: (_) => DraggableScrollableSheet(
          expand: false,
          initialChildSize: 0.62,
          maxChildSize: 0.95,
          builder: (context, scroll) => DetailsSheet(item: item, scroll: scroll),
        ),
      );

  @override
  State<DetailsSheet> createState() => _DetailsSheetState();
}

class _DetailsSheetState extends State<DetailsSheet> {
  PhotoDetails? _details;
  int _size = 0;
  (double, double)? _location;
  late final TextEditingController _caption;
  final _tag = TextEditingController();
  late final Library _lib;
  late final Vault _vault;

  String _captionOf(Item i) => i.locked ? i.caption : _lib.metaOf(i).caption;
  List<String> _tagsOf(Item i) => i.locked ? i.tags : _lib.metaOf(i).tags;

  void _setCaption(Item i, String v) => i.locked ? _vault.setDetails(i, caption: v) : _lib.setCaption(i, v);

  void _addTag(Item i, String v) {
    final t = v.trim();
    if (t.isEmpty) return;
    if (!i.locked) return _lib.addTag(i, t);
    if (i.tags.any((x) => x.toLowerCase() == t.toLowerCase())) return;
    _vault.setDetails(i, tags: [...i.tags, t]);
  }

  void _removeTag(Item i, String t) => i.locked ? _vault.setDetails(i, tags: [...i.tags]..remove(t)) : _lib.removeTag(i, t);

  @override
  void initState() {
    super.initState();
    final app = AppScope.read(context);
    _lib = app.library;
    _vault = app.vault;
    final i = widget.item;
    _caption = TextEditingController(text: _captionOf(i));
    _size = i.size;
    if (i.latitude != null && i.longitude != null) _location = (i.latitude!, i.longitude!);
    _load();
  }

  Future<void> _load() async {
    final app = AppScope.read(context);
    final i = widget.item;
    if (i.locked) return;
    final d = await app.library.readDetails(i);
    final f = await app.source.file(i);
    final size = f == null ? 0 : await f.length();
    final loc = await app.source.location(i);
    if (!mounted) return;
    setState(() {
      _details = d;
      _size = size;
      _location = loc ?? _location;
    });
  }

  @override
  void dispose() {
    if (_caption.text.trim() != _captionOf(widget.item)) _setCaption(widget.item, _caption.text);
    _caption.dispose();
    _tag.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    final i = widget.item;
    final lib = app.library;
    final tags = _tagsOf(i);
    final camera = _details?.camera.isNotEmpty == true ? _details!.camera : i.camera;
    Widget row(IconData icon, String title, String? sub, {Widget? trailing}) => ListTile(
          leading: Icon(icon, color: p.text2),
          title: Text(title, style: TextStyle(color: p.text)),
          subtitle: sub == null || sub.isEmpty ? null : Text(sub, style: TextStyle(color: p.text3)),
          trailing: trailing,
          dense: false,
        );
    final albums = i.locked ? app.vault.albumsOf(i).map((a) => a.name).toList() : lib.albumsOf(i).map((a) => a.name).toList();
    return ListenableBuilder(
      listenable: Listenable.merge([lib, app.vault]),
      builder: (context, _) => ListView(controller: widget.scroll, padding: const EdgeInsets.only(bottom: 32), children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 0, 20, 4),
          child: Text('Details', style: TextStyle(fontSize: 20, fontWeight: FontWeight.w600, color: p.text)),
        ),
        row(Icons.calendar_today_outlined, formatDateTime(i.taken),
            i.takenSource == 'name' ? 'Date from the file name' : (i.takenSource == 'exif' ? 'Date the photo was taken' : null)),
        row(i.isVideo ? Icons.movie_outlined : Icons.image_outlined, i.name, [
          if (i.width > 0) '${i.width} × ${i.height}',
          if (i.isVideo && i.duration > 0) formatDuration(i.duration),
          if (_size > 0) formatSize(_size),
        ].join('  ·  ')),
        if (camera.isNotEmpty) row(Icons.camera_alt_outlined, camera, [_details?.lens, _details?.settings].whereType<String>().where((s) => s.isNotEmpty).join('\n')),
        if (_location != null)
          row(Icons.place_outlined, '${_location!.$1.toStringAsFixed(5)}, ${_location!.$2.toStringAsFixed(5)}', 'Location',
              trailing: TextButton(
                onPressed: () => launchUrl(Uri.parse('https://www.google.com/maps/search/?api=1&query=${_location!.$1},${_location!.$2}'), mode: LaunchMode.externalApplication),
                child: const Text('Map'),
              )),
        row(i.locked ? Icons.lock_outline : Icons.folder_outlined, i.locked ? 'Locked album' : i.folder, i.locked ? 'Encrypted' : 'Folder'),
        if (i.rotation != 0) row(Icons.rotate_right, 'Rotated ${i.rotation}° in Shutterbox', 'The file isn\'t changed'),
        if (albums.isNotEmpty)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 0),
            child: Wrap(spacing: 8, runSpacing: 8, children: [
              for (final a in albums) Chip(avatar: Icon(Icons.photo_album_outlined, size: 16, color: p.accent), label: Text(a)),
            ]),
          ),
        ...[
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
            child: TextField(
              controller: _caption,
              minLines: 1,
              maxLines: 4,
              textCapitalization: TextCapitalization.sentences,
              decoration: const InputDecoration(labelText: 'Caption', hintText: 'Add a caption'),
              onSubmitted: (v) => _setCaption(i, v),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 14, 16, 0),
            child: Wrap(spacing: 8, runSpacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
              for (final t in tags) InputChip(label: Text(t), onDeleted: () => _removeTag(i, t)),
              SizedBox(
                width: 160,
                child: TextField(
                  controller: _tag,
                  decoration: const InputDecoration(hintText: 'Add a tag', isDense: true, prefixIcon: Icon(Icons.sell_outlined, size: 18)),
                  onSubmitted: (v) {
                    _addTag(i, v);
                    _tag.clear();
                  },
                ),
              ),
            ]),
          ),
        ],
      ]),
    );
  }
}
