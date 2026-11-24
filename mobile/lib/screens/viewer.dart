import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:video_player/video_player.dart';

import '../actions.dart';
import '../app.dart';
import '../models.dart';
import '../theme.dart';
import '../vault/vault.dart';
import 'details.dart';
import 'locked.dart';

/// Full screen: swipe between photos and videos, pinch or double-tap to zoom, play videos.
class ViewerScreen extends StatefulWidget {
  const ViewerScreen({super.key, required this.items, required this.index, this.album, this.lockedAlbum, this.inTrash = false});
  final List<Item> items;
  final int index;
  final Album? album;
  final VaultAlbum? lockedAlbum;
  final bool inTrash;

  static Future<void> open(BuildContext context, List<Item> all, Item item, {Album? album, VaultAlbum? lockedAlbum, bool inTrash = false}) {
    final i = all.indexOf(item);
    return Navigator.of(context).push(PageRouteBuilder<void>(
      pageBuilder: (_, __, ___) => ViewerScreen(items: List.of(all), index: i < 0 ? 0 : i, album: album, lockedAlbum: lockedAlbum, inTrash: inTrash),
      transitionsBuilder: (_, a, __, child) => FadeTransition(opacity: a, child: child),
      transitionDuration: const Duration(milliseconds: 180),
    ));
  }

  @override
  State<ViewerScreen> createState() => _ViewerScreenState();
}

class _ViewerScreenState extends State<ViewerScreen> {
  late final List<Item> _items = widget.items;
  late final _pages = PageController(initialPage: widget.index);
  late int _index = widget.index;
  bool _chrome = true;
  bool _zoomed = false;

  Item get _item => _items[_index];

  @override
  void initState() {
    super.initState();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
  }

  @override
  void dispose() {
    _pages.dispose();
    super.dispose();
  }

  /// Takes the current item out of the viewer (deleted, locked, restored…).
  void _drop() {
    setState(() {
      _items.removeAt(_index);
      if (_index >= _items.length) _index = _items.length - 1;
    });
    if (_items.isEmpty) Navigator.of(context).pop();
  }

