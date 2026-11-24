import 'dart:async';

import 'package:flutter/material.dart';

import '../actions.dart';
import '../app.dart';
import '../models.dart';
import '../native.dart';
import '../theme.dart';
import '../widgets.dart';
import 'albums.dart';
import 'import.dart';
import 'locked.dart';
import 'search.dart';
import 'settings_screen.dart';
import 'viewer.dart';

/// Photos, Videos, Albums and the Locked album, with the navigation bar at the bottom.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key, this.initialTab = 0});
  final int initialTab;

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  late int _tab = widget.initialTab;
  final _photos = Selection(), _videos = Selection(), _locked = Selection();
  AppState? _app;
  StreamSubscription<void>? _shared;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final app = AppScope.of(context);
    if (_app == app) return;
    _app?.showLocked.removeListener(_toLocked);
    _app?.showPhotos.removeListener(_toPhotos);
    _app = app;
    app.showLocked.addListener(_toLocked);
    app.showPhotos.addListener(_toPhotos);
    if (app.native && isAndroid) {
      _shared ??= Native.sharedArrived.listen((_) => _takeShared());
      WidgetsBinding.instance.addPostFrameCallback((_) => _takeShared());
    }
  }

  /// Something was shared to "Lock with Shutterbox".
  Future<void> _takeShared() async {
    final files = await Native.takeShared();
    if (files.isEmpty || !mounted) return;
    Navigator.of(context).popUntil((r) => r.isFirst);
    setState(() => _tab = 3);
    await LockedActions.lockShared(context, files);
  }

  void _toLocked() {
    if (!mounted) return;
    Navigator.of(context).popUntil((r) => r.isFirst);
    setState(() => _tab = 3);
  }

  void _toPhotos() {
    if (!mounted) return;
    Navigator.of(context).popUntil((r) => r.isFirst);
    _locked.clear();
    setState(() => _tab = 0);
  }

  @override
  void dispose() {
    _app?.showLocked.removeListener(_toLocked);
    _app?.showPhotos.removeListener(_toPhotos);
    _shared?.cancel();
    _photos.dispose();
    _videos.dispose();
    _locked.dispose();
    super.dispose();
  }

  Widget _nav(bool hideLocked) => NavigationBar(
        // with the Locked tab hidden, it shows nothing selected while you're in it
        selectedIndex: hideLocked && _tab == 3 ? 0 : _tab,
        onDestinationSelected: (i) {
          _photos.clear();
          _videos.clear();
          _locked.clear();
          setState(() => _tab = i);
        },
        destinations: [
          const NavigationDestination(icon: Icon(Icons.photo_outlined), selectedIcon: Icon(Icons.photo), label: 'Photos'),
          const NavigationDestination(icon: Icon(Icons.movie_outlined), selectedIcon: Icon(Icons.movie), label: 'Videos'),
          const NavigationDestination(icon: Icon(Icons.photo_album_outlined), selectedIcon: Icon(Icons.photo_album), label: 'Albums'),
          if (!hideLocked) const NavigationDestination(icon: Icon(Icons.lock_outline), selectedIcon: Icon(Icons.lock), label: 'Locked'),
        ],
      );

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return ListenableBuilder(listenable: app.settings, builder: (context, _) => _build(context, app.settings.hideLocked && app.vault.isSetUp));
  }

  Widget _build(BuildContext context, bool hideLocked) {
    final nav = _nav(hideLocked);
    return PopScope(
      canPop: _tab == 0 && !_photos.active,
      onPopInvokedWithResult: (didPop, _) {
        if (didPop) return;
        if (_photos.active || _videos.active || _locked.active) {
          _photos.clear();
          _videos.clear();
          _locked.clear();
        } else {
          setState(() => _tab = 0);
        }
      },
      child: IndexedStack(index: _tab, children: [
        TimelineTab(videos: false, selection: _photos, nav: nav),
        TimelineTab(videos: true, selection: _videos, nav: nav),
        AlbumsTab(nav: nav),
        LockedTab(selection: _locked, nav: nav),
      ]),
    );
  }
}

