import 'package:flutter/material.dart';

import '../actions.dart';
import '../app.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'home.dart';
import 'people.dart';
import 'places.dart';
import 'viewer.dart';

/// Favorites, Recently added, Folders, Trash and your own albums.
class AlbumsTab extends StatelessWidget {
  const AlbumsTab({super.key, required this.nav});
  final Widget nav;

  static Future<void> newAlbum(BuildContext context) async {
    final lib = AppScope.read(context).library;
    final name = await askText(context, 'New album', hint: 'Album name', action: 'Create');
    if (name == null) return;
    final a = lib.createAlbum(name);
    if (context.mounted) toast(context, 'Created ${a.name}. Select photos and choose Album to add them.');
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final lib = app.library;
    return ListenableBuilder(
      listenable: lib,
      builder: (context, _) => Scaffold(
        appBar: AppBar(title: const Text('Albums'), actions: [
          IconButton(tooltip: 'New album', icon: const Icon(Icons.add), onPressed: () => newAlbum(context)),
          MainMenu(items: lib.photos),
        ]),
        body: LayoutBuilder(builder: (context, c) {
          final cols = c.maxWidth > 700 ? 4 : 2;
          return CustomScrollView(slivers: [
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(12, 4, 12, 0),
              sliver: SliverGrid.count(
                crossAxisCount: cols,
                mainAxisSpacing: 10,
                crossAxisSpacing: 10,
                childAspectRatio: 2.7,
                children: [
                  _QuickTile(icon: Icons.favorite_border, label: 'Favorites', count: lib.favorites.length, onTap: () => CollectionScreen.open(context, CollectionKind.favorites)),
                  _QuickTile(icon: Icons.schedule, label: 'Recently added', count: lib.recent.length, onTap: () => CollectionScreen.open(context, CollectionKind.recent)),
                  if (app.people != null)
                    ListenableBuilder(
                      listenable: app.people!,
                      builder: (context, _) => _QuickTile(
                          icon: Icons.face_outlined,
                          label: 'People',
                          count: app.people!.list((id) => lib.byIdFast(id) != null && !lib.isTrashed(lib.byIdFast(id)!)).length,
                          onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const PeopleScreen()))),
                    ),
                  _QuickTile(
                      icon: Icons.place_outlined,
                      label: 'Places',
                      count: lib.placed.length,
                      onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const PlacesScreen()))),
                  _QuickTile(icon: Icons.event_outlined, label: 'On this day', count: lib.onThisDay.length, onTap: () => CollectionScreen.open(context, CollectionKind.onThisDay)),
                  _QuickTile(
                      icon: Icons.folder_outlined,
                      label: 'Folders',
                      count: lib.folders.length,
                      onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const FoldersScreen()))),
                  _QuickTile(icon: Icons.delete_outline, label: 'Trash', count: lib.trash.length, onTap: () => CollectionScreen.open(context, CollectionKind.trash)),
                ],
              ),
            ),
            SliverToBoxAdapter(child: SectionTitle('Your albums (${lib.albums.length})')),
            if (lib.albums.isEmpty)
              SliverToBoxAdapter(
                child: EmptyState(
                  icon: Icons.photo_album_outlined,
                  title: 'No albums yet',
                  body: 'A photo can be in any number of albums. Create one, then select photos and choose Album.',
                  action: FilledButton.icon(onPressed: () => newAlbum(context), icon: const Icon(Icons.add), label: const Text('New album')),
                ),
              )
            else
              SliverPadding(
                padding: const EdgeInsets.fromLTRB(12, 0, 12, 24),
                sliver: SliverGrid.count(
                  crossAxisCount: cols,
                  mainAxisSpacing: 14,
                  crossAxisSpacing: 12,
                  childAspectRatio: 0.82,
                  children: [
                    for (final a in lib.albums)
                      AlbumCard(
                        name: a.name,
                        count: lib.inAlbum(a).length,
                        cover: lib.coverOf(a),
                        onTap: () => CollectionScreen.open(context, CollectionKind.album, album: a),
                        onLongPress: () => albumMenu(context, a),
                      ),
                  ],
                ),
              ),
            SliverToBoxAdapter(child: SizedBox(height: 16 + MediaQuery.of(context).padding.bottom)),
          ]);
        }),
        bottomNavigationBar: nav,
      ),
    );
  }

  static Future<void> albumMenu(BuildContext context, Album a, {bool popAfterDelete = false}) async {
    final lib = AppScope.read(context).library;
    final v = await showModalBottomSheet<String>(
      context: context,
      builder: (ctx) => SafeArea(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          ListTile(leading: const Icon(Icons.edit_outlined), title: const Text('Rename'), onTap: () => Navigator.pop(ctx, 'rename')),
          ListTile(leading: const Icon(Icons.slideshow_outlined), title: const Text('Slideshow'), onTap: () => Navigator.pop(ctx, 'show')),
          ListTile(
            leading: Icon(Icons.delete_outline, color: Palette.of(ctx).danger),
            title: const Text('Delete album'),
            subtitle: const Text('The photos stay in your library'),
            onTap: () => Navigator.pop(ctx, 'delete'),
          ),
        ]),
      ),
    );
    if (!context.mounted) return;
    switch (v) {
      case 'rename':
        final name = await askText(context, 'Rename album', initial: a.name, action: 'Rename');
        if (name != null) lib.renameAlbum(a, name);
      case 'show':
        await Actions2.slideshow(context, lib.inAlbum(a));
      case 'delete':
        if (await confirm(context, 'Delete "${a.name}"?', 'Only the album is deleted. Its photos and videos stay in your library.', action: 'Delete', danger: true)) {
          lib.deleteAlbum(a);
          if (popAfterDelete && context.mounted) Navigator.of(context).pop();
        }
    }
  }
}