  Future<void> _more() async {
    final app = AppScope.read(context);
    final i = _item;
    final p = Palette.of(context);
    final v = await showModalBottomSheet<String>(
      context: context,
      builder: (ctx) => SafeArea(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          if (!i.locked) ...[
            ListTile(leading: const Icon(Icons.library_add_outlined), title: const Text('Add to album'), onTap: () => Navigator.pop(ctx, 'album')),
            if (widget.album != null) ...[
              ListTile(leading: const Icon(Icons.image_outlined), title: const Text('Use as album cover'), onTap: () => Navigator.pop(ctx, 'cover')),
              ListTile(leading: const Icon(Icons.remove_circle_outline), title: const Text('Remove from album'), onTap: () => Navigator.pop(ctx, 'unalbum')),
            ],
            ListTile(leading: const Icon(Icons.lock_outline), title: const Text('Copy to Locked album'), onTap: () => Navigator.pop(ctx, 'copy')),
            ListTile(leading: const Icon(Icons.drive_file_move_outline), title: const Text('Move to Locked album'), onTap: () => Navigator.pop(ctx, 'move')),
            ListTile(leading: const Icon(Icons.open_in_new), title: const Text('Edit or open in another app'), onTap: () => Navigator.pop(ctx, 'open')),
          ] else ...[
            ListTile(leading: const Icon(Icons.library_add_outlined), title: const Text('Add to a Locked album album'), onTap: () => Navigator.pop(ctx, 'lalbum')),
            if (widget.lockedAlbum != null)
              ListTile(leading: const Icon(Icons.remove_circle_outline), title: Text('Remove from ${widget.lockedAlbum!.name}'), onTap: () => Navigator.pop(ctx, 'lunalbum')),
            ListTile(leading: const Icon(Icons.lock_open), title: const Text('Restore to your gallery'), onTap: () => Navigator.pop(ctx, 'restore')),
          ],
          if (!i.isVideo) ListTile(leading: const Icon(Icons.slideshow_outlined), title: const Text('Slideshow'), onTap: () => Navigator.pop(ctx, 'show')),
          ListTile(leading: Icon(Icons.info_outline, color: p.text2), title: const Text('Details'), onTap: () => Navigator.pop(ctx, 'info')),
        ]),
      ),
    );
    if (!mounted || v == null) return;
    switch (v) {
      case 'album':
        await Actions2.addToAlbum(context, [i]);
      case 'cover':
        app.library.setCover(widget.album!, i);
        toast(context, 'Album cover set');
      case 'unalbum':
        app.library.removeFromAlbum(widget.album!, [i]);
        _drop();
      case 'copy':
        await Actions2.lock(context, [i], move: false);
      case 'move':
        if (await Actions2.lock(context, [i], move: true) && !app.library.all.contains(i)) _drop();
      case 'open':
        await Actions2.openWith(context, i);
      case 'lalbum':
        await LockedActions.addToAlbum(context, [i]);
      case 'lunalbum':
        await app.vault.removeFromAlbum(widget.lockedAlbum!, [i]);
        _drop();
      case 'restore':
        if (await LockedActions.restore(context, [i])) _drop();
      case 'show':
        final from = _items.where((x) => !x.isVideo).toList();
        await Actions2.slideshow(context, from);
      case 'info':
        await DetailsSheet.show(context, i);
    }
  }

  Future<void> _delete() async {
    final app = AppScope.read(context);
    final i = _item;
    if (i.locked) {
      if (await LockedActions.delete(context, [i])) _drop();
    } else if (widget.inTrash) {
      if (!await confirm(context, 'Delete from your phone?', 'Android may ask you to allow it.', action: 'Delete', danger: true)) return;
      if (await app.library.deleteFromPhone([i]) > 0) _drop();
    } else {
      Actions2.trash(context, [i]);
      _drop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([app.library, app.vault]),
      builder: (context, _) {
        if (_items.isEmpty) return const Scaffold(backgroundColor: Colors.black);
        if (_item.locked && (!app.vault.isUnlocked || (_item.protectedIn != null && !(app.vault.album(_item.protectedIn)?.isOpen ?? false)))) {
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (mounted) Navigator.of(context).pop();
          });
          return const Scaffold(backgroundColor: Colors.black);
        }
        final i = _item;
        return Scaffold(
          backgroundColor: Colors.black,
          body: Stack(children: [
            PageView.builder(
              controller: _pages,
              physics: _zoomed ? const NeverScrollableScrollPhysics() : const PageScrollPhysics(),
              itemCount: _items.length,
              onPageChanged: (n) => setState(() {
                _index = n;
                _zoomed = false;
                app.touch();
              }),
              itemBuilder: (context, n) {
                final it = _items[n];
                return it.isVideo
                    ? VideoPage(key: ValueKey(it.id), item: it, active: n == _index, chrome: _chrome, onTap: () => setState(() => _chrome = !_chrome))
                    : PhotoPage(
                        key: ValueKey('${it.id}:${it.rotation}'),
                        item: it,
                        onTap: () => setState(() => _chrome = !_chrome),
                        onZoom: (z) {
                          if (z != _zoomed) setState(() => _zoomed = z);
                        },
                      );
              },
            ),
            AnimatedOpacity(
              opacity: _chrome ? 1 : 0,
              duration: const Duration(milliseconds: 160),
              child: IgnorePointer(
                ignoring: !_chrome,
                child: Column(children: [
                  _topBar(i),
                  const Spacer(),
                  _bottomBar(i),
                ]),
              ),
            ),
          ]),
        );
      },
    );
  }

  Widget _topBar(Item i) => Container(
        decoration: const BoxDecoration(gradient: LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0xAA000000), Color(0x00000000)])),
        child: SafeArea(
          bottom: false,
          child: Row(children: [
            const BackButton(color: Colors.white),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(formatDay(i.taken), maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: Colors.white, fontSize: 16, fontWeight: FontWeight.w600)),
                Text('${formatTime(i.taken)}  ·  ${i.name}', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: Colors.white70, fontSize: 12.5)),
              ]),
            ),
            IconButton(tooltip: 'Details', color: Colors.white, icon: const Icon(Icons.info_outline), onPressed: () => DetailsSheet.show(context, i)),
            IconButton(tooltip: 'More', color: Colors.white, icon: const Icon(Icons.more_vert), onPressed: _more),
          ]),
        ),
      );

  Widget _bottomBar(Item i) {
    final app = AppScope.of(context);
    final fav = app.isFavorite(i);
    Widget b(IconData icon, String label, VoidCallback? onTap, {Color color = Colors.white}) => InkWell(
          borderRadius: BorderRadius.circular(12),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              Icon(icon, color: color, size: 24),
              const SizedBox(height: 3),
              Text(label, style: TextStyle(color: color, fontSize: 11.5)),
            ]),
          ),
        );
    final rotate = b(Icons.rotate_right, 'Rotate', () async {
      if (i.locked) {
        await app.vault.rotate(i, 90);
        setState(() {});
      } else {
        app.library.rotate([i], 90);
        setState(() {});
      }
    });
    return Container(
      decoration: const BoxDecoration(gradient: LinearGradient(begin: Alignment.bottomCenter, end: Alignment.topCenter, colors: [Color(0xAA000000), Color(0x00000000)])),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(8, 18, 8, 6),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.spaceAround,
            children: widget.inTrash
                ? [
                    b(Icons.restore, 'Restore', () {
                      app.library.restoreFromTrash([i]);
                      _drop();
                    }),
                    b(Icons.delete_forever_outlined, 'Delete now', _delete),
                  ]
                : i.locked
                    ? [
                        b(fav ? Icons.favorite : Icons.favorite_border, 'Favorite', () async {
                          await app.vault.setDetails(i, favorite: !i.favorite);
                          setState(() {});
                        }, color: fav ? const Color(0xFFFF7A7A) : Colors.white),
                        rotate,
                        b(Icons.share_outlined, 'Share', () => LockedActions.share(context, [i])),
                        b(Icons.lock_open, 'Restore', () async {
                          if (await LockedActions.restore(context, [i])) _drop();
                        }),
                        b(Icons.delete_outline, 'Delete', _delete),
                      ]
                    : [
                        b(fav ? Icons.favorite : Icons.favorite_border, 'Favorite', () => app.library.toggleFavorite(i), color: fav ? const Color(0xFFFF7A7A) : Colors.white),
                        rotate,
                        b(Icons.share_outlined, 'Share', () => Actions2.share(context, [i])),
                        b(Icons.lock_outline, 'Lock', () async {
                          if (await Actions2.lockMenu(context, [i]) && !app.library.all.contains(i)) _drop();
                        }),
                        b(Icons.delete_outline, 'Delete', _delete),
                      ],
          ),
        ),
      ),
    );
  }
}