/// The Photos or the Videos timeline (videos have their own, apart from your photos).
class TimelineTab extends StatelessWidget {
  const TimelineTab({super.key, required this.videos, required this.selection, required this.nav});
  final bool videos;
  final Selection selection;
  final Widget nav;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final lib = app.library;
    final p = Palette.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([lib, selection, app.updater, app.settings]),
      builder: (context, _) {
        final items = videos ? lib.videos : lib.photos;
        return Scaffold(
          appBar: AppBar(
            title: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              GestureDetector(
                // the way into a hidden Locked tab
                onLongPress: videos ? null : () => app.showLocked.value++,
                child: Text(videos ? 'Videos' : 'Photos'),
              ),
              Text(
                lib.scanning && items.isEmpty ? 'Looking for your ${videos ? 'videos' : 'photos'}…' : '${items.length} ${videos ? (items.length == 1 ? 'video' : 'videos') : (items.length == 1 ? 'photo' : 'photos')}',
                style: TextStyle(fontSize: 12.5, color: p.text3, fontWeight: FontWeight.w400),
              ),
            ]),
            actions: [
              IconButton(
                tooltip: 'Search',
                icon: const Icon(Icons.search),
                onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const SearchScreen())),
              ),
              IconButton(
                tooltip: 'Select',
                isSelected: selection.active,
                icon: const Icon(Icons.check_circle_outline),
                selectedIcon: Icon(Icons.check_circle, color: p.accent),
                onPressed: () => selection.active ? selection.clear() : selection.mode = true,
              ),
              MainMenu(items: items),
            ],
          ),
          body: Column(children: [
            if (!videos && app.updater.available != null && !app.updater.dismissed) const UpdateBanner(),
            if (lib.scanning && items.isNotEmpty) const LinearProgressIndicator(minHeight: 2),
            Expanded(
              child: lib.access == false
                  ? EmptyState(
                      icon: Icons.photo_library_outlined,
                      title: 'Let Shutterbox see your photos',
                      body: 'Shutterbox shows the photos and videos already on your phone. It never moves or changes them, and nothing leaves your phone.',
                      action: Column(children: [
                        FilledButton(
                          onPressed: () async {
                            await lib.scan(ask: true);
                            if (lib.access == true) app.source.watch(lib.scan);
                          },
                          child: const Text('Allow access'),
                        ),
                        TextButton(onPressed: app.source.openSettings, child: const Text('Open Android settings')),
                      ]),
                    )
                  : !lib.loaded
                      ? const Center(child: CircularProgressIndicator())
                      : RefreshIndicator(
                          onRefresh: lib.scan,
                          child: MediaGrid(
                            items: items,
                            selection: selection,
                            onOpen: (i, all) => ViewerScreen.open(context, all, i),
                            empty: EmptyState(
                              icon: videos ? Icons.movie_outlined : Icons.photo_outlined,
                              title: videos ? 'No videos yet' : 'No photos yet',
                              body: videos
                                  ? 'Videos on your phone show up here, apart from your photos.'
                                  : 'Photos on your phone show up here, newest first. Import some from a camera card or a folder, or check which folders Shutterbox looks in (Settings → Folders).',
                              action: OutlinedButton.icon(
                                onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const ImportScreen())),
                                icon: const Icon(Icons.download_outlined),
                                label: const Text('Import'),
                              ),
                            ),
                          ),
                        ),
            ),
          ]),
          bottomNavigationBar: selection.active ? SelectionBar(selection: selection, all: items, actions: libraryActions(context, selection, items)) : nav,
        );
      },
    );
  }
}