class _QuickTile extends StatelessWidget {
  const _QuickTile({required this.icon, required this.label, required this.count, required this.onTap});
  final IconData icon;
  final String label;
  final int count;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Material(
      color: p.card,
      borderRadius: BorderRadius.circular(14),
      child: InkWell(
        borderRadius: BorderRadius.circular(14),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 14),
          child: Row(children: [
            Icon(icon, color: p.accent, size: 22),
            const SizedBox(width: 12),
            Expanded(child: Text(label, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontWeight: FontWeight.w600, color: p.text))),
            Text('$count', style: TextStyle(color: p.text3)),
          ]),
        ),
      ),
    );
  }
}

/// An album: its cover, name and how many items.
class AlbumCard extends StatelessWidget {
  const AlbumCard({super.key, required this.name, required this.count, required this.cover, required this.onTap, this.onLongPress, this.icon});
  final String name;
  final int count;
  final Item? cover;
  final VoidCallback onTap;
  final VoidCallback? onLongPress;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    return InkWell(
      borderRadius: BorderRadius.circular(14),
      onTap: onTap,
      onLongPress: onLongPress,
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Expanded(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(14),
            child: Container(
              color: p.tile,
              width: double.infinity,
              child: cover == null
                  ? Icon(icon ?? Icons.photo_album_outlined, size: 40, color: p.text3)
                  : RotatedBox(
                      quarterTurns: (cover!.rotation ~/ 90) % 4,
                      child: Image(image: app.thumbnail(cover!, 384), fit: BoxFit.cover, errorBuilder: (_, __, ___) => Icon(Icons.image_outlined, color: p.text3)),
                    ),
            ),
          ),
        ),
        const SizedBox(height: 8),
        Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontWeight: FontWeight.w600, color: p.text)),
        Text('$count ${count == 1 ? 'item' : 'items'}', style: TextStyle(fontSize: 12.5, color: p.text3)),
      ]),
    );
  }
}

/// The phone's folders (Camera, Screenshots, Downloads…).
class FoldersScreen extends StatelessWidget {
  const FoldersScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final lib = AppScope.of(context).library;
    return ListenableBuilder(
      listenable: lib,
      builder: (context, _) {
        final folders = lib.folders;
        return Scaffold(
          appBar: AppBar(title: const Text('Folders')),
          body: folders.isEmpty
              ? const EmptyState(icon: Icons.folder_outlined, title: 'No folders', body: 'Folders with photos or videos show up here.')
              : LayoutBuilder(
                  builder: (context, c) => GridView.count(
                    padding: const EdgeInsets.all(12),
                    crossAxisCount: c.maxWidth > 700 ? 4 : 2,
                    mainAxisSpacing: 14,
                    crossAxisSpacing: 12,
                    childAspectRatio: 0.82,
                    children: [
                      for (final (id, name, items) in folders)
                        AlbumCard(
                          name: name,
                          count: items.length,
                          cover: items.first,
                          icon: Icons.folder_outlined,
                          onTap: () => CollectionScreen.open(context, CollectionKind.folder, folderId: id, title: name),
                        ),
                    ],
                  ),
                ),
        );
      },
    );
  }
}

enum CollectionKind { favorites, recent, album, folder, trash, place, onThisDay, person }

/// One collection: Favorites, Recently added, an album, a folder or Trash.
class CollectionScreen extends StatefulWidget {
  const CollectionScreen({super.key, required this.kind, this.album, this.folderId, this.title, this.items, this.personId});
  final CollectionKind kind;