/// A photo you can pinch or double-tap to zoom into.
class PhotoPage extends StatefulWidget {
  const PhotoPage({super.key, required this.item, required this.onTap, required this.onZoom});
  final Item item;
  final VoidCallback onTap;
  final void Function(bool zoomed) onZoom;

  @override
  State<PhotoPage> createState() => _PhotoPageState();
}

class _PhotoPageState extends State<PhotoPage> with SingleTickerProviderStateMixin {
  final _t = TransformationController();
  late final _anim = AnimationController(vsync: this, duration: const Duration(milliseconds: 220));
  Animation<Matrix4>? _zoomAnim;
  Offset _tap = Offset.zero;

  @override
  void initState() {
    super.initState();
    _anim.addListener(() {
      if (_zoomAnim != null) _t.value = _zoomAnim!.value;
    });
    _t.addListener(() => widget.onZoom(_t.value.getMaxScaleOnAxis() > 1.01));
  }

  @override
  void dispose() {
    _anim.dispose();
    _t.dispose();
    super.dispose();
  }

  void _doubleTap() {
    final zoomed = _t.value.getMaxScaleOnAxis() > 1.01;
    final Matrix4 end;
    if (zoomed) {
      end = Matrix4.identity();
    } else {
      const s = 2.6;
      end = Matrix4.identity()
        ..translateByDouble(-_tap.dx * (s - 1), -_tap.dy * (s - 1), 0, 1)
        ..scaleByDouble(s, s, 1, 1);
    }
    _zoomAnim = Matrix4Tween(begin: _t.value, end: end).animate(CurvedAnimation(parent: _anim, curve: Curves.easeOut));
    _anim.forward(from: 0);
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final i = widget.item;
    return GestureDetector(
      onTap: widget.onTap,
      onDoubleTapDown: (d) => _tap = d.localPosition,
      onDoubleTap: _doubleTap,
      child: InteractiveViewer(
        transformationController: _t,
        minScale: 1,
        maxScale: 8,
        child: SizedBox.expand(
          child: RotatedBox(
            quarterTurns: (i.rotation ~/ 90) % 4,
            child: Image(
              image: app.full(i),
              fit: BoxFit.contain,
              gaplessPlayback: true,
              filterQuality: FilterQuality.high,
              loadingBuilder: (context, child, progress) => progress == null
                  ? child
                  : Stack(fit: StackFit.expand, children: [
                      Image(image: app.thumbnail(i, 256), fit: BoxFit.contain, errorBuilder: (_, __, ___) => const SizedBox()),
                      child,
                    ]),
              errorBuilder: (_, e, __) => Center(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  const Icon(Icons.broken_image_outlined, color: Colors.white54, size: 48),
                  const SizedBox(height: 10),
                  Text('Couldn\'t show ${i.name}', style: const TextStyle(color: Colors.white70)),
                ]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// A video: play/pause, seek and mute. Locked album videos are decrypted to a temporary file while they play.
class VideoPage extends StatefulWidget {
  const VideoPage({super.key, required this.item, required this.active, required this.chrome, required this.onTap});
  final Item item;
  final bool active, chrome;
  final VoidCallback onTap;

  @override
  State<VideoPage> createState() => _VideoPageState();
}

class _VideoPageState extends State<VideoPage> {
  VideoPlayerController? _c;
  File? _temp;
  String? _error;
  double? _decrypting;
  bool _muted = false;
  bool _started = false;

  @override
  void didUpdateWidget(VideoPage old) {
    super.didUpdateWidget(old);
    if (!widget.active) _c?.pause();
    if (widget.active && !_started) _start();
  }

  @override
  void initState() {
    super.initState();
    if (widget.active) _start();
  }

  Future<void> _start() async {
    _started = true;
    final app = AppScope.read(context);
    try {
      File? f;
      if (widget.item.locked) {
        setState(() => _decrypting = 0);
        f = await app.vault.decryptForPlayback(widget.item, progress: (v) {
          if (mounted) setState(() => _decrypting = v);
        });
        _temp = f;
      } else {
        f = await app.source.file(widget.item);
      }
      if (f == null) throw const FileSystemException('the video file wasn\'t found');
      final c = VideoPlayerController.file(f);
      await c.initialize();
      if (!mounted) {
        await c.dispose();
        return;
      }
      c.addListener(() {
        if (mounted) setState(() {});
      });
      setState(() {
        _c = c;
        _decrypting = null;
      });
      if (widget.active) await c.play();
    } catch (e) {
      if (mounted) setState(() => _error = 'Couldn\'t play ${widget.item.name} ($e)');
    }
  }

  @override
  void dispose() {
    _c?.dispose();
    final t = _temp;
    if (t != null) {
      // the decrypted copy goes as soon as you move on
      Future<void>.delayed(const Duration(milliseconds: 300), () async {
        try {
          if (await t.exists()) await t.delete();
        } catch (_) {}
      });
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = _c;
    final i = widget.item;
    Widget body;
    if (_error != null) {
      body = Center(child: Padding(padding: const EdgeInsets.all(24), child: Text(_error!, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70))));
    } else if (c == null || !c.value.isInitialized) {
      body = Center(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          CircularProgressIndicator(value: _decrypting == null || _decrypting == 0 ? null : _decrypting),
          if (_decrypting != null) ...[const SizedBox(height: 12), const Text('Decrypting…', style: TextStyle(color: Colors.white70))],
        ]),
      );
    } else {
      body = Center(
        child: RotatedBox(
          quarterTurns: (i.rotation ~/ 90) % 4,
          child: AspectRatio(aspectRatio: c.value.aspectRatio == 0 ? 16 / 9 : c.value.aspectRatio, child: VideoPlayer(c)),
        ),
      );
    }
    final v = c?.value;
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: widget.onTap,
      child: Stack(children: [
        Positioned.fill(child: body),
        if (v != null && v.isInitialized && widget.chrome) ...[
          Center(
            child: IconButton.filled(
              iconSize: 44,
              style: IconButton.styleFrom(backgroundColor: Colors.black45, foregroundColor: Colors.white),
              onPressed: () => v.isPlaying ? c!.pause() : c!.play(),
              icon: Icon(v.isPlaying ? Icons.pause : Icons.play_arrow),
            ),
          ),
          Positioned(
            left: 0,
            right: 0,
            bottom: 96 + MediaQuery.of(context).padding.bottom,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: Row(children: [
                Text(formatDuration(v.position.inMilliseconds / 1000), style: const TextStyle(color: Colors.white, fontSize: 12)),
                Expanded(
                  child: Slider(
                    value: v.position.inMilliseconds.clamp(0, v.duration.inMilliseconds).toDouble(),
                    max: v.duration.inMilliseconds <= 0 ? 1 : v.duration.inMilliseconds.toDouble(),
                    activeColor: Palette.of(context).accent,
                    inactiveColor: Colors.white24,
                    onChanged: (x) => c!.seekTo(Duration(milliseconds: x.round())),
                  ),
                ),
                Text(formatDuration(v.duration.inMilliseconds / 1000), style: const TextStyle(color: Colors.white, fontSize: 12)),
                IconButton(
                  color: Colors.white,
                  tooltip: _muted ? 'Unmute' : 'Mute',
                  icon: Icon(_muted ? Icons.volume_off : Icons.volume_up),
                  onPressed: () {
                    setState(() => _muted = !_muted);
                    c!.setVolume(_muted ? 0 : 1);
                  },
                ),
              ]),
            ),
          ),
        ],
      ]),
    );
  }
}