/// The selection bar's buttons for photos and videos in your library.
List<Widget> libraryActions(BuildContext context, Selection selection, List<Item> all, {VoidCallback? extra, String? extraLabel, IconData? extraIcon}) {
  final lib = AppScope.read(context).library;
  List<Item> sel() => selection.of(all);
  final none = selection.count == 0;
  return [
    ActionButton(icon: Icons.share_outlined, label: 'Share', onPressed: none ? null : () => Actions2.share(context, sel())),
    ActionButton(icon: Icons.library_add_outlined, label: 'Album', onPressed: none ? null : () => Actions2.addToAlbum(context, sel())),
    ActionButton(
      icon: Icons.lock_outline,
      label: 'Lock',
      onPressed: none
          ? null
          : () async {
              if (await Actions2.lockMenu(context, sel())) selection.clear();
            },
    ),
    ActionButton(
      icon: Icons.delete_outline,
      label: 'Delete',
      onPressed: none
          ? null
          : () {
              Actions2.trash(context, sel());
              selection.clear();
            },
    ),
    PopupMenuButton<String>(
      tooltip: 'More',
      enabled: !none,
      onSelected: (v) {
        switch (v) {
          case 'fav':
            Actions2.favorite(context, sel());
          case 'right':
            lib.rotate(sel(), 90);
          case 'left':
            lib.rotate(sel(), -90);
          case 'show':
            Actions2.slideshow(context, sel());
          case 'extra':
            extra?.call();
        }
      },
      itemBuilder: (_) => [
        if (extra != null) PopupMenuItem(value: 'extra', child: ListTile(leading: Icon(extraIcon), title: Text(extraLabel ?? ''))),
        const PopupMenuItem(value: 'fav', child: ListTile(leading: Icon(Icons.favorite_border), title: Text('Favorite'))),
        const PopupMenuItem(value: 'right', child: ListTile(leading: Icon(Icons.rotate_right), title: Text('Rotate right'))),
        const PopupMenuItem(value: 'left', child: ListTile(leading: Icon(Icons.rotate_left), title: Text('Rotate left'))),
        const PopupMenuItem(value: 'show', child: ListTile(leading: Icon(Icons.slideshow_outlined), title: Text('Slideshow'))),
      ],
      child: ActionButton(icon: Icons.more_horiz, label: 'More', onPressed: null, passive: !none),
    ),
  ];
}

/// ⋮: grouping, slideshow, import, rescan and settings.
class MainMenu extends StatelessWidget {
  const MainMenu({super.key, required this.items});
  final List<Item> items;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final group = app.settings.groupBy;
    return PopupMenuButton<String>(
      tooltip: 'More',
      onSelected: (v) {
        switch (v) {
          case 'day' || 'month' || 'year':
            app.settings.update((s) => s.groupBy = v);
          case 'slideshow':
            Actions2.slideshow(context, items);
          case 'import':
            Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const ImportScreen()));
          case 'rescan':
            app.library.scan();
          case 'settings':
            Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const SettingsScreen()));
        }
      },
      itemBuilder: (_) => [
        for (final (v, label) in [('day', 'Group by day'), ('month', 'Group by month'), ('year', 'Group by year')])
          CheckedPopupMenuItem(value: v, checked: group == v, child: Text(label)),
        const PopupMenuDivider(),
        const PopupMenuItem(value: 'slideshow', child: ListTile(leading: Icon(Icons.slideshow_outlined), title: Text('Slideshow'))),
        const PopupMenuItem(value: 'import', child: ListTile(leading: Icon(Icons.download_outlined), title: Text('Import'))),
        const PopupMenuItem(value: 'rescan', child: ListTile(leading: Icon(Icons.refresh), title: Text('Look for new photos'))),
        const PopupMenuItem(value: 'settings', child: ListTile(leading: Icon(Icons.settings_outlined), title: Text('Settings'))),
      ],
    );
  }
}

/// "Shutterbox 0.4.0 is available" at the top of Photos.
class UpdateBanner extends StatelessWidget {
  const UpdateBanner({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final u = app.updater;
    final p = Palette.of(context);
    return Container(
      margin: const EdgeInsets.fromLTRB(12, 0, 12, 8),
      padding: const EdgeInsets.fromLTRB(14, 10, 6, 10),
      decoration: BoxDecoration(color: p.accent.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(12)),
      child: Row(children: [
        Icon(Icons.system_update_outlined, color: p.accent),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            u.progress != null ? 'Downloading Shutterbox ${u.available!.version}… ${(u.progress! * 100).round()}%' : 'Shutterbox ${u.available!.version} is available',
            style: TextStyle(color: p.text, fontWeight: FontWeight.w500),
          ),
        ),
        TextButton(onPressed: u.dismiss, child: const Text('Later')),
        FilledButton(
          onPressed: u.progress != null
              ? null
              : () async {
                  final e = await u.install();
                  if (e != null && context.mounted) toast(context, e);
                },
          child: const Text('Update'),
        ),
      ]),
    );
  }
}