  /// For a person in People.
  final String? personId;
  final Album? album;
  final String? folderId;
  final String? title;

  /// For a place on the map.
  final List<Item>? items;

  static Future<void> open(BuildContext context, CollectionKind kind, {Album? album, String? folderId, String? title, List<Item>? items, String? personId}) =>
      Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => CollectionScreen(kind: kind, album: album, folderId: folderId, title: title, items: items, personId: personId)));

  @override
  State<CollectionScreen> createState() => _CollectionScreenState();
}

class _CollectionScreenState extends State<CollectionScreen> {
  final _sel = Selection();
  late String? _personId = widget.personId;

  @override
  void dispose() {
    _sel.dispose();
    super.dispose();
  }

  List<Item> _items() {
    final lib = AppScope.read(context).library;
    return switch (widget.kind) {
      CollectionKind.favorites => lib.favorites,
      CollectionKind.recent => lib.recent,
      CollectionKind.album => lib.inAlbum(widget.album!),
      CollectionKind.folder => lib.inFolder(widget.folderId!),
      CollectionKind.trash => lib.trash,
      CollectionKind.place => (widget.items ?? const <Item>[]).where((i) => !lib.isTrashed(i) && lib.all.contains(i)).toList(),
      CollectionKind.onThisDay => lib.onThisDay,
      CollectionKind.person => _personItems(),
    };
  }

  List<Item> _personItems() {
    final app = AppScope.read(context);
    final ids = app.people?.photosOf(_personId ?? '') ?? const <String>[];
    final out = [for (final id in ids) app.library.byIdFast(id)].whereType<Item>().where((i) => !app.library.isTrashed(i)).toList();
    out.sort((a, b) => b.taken.compareTo(a.taken));
    return out;
  }

  String get _title => switch (widget.kind) {
        CollectionKind.favorites => 'Favorites',
        CollectionKind.recent => 'Recently added',
        CollectionKind.album => widget.album!.name,
        CollectionKind.folder => widget.title ?? 'Folder',
        CollectionKind.trash => 'Trash',
        CollectionKind.place => widget.title ?? 'Place',
        CollectionKind.onThisDay => 'On this day',
        CollectionKind.person => AppScope.read(context).people?.person(_personId ?? '')?.name ?? 'Who\'s this?',
      };

  Widget get _empty => switch (widget.kind) {
        CollectionKind.favorites => const EmptyState(icon: Icons.favorite_border, title: 'No favorites yet', body: 'Tap the heart on a photo or video to add it here.'),
        CollectionKind.recent => const EmptyState(icon: Icons.schedule, title: 'Nothing new', body: 'Photos and videos added to your phone in the last 30 days show up here.'),
        CollectionKind.album => const EmptyState(icon: Icons.photo_album_outlined, title: 'This album is empty', body: 'Select photos in Photos or Videos and choose Album to add them.'),
        CollectionKind.folder => const EmptyState(icon: Icons.folder_outlined, title: 'Nothing here', body: 'This folder has no photos or videos.'),
        CollectionKind.place => const EmptyState(icon: Icons.place_outlined, title: 'Nothing here now', body: 'These photos were deleted or moved to Trash.'),
        CollectionKind.person => const EmptyState(icon: Icons.face_outlined, title: 'Nothing here now', body: 'This person\'s photos were deleted or moved to Trash.'),
        CollectionKind.onThisDay => const EmptyState(icon: Icons.event_outlined, title: 'Nothing on this day', body: 'Photos and videos taken on today\'s date in earlier years show up here.'),
        CollectionKind.trash => const EmptyState(icon: Icons.delete_outline, title: 'Trash is empty', body: 'Deleted photos and videos wait here. Emptying Trash deletes them from your phone (to Android\'s own trash where it has one).'),
      };

  Future<void> _emptyTrash() async {
    final lib = AppScope.read(context).library;
    final n = lib.trash.length;
    if (n == 0) return;
    if (!await confirm(context, 'Empty Trash?', 'This deletes $n ${n == 1 ? 'item' : 'items'} from your phone. Android may ask you to allow it.', action: 'Empty Trash', danger: true)) return;
    final gone = await lib.emptyTrash();
    if (mounted) toast(context, 'Deleted $gone from your phone');
  }

