import 'dart:io';

import 'package:flutter/material.dart';
import 'package:open_filex/open_filex.dart';
import 'package:share_plus/share_plus.dart';

import 'app.dart';
import 'models.dart';
import 'screens/locked.dart';
import 'screens/slideshow.dart';
import 'theme.dart';
import 'vault/vault.dart';

/// The things you can do with photos and videos, shared by the grid, the selection bar and the viewer.
class Actions2 {
  static Future<void> share(BuildContext context, List<Item> items) async {
    final app = AppScope.read(context);
    final list = items.where((i) => !i.locked).toList();
    if (list.isEmpty) return toast(context, 'Locked album items can\'t be shared. Restore them first.');
    final files = <XFile>[];
    for (final i in list.take(100)) {
      final f = await app.source.file(i);
      if (f != null) files.add(XFile(f.path, name: i.name));
    }
    if (files.isEmpty) {
      if (context.mounted) toast(context, 'Couldn\'t find the files to share.');
      return;
    }
    AppScope.read(context).expectReturn();
    await SharePlus.instance.share(ShareParams(files: files));
  }

  /// Opens the photo or video in another app (an editor, another gallery…).
  static Future<void> openWith(BuildContext context, Item item) async {
    final app = AppScope.read(context);
    final f = await app.source.file(item);
    if (f == null) {
      if (context.mounted) toast(context, 'Couldn\'t find the file.');
      return;
    }
    AppScope.read(context).expectReturn();
    final r = await OpenFilex.open(f.path);
    if (r.type != ResultType.done && context.mounted) toast(context, 'No app could open it (${r.message}).');
  }

  static void trash(BuildContext context, List<Item> items) {
    final app = AppScope.read(context);
    final list = items.where((i) => !i.locked).toList();
    if (list.isEmpty) return;
    app.library.moveToTrash(list);
    final m = ScaffoldMessenger.maybeOf(context);
    m?.hideCurrentSnackBar();
    m?.showSnackBar(SnackBar(
      content: Text(list.length == 1 ? 'Moved to Trash' : '${list.length} moved to Trash'),
      action: SnackBarAction(label: 'Undo', onPressed: () => app.library.restoreFromTrash(list)),
    ));
  }

  static void favorite(BuildContext context, List<Item> items) {
    final lib = AppScope.read(context).library;
    final list = items.where((i) => !i.locked).toList();
    if (list.isEmpty) return;
    final make = !list.every(lib.isFavorite);
    lib.setFavorite(list, make);
    toast(context, make ? 'Added to Favorites' : 'Removed from Favorites');
  }

