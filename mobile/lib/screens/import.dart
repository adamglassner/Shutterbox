import 'dart:io';

import 'package:exif/exif.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';

import '../app.dart';
import '../files_access.dart';
import '../models.dart';
import '../theme.dart';

/// Import from a camera card, a USB drive, a phone or any folder: copies into date folders
/// (DCIM/Shutterbox/2026/2026-10-14), skips what you already have, and can clear the card afterwards.
class ImportScreen extends StatefulWidget {
  const ImportScreen({super.key});

  @override
  State<ImportScreen> createState() => _ImportScreenState();
}

class _ImportScreenState extends State<ImportScreen> {
  bool _deleteAfter = false;
  String? _summary;

  static bool _isMedia(String path) {
    final e = Item.extension(path);
    return Item.videoExtensions.contains(e) || Item.photoExtensions.contains(e);
  }

  Future<void> _pickFiles() async {
    final r = await FilePicker.platform.pickFiles(type: FileType.media, allowMultiple: true);
    if (r == null || !mounted) return;
    final files = [for (final f in r.files) if (f.path != null) File(f.path!)];
    await _import(files, deleteAfter: false, temporaryCopies: true);
  }

  Future<void> _pickFolder() async {
    if (!await FilesAccess.ensure(context, 'To import a whole folder (a camera card, a USB drive, a folder from your computer), Shutterbox needs to read it.')) return;
    final dir = await FilePicker.platform.getDirectoryPath(dialogTitle: 'Choose the folder to import');
    if (dir == null || !mounted) return;
    final files = <File>[];
    try {
      await for (final e in Directory(dir).list(recursive: true, followLinks: false)) {
        if (e is File && _isMedia(e.path) && !e.path.split('/').any((s) => s.startsWith('.'))) files.add(e);
      }
    } catch (e) {
      if (mounted) toast(context, 'Couldn\'t read that folder ($e).');
      return;
    }
    if (!mounted) return;
    if (files.isEmpty) return toast(context, 'There are no photos or videos in that folder.');
    await _import(files, deleteAfter: _deleteAfter, temporaryCopies: false);
  }

  /// When it was taken: the file name, then EXIF, then the file's date.
  static Future<DateTime> _dateOf(File f) async {
    final name = f.uri.pathSegments.last;
    final fromName = dateFromName(name);
    if (fromName != null) return fromName;
    if (!Item.isVideoName(name)) {
      try {
        final raf = await f.open();
        final head = await raf.read(256 * 1024);
        await raf.close();
        final tags = await readExifFromBytes(head);
        final t = (tags['EXIF DateTimeOriginal'] ?? tags['Image DateTime'])?.printable;
        final m = RegExp(r'(\d{4}):(\d\d):(\d\d) (\d\d):(\d\d):(\d\d)').firstMatch(t ?? '');
        if (m != null) {
          return DateTime(int.parse(m[1]!), int.parse(m[2]!), int.parse(m[3]!), int.parse(m[4]!), int.parse(m[5]!), int.parse(m[6]!));
        }
      } catch (_) {}
    }
    return f.lastModified();
  }

  Future<void> _import(List<File> files, {required bool deleteAfter, required bool temporaryCopies}) async {
    final app = AppScope.read(context);
    final lib = app.library;
    // what you already have: same name and size
    final byName = <String, List<Item>>{};
    for (final i in lib.all) {
      byName.putIfAbsent(i.name.toLowerCase(), () => []).add(i);
    }
    var added = 0, skipped = 0, deleted = 0;
    final errors = <String>[];
    await withProgress(context, 'Importing', (report) async {
      var done = 0;
      for (final f in files) {
        report(done++, files.length);
        final name = f.uri.pathSegments.last;
        try {
          final size = await f.length();
          var have = false;
          for (final i in byName[name.toLowerCase()] ?? const <Item>[]) {
            final existing = await app.source.file(i);
            if (existing != null && await existing.length() == size) {
              have = true;
              break;
            }
          }
          if (have) {
            skipped++;
          } else {
            final d = await _dateOf(f);
            String two(int n) => n.toString().padLeft(2, '0');
            final folder = 'DCIM/Shutterbox/${d.year}/${d.year}-${two(d.month)}-${two(d.day)}';
            if (!await app.source.addToGallery(f, name, folder, taken: d)) throw const FileSystemException('Android didn\'t accept it');
            added++;
          }
          if (deleteAfter) {
            await f.delete();
            deleted++;
          }
        } catch (e) {
          errors.add('$name: $e');
        }
        if (temporaryCopies) {
          try {
            await f.delete();
          } catch (_) {}
        }
      }
      report(done, files.length);
    });
    await lib.scan();
    if (!mounted) return;
    setState(() => _summary = [
          'Imported $added ${added == 1 ? 'item' : 'items'}',
          if (skipped > 0) 'skipped $skipped you already have',
          if (deleted > 0) 'deleted $deleted from the folder',
          if (errors.isNotEmpty) '${errors.length} couldn\'t be imported (${errors.first})',
        ].join(', '));
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Import')),
      body: ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 32), children: [
        Text('Shutterbox copies photos and videos into date folders (DCIM/Shutterbox/2026/2026-10-14) and skips the ones you already have.',
            style: TextStyle(color: p.text2, height: 1.4)),
        const SizedBox(height: 18),
        _Option(
          icon: Icons.sd_card_outlined,
          title: 'Import a folder',
          body: 'A camera card or USB drive (plugged in with an adapter), or any folder, with everything in its subfolders.',
          onTap: _pickFolder,
          extra: CheckboxListTile(
            contentPadding: EdgeInsets.zero,
            value: _deleteAfter,
            onChanged: (v) => setState(() => _deleteAfter = v ?? false),
            title: const Text('Clear the card afterwards'),
            subtitle: const Text('Deletes each file from the folder once it\'s imported (or if you already have it)'),
          ),
        ),
        const SizedBox(height: 12),
        _Option(icon: Icons.add_photo_alternate_outlined, title: 'Choose photos and videos', body: 'Pick files from anywhere Android can reach: Downloads, Drive, another app…', onTap: _pickFiles),
        if (_summary != null) ...[
          const SizedBox(height: 18),
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(color: p.success.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(12)),
            child: Row(children: [
              Icon(Icons.check_circle_outline, color: p.success),
              const SizedBox(width: 12),
              Expanded(child: Text('$_summary.', style: TextStyle(color: p.text))),
            ]),
          ),
        ],
      ]),
    );
  }
}

class _Option extends StatelessWidget {
  const _Option({required this.icon, required this.title, required this.body, required this.onTap, this.extra});
  final IconData icon;
  final String title, body;
  final VoidCallback onTap;
  final Widget? extra;

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 10),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
            Icon(icon, color: p.accent, size: 26),
            const SizedBox(width: 12),
            Expanded(child: Text(title, style: TextStyle(fontSize: 16.5, fontWeight: FontWeight.w600, color: p.text))),
          ]),
          const SizedBox(height: 6),
          Text(body, style: TextStyle(color: p.text2, height: 1.35)),
          if (extra != null) extra!,
          Align(alignment: Alignment.centerRight, child: FilledButton.tonal(onPressed: onTap, child: const Text('Choose…'))),
        ]),
      ),
    );
  }
}