  List<Widget> _trashActions(List<Item> all) {
    final lib = AppScope.read(context).library;
    final none = _sel.count == 0;
    return [
      ActionButton(
        icon: Icons.restore,
        label: 'Restore',
        onPressed: none
            ? null
            : () {
                lib.restoreFromTrash(_sel.of(all));
                _sel.clear();
              },
      ),
      ActionButton(
        icon: Icons.delete_forever_outlined,
        label: 'Delete now',
        color: Palette.of(context).danger,
        onPressed: none
            ? null
            : () async {
                final list = _sel.of(all);
                if (!await confirm(context, 'Delete ${list.length} from your phone?', 'Android may ask you to allow it.', action: 'Delete', danger: true)) return;
                await lib.deleteFromPhone(list);
                _sel.clear();
              },
      ),
    ];
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final lib = app.library;
    final p = Palette.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([lib, _sel, if (widget.kind == CollectionKind.person && app.people != null) app.people!]),
      builder: (context, _) {
        if (widget.kind == CollectionKind.album && !lib.albums.contains(widget.album)) return const Scaffold();
        final person = widget.kind == CollectionKind.person;
        final items = _items();
        final trash = widget.kind == CollectionKind.trash;
        final askName = person && app.people?.person(_personId ?? '')?.named != true && items.isNotEmpty;
        return PopScope(
          canPop: !_sel.active,
          onPopInvokedWithResult: (didPop, _) {
            if (!didPop) _sel.clear();
          },
          child: Scaffold(
            appBar: AppBar(
              title: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(_title, maxLines: 1, overflow: TextOverflow.ellipsis),
                Text('${items.length} ${items.length == 1 ? 'item' : 'items'}', style: TextStyle(fontSize: 12.5, color: p.text3, fontWeight: FontWeight.w400)),
              ]),
              actions: [
                IconButton(
                  tooltip: 'Select',
                  isSelected: _sel.active,
                  icon: const Icon(Icons.check_circle_outline),
                  selectedIcon: Icon(Icons.check_circle, color: p.accent),
                  onPressed: () => _sel.active ? _sel.clear() : _sel.mode = true,
                ),
                if (trash)
                  TextButton(onPressed: items.isEmpty ? null : _emptyTrash, child: const Text('Empty'))
                else
                  IconButton(tooltip: 'Slideshow', icon: const Icon(Icons.slideshow_outlined), onPressed: () => Actions2.slideshow(context, items)),
                if (widget.kind == CollectionKind.album)
                  IconButton(tooltip: 'Album', icon: const Icon(Icons.more_vert), onPressed: () => AlbumsTab.albumMenu(context, widget.album!, popAfterDelete: true)),
                if (person)
                  IconButton(
                    tooltip: 'Person',
                    icon: const Icon(Icons.more_vert),
                    onPressed: () => personMenu(context, _personId!,
                        onMoved: (id) => setState(() => _personId = id), selectedForCover: _sel.count == 1 ? _sel.of(items).first : null),
                  ),
              ],
            ),
            body: MediaGrid(
              header: askName
                  ? Padding(
                      padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
                      child: Row(children: [
                        Expanded(child: Text('Shutterbox thinks these are the same person.', style: TextStyle(color: p.text2))),
                        FilledButton(
                            onPressed: () async {
                              final kept = await renamePerson(context, _personId!);
                              if (kept != null && mounted) setState(() => _personId = kept);
                            },
                            child: const Text('Add a name')),
                      ]),
                    )
                  : null,
              headerHeight: askName ? 56 : 0,
              items: items,
              selection: _sel,
              groupBy: widget.kind == CollectionKind.recent || trash ? 'none' : (widget.kind == CollectionKind.onThisDay ? 'year' : null),
              onOpen: (i, all) => ViewerScreen.open(context, all, i, album: widget.album, inTrash: trash),
              empty: _empty,
            ),
            bottomNavigationBar: _sel.active
                ? SelectionBar(
                    selection: _sel,
                    all: items,
                    actions: trash
                        ? _trashActions(items)
                        : libraryActions(
                            context,
                            _sel,
                            items,
                            extra: widget.kind == CollectionKind.album
                                ? () {
                                    lib.removeFromAlbum(widget.album!, _sel.of(items));
                                    _sel.clear();
                                  }
                                : person
                                    ? () async {
                                        final people = app.people!;
                                        final list = _sel.of(items);
                                        people.notThisPerson(_personId!, list.map((i) => i.id));
                                        await people.save();
                                        _sel.clear();
                                        if (context.mounted) {
                                          toast(context, list.length == 1 ? 'Taken out. Shutterbox won\'t put that face with them again.' : 'Took ${list.length} photos out. Shutterbox won\'t put those faces with them again.');
                                        }
                                      }
                                    : null,
                            extraLabel: person ? 'Not them' : 'Remove from album',
                            extraIcon: person ? Icons.person_remove_outlined : Icons.remove_circle_outline,
                          ),
                  )
                : null,
          ),
        );
      },
    );
  }
}