  static Future<void> addToAlbum(BuildContext context, List<Item> items) async {
    final app = AppScope.read(context);
    final lib = app.library;
    final list = items.where((i) => !i.locked).toList();
    if (list.isEmpty) return;
    final p = Palette.of(context);
    final choice = await showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      builder: (ctx) => SafeArea(
        child: ConstrainedBox(
          constraints: BoxConstraints(maxHeight: MediaQuery.of(ctx).size.height * 0.7),
          child: ListView(shrinkWrap: true, children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
              child: Text('Add ${list.length == 1 ? 'it' : '${list.length} items'} to an album', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: p.text)),
            ),
            ListTile(leading: Icon(Icons.add, color: p.accent), title: const Text('New album…'), onTap: () => Navigator.pop(ctx, '+new')),
            for (final a in lib.albums)
              ListTile(
                leading: const Icon(Icons.photo_album_outlined),
                title: Text(a.name),
                subtitle: Text('${lib.inAlbum(a).length} items'),
                onTap: () => Navigator.pop(ctx, a.id),
              ),
          ]),
        ),
      ),
    );
    if (choice == null || !context.mounted) return;
    if (choice == '+new') {
      final name = await askText(context, 'New album', hint: 'Album name', action: 'Create');
      if (name == null) return;
      final a = lib.createAlbum(name, list);
      if (context.mounted) toast(context, 'Added to ${a.name}');
      return;
    }
    final a = lib.albums.firstWhere((x) => x.id == choice);
    final n = lib.addToAlbum(a, list);
    toast(context, n == 0 ? 'Already in ${a.name}' : 'Added $n to ${a.name}');
  }

  static Future<void> slideshow(BuildContext context, List<Item> items) async {
    final photos = items.where((i) => !i.isVideo).toList()..sort((a, b) => a.taken.compareTo(b.taken));
    if (photos.isEmpty) return toast(context, 'There are no photos for a slideshow (videos are skipped).');
    await Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => SlideshowScreen(items: photos), fullscreenDialog: true));
  }

  /// Copy to Locked album or Move to Locked album, optionally into one of its albums.
  static Future<bool> lockMenu(BuildContext context, List<Item> items) async {
    final list = items.where((i) => !i.locked).toList();
    if (list.isEmpty) return false;
    final p = Palette.of(context);
    final choice = await showModalBottomSheet<String>(
      context: context,
      builder: (ctx) => SafeArea(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
            child: Text(list.length == 1 ? 'Lock this item' : 'Lock ${list.length} items', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: p.text)),
          ),
          ListTile(
            leading: Icon(Icons.lock_outline, color: p.accent),
            title: const Text('Copy to Locked album'),
            subtitle: const Text('Your originals stay where they are'),
            onTap: () => Navigator.pop(ctx, 'copy'),
          ),
          ListTile(
            leading: Icon(Icons.drive_file_move_outline, color: p.accent),
            title: const Text('Move to Locked album'),
            subtitle: const Text('Each item is encrypted and checked, then the original is deleted for good'),
            onTap: () => Navigator.pop(ctx, 'move'),
          ),
          const SizedBox(height: 8),
        ]),
      ),
    );
    if (choice == null || !context.mounted) return false;
    return lock(context, list, move: choice == 'move');
  }

  /// Puts items in the Locked album. Sets it up or unlocks it first when needed.
  static Future<bool> lock(BuildContext context, List<Item> items, {required bool move}) async {
    final app = AppScope.read(context);
    if (!await LockedGate.ensureUnlocked(context)) return false;
    if (!context.mounted) return false;
    final vault = app.vault;
    String? albumId;
    if (vault.albums.isNotEmpty) {
      final p = Palette.of(context);
      final pick = await showModalBottomSheet<String>(
        context: context,
        isScrollControlled: true,
        builder: (ctx) => SafeArea(
          child: ConstrainedBox(
            constraints: BoxConstraints(maxHeight: MediaQuery.of(ctx).size.height * 0.7),
            child: ListView(shrinkWrap: true, children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
                child: Text('Also put them in an album?', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: p.text)),
              ),
              ListTile(leading: const Icon(Icons.lock_outline), title: const Text('Just the Locked album'), onTap: () => Navigator.pop(ctx, '')),
              for (final a in vault.albums)
                ListTile(leading: const Icon(Icons.photo_album_outlined), title: Text(a.name), subtitle: Text('${vault.itemsIn(a).length} items'), onTap: () => Navigator.pop(ctx, a.id)),
            ]),
          ),
        ),
      );
      if (pick == null) return false;
      albumId = pick.isEmpty ? null : pick;
    }
    if (!context.mounted) return false;
    final sources = [
      for (final i in items)
        if (!i.locked) LockSource(item: i, file: await app.source.file(i) ?? File(''), thumbnail: () => app.source.thumbnailJpeg(i)),
    ];
    if (!context.mounted) return false;
    final r = await withProgress(context, move ? 'Moving to the Locked album' : 'Copying to the Locked album',
        (report) => vault.add(sources, albumId: albumId, verify: move, progress: report));
    var deleted = 0;
    if (move && r.secured.isNotEmpty) {
      deleted = await app.library.deleteFromPhone(r.secured, toTrash: false);
    }
    if (!context.mounted) return true;
    final parts = <String>[
      if (r.added > 0) '${move ? 'Moved' : 'Copied'} ${r.added} to the Locked album',
      if (r.skipped > 0) '${r.skipped} already there',
      if (move && deleted < r.secured.length) '${r.secured.length - deleted} originals weren\'t deleted (Android asked, or they\'re in use)',
      if (r.errors.isNotEmpty) '${r.errors.length} couldn\'t be locked: ${r.errors.first}',
    ];
    toast(context, parts.isEmpty ? 'Nothing to lock' : parts.join('. '));
    return true;
  }
}
