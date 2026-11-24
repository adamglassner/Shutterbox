import 'dart:async';
import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:share_plus/share_plus.dart';

import '../actions.dart';
import '../app.dart';
import '../biometrics.dart';
import '../files_access.dart';
import '../models.dart';
import '../native.dart';
import '../settings.dart';
import '../theme.dart';
import '../vault/vault.dart';
import '../widgets.dart';
import 'camera_capture.dart';
import 'viewer.dart';

/// The Locked album: set it up, unlock it, and see what's in it (with its own albums, some with their own PIN).
class LockedTab extends StatefulWidget {
  const LockedTab({super.key, required this.selection, required this.nav});
  final Selection selection;
  final Widget nav;

  @override
  State<LockedTab> createState() => _LockedTabState();
}

class _LockedTabState extends State<LockedTab> {
  /// The album inside the Locked album being shown (null: All).
  String? _albumId;
  bool _searching = false;
  final _query = TextEditingController();
  bool _warningsSeen = false;

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  Future<void> _albumMenu(String v, VaultAlbum? album, List<Item> items) async {
    final app = AppScope.read(context);
    final vault = app.vault;
    switch (v) {
      case 'new':
        final a = await LockedActions.newAlbum(context);
        if (a != null) setState(() => _albumId = a.id);
      case 'rename':
        final name = await askText(context, 'Rename album', initial: album!.name, action: 'Rename');
        if (name != null) await vault.renameAlbum(album, name);
      case 'delete':
        final a1 = album!;
        if (!a1.isProtected || a1.isOpen) {
          final what = a1.isProtected ? 'Its items go back into the Locked album.' : 'Its items stay in the Locked album.';
          if (await confirm(context, 'Delete "${a1.name}"?', 'Only the album is deleted. $what', action: 'Delete', danger: true)) {
            await vault.deleteAlbum(a1);
            setState(() => _albumId = null);
          }
        }
      case 'protect':
        await LockedActions.protectAlbum(context, album!);
      case 'changepin':
        final a2 = album!;
        final r = await showDialog<(String, String)>(context: context, builder: (_) => SecretDialog(title: 'New PIN or password for "${a2.name}"', kind: a2.protection!.kind));
        if (r != null) {
          await vault.changeAlbumSecret(a2, r.$1, r.$2);
          if (mounted) toast(context, 'Changed');
        }
      case 'unprotect':
        final a3 = album!;
        if (await confirm(context, 'Take the PIN off "${a3.name}"?', 'Its items are re-encrypted with the Locked album\'s key and open whenever the Locked album does.', action: 'Take it off')) {
          if (!mounted) return;
          await withProgress(context, 'Moving items', (_) => vault.unprotectAlbum(a3));
        }
      case 'lockalbum':
        vault.lockAlbum(album!);
        app.forgetPictures();
      case 'show':
        await Actions2.slideshow(context, items);
      case 'guesses':
        await Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const WrongGuessesScreen()));
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final vault = app.vault;
    final p = Palette.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([vault, widget.selection, _query]),
      builder: (context, _) {
        if (!vault.isSetUp) {
          return Scaffold(appBar: AppBar(title: const Text('Locked album')), body: const LockedIntro(), bottomNavigationBar: widget.nav);
        }
        if (!vault.isUnlocked) {
          _warningsSeen = false;
          return Scaffold(appBar: AppBar(title: const Text('Locked album')), body: UnlockView(onUnlocked: () {}), bottomNavigationBar: widget.nav);
        }
        final album = vault.album(_albumId);
        if (_albumId != null && (album == null || album.deleted)) _albumId = null;
        final all = (album == null ? vault.items : vault.itemsIn(album)).toList()..sort((a, b) => b.taken.compareTo(a.taken));
        final items = _searching && _query.text.trim().isNotEmpty ? vault.search(_query.text, all) : all;
        final sel = widget.selection;
        final albumLocked = album != null && album.isProtected && !album.isOpen;
        final warnings = _warningsSeen ? const <FailedAttempt>[] : vault.lastUnlockWarnings;
        return Scaffold(
          appBar: AppBar(
            title: _searching
                ? TextField(
                    controller: _query,
                    autofocus: true,
                    decoration: const InputDecoration(hintText: 'Search the Locked album', filled: false, border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none),
                  )
                : Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    const Text('Locked album'),
                    Text(vault.isBusy ? 'Working…' : '${vault.count} ${vault.count == 1 ? 'item' : 'items'}, encrypted',
                        style: TextStyle(fontSize: 12.5, color: p.text3, fontWeight: FontWeight.w400)),
                  ]),
            actions: [
              IconButton(
                tooltip: _searching ? 'Close search' : 'Search',
                icon: Icon(_searching ? Icons.close : Icons.search),
                onPressed: () => setState(() {
                  _searching = !_searching;
                  _query.clear();
                }),
              ),
              if (!_searching)
                IconButton(
                  tooltip: 'Select',
                  isSelected: sel.active,
                  icon: const Icon(Icons.check_circle_outline),
                  selectedIcon: Icon(Icons.check_circle, color: p.accent),
                  onPressed: () => sel.active ? sel.clear() : sel.mode = true,
                ),
              IconButton(tooltip: 'Lock now and go to Photos', icon: const Icon(Icons.lock_outline), onPressed: app.panicLock),
              PopupMenuButton<String>(
                onSelected: (v) => _albumMenu(v, album, items),
                itemBuilder: (_) => [
                  const PopupMenuItem(value: 'new', child: ListTile(leading: Icon(Icons.create_new_folder_outlined), title: Text('New album'))),
                  if (album != null) ...[
                    if (!albumLocked) const PopupMenuItem(value: 'rename', child: ListTile(leading: Icon(Icons.edit_outlined), title: Text('Rename album'))),
                    if (!album.isProtected) const PopupMenuItem(value: 'protect', child: ListTile(leading: Icon(Icons.pin_outlined), title: Text('Give it its own PIN'))),
                    if (album.isProtected && album.isOpen) ...[
                      const PopupMenuItem(value: 'lockalbum', child: ListTile(leading: Icon(Icons.lock_outline), title: Text('Lock this album'))),
                      const PopupMenuItem(value: 'changepin', child: ListTile(leading: Icon(Icons.password), title: Text('Change its PIN'))),
                      const PopupMenuItem(value: 'unprotect', child: ListTile(leading: Icon(Icons.no_encryption_outlined), title: Text('Take its PIN off'))),
                    ],
                    if (!albumLocked) const PopupMenuItem(value: 'delete', child: ListTile(leading: Icon(Icons.delete_outline), title: Text('Delete album'))),
                  ],
                  const PopupMenuItem(value: 'show', child: ListTile(leading: Icon(Icons.slideshow_outlined), title: Text('Slideshow'))),
                  const PopupMenuItem(value: 'guesses', child: ListTile(leading: Icon(Icons.gpp_maybe_outlined), title: Text('Wrong guesses'))),
                ],
              ),
            ],
          ),
          body: Column(children: [
            if (warnings.isNotEmpty)
              _WarningBanner(
                attempts: warnings,
                onSee: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const WrongGuessesScreen())),
                onDismiss: () => setState(() => _warningsSeen = true),
              ),
            SizedBox(
              height: 48,
              child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), children: [
                _AlbumChip(label: 'All (${vault.count})', selected: album == null, onTap: () => setState(() => _albumId = null)),
                for (final a in vault.albums)
                  _AlbumChip(
                    label: a.isProtected && !a.isOpen ? a.name : '${a.name} (${vault.itemsIn(a).length})',
                    icon: a.isProtected ? (a.isOpen ? Icons.lock_open : Icons.lock) : null,
                    selected: a.id == _albumId,
                    onTap: () => setState(() => _albumId = a.id),
                  ),
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 6),
                  child: ActionChip(
                    avatar: Icon(Icons.add, size: 18, color: p.accent),
                    label: const Text('New album'),
                    onPressed: () async {
                      final a = await LockedActions.newAlbum(context);
                      if (a != null) setState(() => _albumId = a.id);
                    },
                  ),
                ),
              ]),
            ),
            if (vault.isBusy) const LinearProgressIndicator(minHeight: 2),
            Expanded(
              child: albumLocked
                  ? AlbumUnlockView(album: album)
                  : MediaGrid(
                      items: items,
                      selection: sel,
                      onOpen: (i, list) => ViewerScreen.open(context, list, i, lockedAlbum: album),
                      empty: EmptyState(
                        icon: _searching ? Icons.search_off : Icons.lock_outline,
                        title: _searching ? 'Nothing found' : (album == null ? 'Nothing locked yet' : '${album.name} is empty'),
                        body: _searching
                            ? 'Search looks at names, captions, tags, cameras, albums, months and years.'
                            : album == null
                                ? 'In Photos or Videos, select what you want to keep private and choose Lock. Or take photos straight into it with the camera button, or share them to "Lock with Shutterbox" from another app.'
                                : album.isProtected
                                    ? 'Select items in All and choose Album to move them in here. They\'re re-encrypted with this album\'s own key.'
                                    : 'Select items in All and choose Album to add them here.',
                      ),
                    ),
            ),
          ]),
          floatingActionButton: albumLocked || sel.active || !isAndroid
              ? null
              : FloatingActionButton(
                  tooltip: 'Take photos into the Locked album',
                  onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => LockedCameraScreen(albumId: album?.id))),
                  child: const Icon(Icons.photo_camera_outlined),
                ),
          bottomNavigationBar: sel.active ? SelectionBar(selection: sel, all: items, actions: _actions(items, album)) : widget.nav,
        );
      },
    );
  }

  List<Widget> _actions(List<Item> all, VaultAlbum? album) {
    final app = AppScope.read(context);
    final sel = widget.selection;
    final none = sel.count == 0;
    List<Item> s() => sel.of(all);
    return [
      ActionButton(
        icon: Icons.lock_open,
        label: 'Restore',
        onPressed: none
            ? null
            : () async {
                if (await LockedActions.restore(context, s())) sel.clear();
              },
      ),
      ActionButton(icon: Icons.library_add_outlined, label: 'Album', onPressed: none ? null : () => LockedActions.addToAlbum(context, s())),
      ActionButton(icon: Icons.share_outlined, label: 'Share', onPressed: none ? null : () => LockedActions.share(context, s())),
      ActionButton(
        icon: Icons.delete_outline,
        label: 'Delete',
        onPressed: none
            ? null
            : () async {
                if (await LockedActions.delete(context, s())) sel.clear();
              },
      ),
      PopupMenuButton<String>(
        tooltip: 'More',
        enabled: !none,
        onSelected: (v) async {
          final list = s();
          switch (v) {
            case 'remove':
              await app.vault.removeFromAlbum(album!, list);
              sel.clear();
            case 'fav':
              final make = !list.every((i) => i.favorite);
              for (final i in list) {
                await app.vault.setDetails(i, favorite: make);
              }
            case 'rotate':
              for (final i in list) {
                await app.vault.rotate(i, 90);
              }
            case 'show':
              if (mounted) await Actions2.slideshow(context, list);
          }
        },
        itemBuilder: (_) => [
          if (album != null)
            PopupMenuItem(value: 'remove', child: ListTile(leading: const Icon(Icons.remove_circle_outline), title: Text(album.isProtected ? 'Move out of this album' : 'Remove from album'))),
          const PopupMenuItem(value: 'fav', child: ListTile(leading: Icon(Icons.favorite_border), title: Text('Favorite'))),
          const PopupMenuItem(value: 'rotate', child: ListTile(leading: Icon(Icons.rotate_right), title: Text('Rotate'))),
          const PopupMenuItem(value: 'show', child: ListTile(leading: Icon(Icons.slideshow_outlined), title: Text('Slideshow'))),
        ],
        child: ActionButton(icon: Icons.more_horiz, label: 'More', onPressed: null, passive: !none),
      ),
    ];
  }
}

class _AlbumChip extends StatelessWidget {
  const _AlbumChip({required this.label, required this.selected, required this.onTap, this.icon});
  final String label;
  final bool selected;
  final VoidCallback onTap;
  final IconData? icon;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(right: 8, top: 6, bottom: 6),
        child: ChoiceChip(
          avatar: icon == null ? null : Icon(icon, size: 16),
          label: Text(label),
          selected: selected,
          onSelected: (_) => onTap(),
          showCheckmark: false,
        ),
      );
}

/// "2 wrong PINs since you last opened it": shown once after unlocking.
class _WarningBanner extends StatelessWidget {
  const _WarningBanner({required this.attempts, required this.onSee, required this.onDismiss});
  final List<FailedAttempt> attempts;
  final VoidCallback onSee, onDismiss;

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final last = attempts.first;
    final n = attempts.length;
    return Container(
      margin: const EdgeInsets.fromLTRB(12, 0, 12, 6),
      padding: const EdgeInsets.fromLTRB(14, 10, 4, 10),
      decoration: BoxDecoration(color: p.danger.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(12)),
      child: Row(children: [
        Icon(Icons.gpp_maybe_outlined, color: p.danger),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            '$n wrong ${n == 1 ? 'guess' : 'guesses'} since you last opened it. The last: ${formatDay(last.time)}, ${formatTime(last.time)} on ${last.device}.',
            style: TextStyle(color: p.text),
          ),
        ),
        TextButton(onPressed: onSee, child: const Text('See')),
        IconButton(tooltip: 'Dismiss', onPressed: onDismiss, icon: const Icon(Icons.close, size: 20)),
      ]),
    );
  }
}

/// A protected album: its own PIN or password.
class AlbumUnlockView extends StatefulWidget {
  const AlbumUnlockView({super.key, required this.album});
  final VaultAlbum album;

  @override
  State<AlbumUnlockView> createState() => _AlbumUnlockViewState();
}

class _AlbumUnlockViewState extends State<AlbumUnlockView> {
  final _secret = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _secret.dispose();
    super.dispose();
  }

  Future<void> _go() async {
    if (_secret.text.isEmpty) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    final ok = await AppScope.read(context).vault.unlockAlbum(widget.album, _secret.text);
    if (!mounted) return;
    setState(() {
      _busy = false;
      if (!ok) _error = 'That isn\'t right.';
    });
    if (ok) _secret.clear();
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final pin = widget.album.protection?.kind != 'Password';
    return ListView(padding: const EdgeInsets.fromLTRB(28, 32, 28, 32), children: [
      Center(child: Icon(Icons.lock, size: 46, color: p.accent)),
      const SizedBox(height: 12),
      Text('"${widget.album.name}" has its own ${pin ? 'PIN' : 'password'}', textAlign: TextAlign.center, style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: p.text)),
      const SizedBox(height: 18),
      TextField(
        controller: _secret,
        obscureText: true,
        enabled: !_busy,
        keyboardType: pin ? TextInputType.number : TextInputType.visiblePassword,
        inputFormatters: pin ? [FilteringTextInputFormatter.digitsOnly] : null,
        textAlign: TextAlign.center,
        style: TextStyle(fontSize: 20, letterSpacing: pin ? 6 : 1, color: p.text),
        decoration: InputDecoration(hintText: pin ? 'Album PIN' : 'Album password', errorText: _error),
        onSubmitted: (_) => _go(),
      ),
      const SizedBox(height: 14),
      FilledButton(onPressed: _busy ? null : _go, child: _busy ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2.4)) : const Text('Open album')),
    ]);
  }
}

/// Every wrong guess (from all devices) and the photos taken of them.
class WrongGuessesScreen extends StatelessWidget {
  const WrongGuessesScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final vault = app.vault;
    final p = Palette.of(context);
    return ListenableBuilder(
      listenable: vault,
      builder: (context, _) {
        final attempts = vault.failedAttempts;
        final photos = vault.isUnlocked ? vault.intruderPhotos : const <IntruderPhoto>[];
        return Scaffold(
          appBar: AppBar(title: const Text('Wrong guesses')),
          body: ListView(padding: const EdgeInsets.only(bottom: 32), children: [
            if (photos.isNotEmpty) ...[
              const SectionTitle('Photos of wrong guesses'),
              SizedBox(
                height: 150,
                child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), children: [
                  for (final ph in photos)
                    Padding(
                      padding: const EdgeInsets.only(right: 10),
                      child: GestureDetector(
                        onLongPress: () async {
                          if (await confirm(context, 'Delete this photo?', 'It\'s deleted for good.', action: 'Delete', danger: true)) await vault.deleteIntruderPhoto(ph);
                        },
                        child: Column(children: [
                          FutureBuilder<Uint8List>(
                            future: vault.readIntruderPhoto(ph),
                            builder: (_, s) => ClipRRect(
                              borderRadius: BorderRadius.circular(10),
                              child: SizedBox(
                                width: 100,
                                height: 120,
                                child: s.hasData ? Image.memory(s.data!, fit: BoxFit.cover) : ColoredBox(color: p.tile),
                              ),
                            ),
                          ),
                          const SizedBox(height: 4),
                          Text(ph.time == null ? '' : '${shortMonth(ph.time!.month)} ${ph.time!.day}, ${formatTime(ph.time!)}', style: TextStyle(fontSize: 12, color: p.text3)),
                        ]),
                      ),
                    ),
                ]),
              ),
            ],
            SectionTitle('Wrong PINs, passwords and recovery keys (${attempts.length})'),
            if (attempts.isEmpty)
              Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: Text('None. Every wrong guess on any device is listed here.', style: TextStyle(color: p.text2))),
            for (final a in attempts)
              ListTile(
                leading: Icon(Icons.gpp_maybe_outlined, color: p.danger),
                title: Text('Wrong ${a.what == 'Recovery key' ? 'recovery key' : a.what}'),
                subtitle: Text('${formatDay(a.time)}, ${formatTime(a.time)} · ${a.device}'),
              ),
            Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                'After 5 wrong guesses in a row on this phone, Shutterbox makes you wait, longer each time. '
                'What really protects the album is that each guess costs 600,000 rounds of PBKDF2, even for someone with a copy of the folder.',
                style: TextStyle(color: p.text3, fontSize: 13, height: 1.4),
              ),
            ),
          ]),
        );
      },
    );
  }
}

/// New PIN or password, twice.
class SecretDialog extends StatefulWidget {
  const SecretDialog({super.key, required this.title, this.kind = 'PIN', this.note});
  final String title;
  final String kind;
  final String? note;

  @override
  State<SecretDialog> createState() => _SecretDialogState();
}

class _SecretDialogState extends State<SecretDialog> {
  late String _kind = widget.kind;
  final _a = TextEditingController(), _b = TextEditingController();
  String? _error;

  @override
  void dispose() {
    _a.dispose();
    _b.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
        title: Text(widget.title),
        content: SingleChildScrollView(
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            if (widget.note != null) Padding(padding: const EdgeInsets.only(bottom: 12), child: Text(widget.note!)),
            SegmentedButton<String>(
              segments: const [ButtonSegment(value: 'PIN', label: Text('PIN')), ButtonSegment(value: 'Password', label: Text('Password'))],
              selected: {_kind},
              onSelectionChanged: (v) => setState(() => _kind = v.first),
            ),
            const SizedBox(height: 14),
            TextField(
              controller: _a,
              obscureText: true,
              keyboardType: _kind == 'PIN' ? TextInputType.number : TextInputType.visiblePassword,
              decoration: InputDecoration(labelText: _kind == 'PIN' ? 'PIN (4 to 12 digits)' : 'Password (6 or more)'),
            ),
            const SizedBox(height: 10),
            TextField(
              controller: _b,
              obscureText: true,
              keyboardType: _kind == 'PIN' ? TextInputType.number : TextInputType.visiblePassword,
              decoration: InputDecoration(labelText: 'Type it again', errorText: _error),
            ),
          ]),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
          FilledButton(
            onPressed: () {
              final problem = Vault.validate(_a.text, _kind);
              if (problem != null) return setState(() => _error = problem);
              if (_a.text != _b.text) return setState(() => _error = 'The two don\'t match.');
              Navigator.pop(context, (_a.text, _kind));
            },
            child: const Text('OK'),
          ),
        ],
      );
}

/// Before the Locked album is set up: what it is, and how to start.
class LockedIntro extends StatelessWidget {
  const LockedIntro({super.key});

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    Widget point(IconData icon, String text) => Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Icon(icon, size: 20, color: p.accent),
            const SizedBox(width: 12),
            Expanded(child: Text(text, style: TextStyle(color: p.text2, height: 1.4))),
          ]),
        );
    return ListView(padding: const EdgeInsets.fromLTRB(24, 24, 24, 32), children: [
      Center(
        child: Container(
          width: 88,
          height: 88,
          decoration: BoxDecoration(color: p.accent.withValues(alpha: 0.14), shape: BoxShape.circle),
          child: Icon(Icons.lock_outline, size: 42, color: p.accent),
        ),
      ),
      const SizedBox(height: 18),
      Text('An album only you can open', textAlign: TextAlign.center, style: TextStyle(fontSize: 21, fontWeight: FontWeight.w600, color: p.text)),
      const SizedBox(height: 18),
      point(Icons.enhanced_encryption_outlined, 'Photos and videos you lock are encrypted (AES-256) so they can\'t be opened outside Shutterbox, not even by a gallery app or a computer.'),
      point(Icons.pin_outlined, 'Open it with a PIN or a password, and your fingerprint or face if you like. A recovery key gets you back in if you forget.'),
      point(Icons.devices_outlined, 'It\'s the same Locked album as Shutterbox for Windows: copy the folder between your PC and your phone and open it on either.'),
      const SizedBox(height: 12),
      FilledButton.icon(
        onPressed: () => Navigator.of(context).push(MaterialPageRoute<bool>(builder: (_) => const LockSetupScreen())),
        icon: const Icon(Icons.lock_outline),
        label: const Text('Set up the Locked album'),
      ),
      const SizedBox(height: 10),
      OutlinedButton.icon(
        onPressed: () => LockedActions.openOther(context),
        icon: const Icon(Icons.folder_open_outlined),
        label: const Text('Open a Locked album from a PC or another phone'),
      ),
    ]);
  }
}

/// PIN or password, the fingerprint button, and the recovery key.
class UnlockView extends StatefulWidget {
  const UnlockView({super.key, required this.onUnlocked});
  final VoidCallback onUnlocked;

  @override
  State<UnlockView> createState() => _UnlockViewState();
}

class _UnlockViewState extends State<UnlockView> {
  final _secret = TextEditingController();
  bool _busy = false;
  bool _recovery = false;
  String? _error;
  bool _bio = false;

  @override
  void initState() {
    super.initState();
    final vault = AppScope.read(context).vault;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _ticking();
    });
    Biometrics.hasKey(vault.identity).then((v) {
      if (mounted) setState(() => _bio = v);
    });
  }

  @override
  void dispose() {
    _secret.dispose();
    _tick?.cancel();
    super.dispose();
  }

  Timer? _tick;

  void _ticking() {
    if (_tick != null || _waitLeft == Duration.zero) return;
    _tick = Timer.periodic(const Duration(seconds: 1), (t) {
      if (!mounted || _waitLeft == Duration.zero) {
        t.cancel();
        _tick = null;
      }
      if (mounted) setState(() {});
    });
  }

  /// How long until another guess is allowed on this phone.
  Duration get _waitLeft {
    final until = DateTime.tryParse(AppScope.read(context).settings.waitUntil);
    if (until == null) return Duration.zero;
    final left = until.difference(DateTime.now());
    return left.isNegative ? Duration.zero : left;
  }

  Future<void> _unlock() async {
    final app = AppScope.read(context);
    final vault = app.vault;
    final s = _secret.text;
    if (s.isEmpty || _waitLeft > Duration.zero) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    final ok = _recovery ? await vault.unlockWithRecoveryKey(s) : await vault.unlock(s);
    if (!mounted) return;
    setState(() => _busy = false);
    if (ok) {
      app.settings.update((x) => x
        ..wrongInARow = 0
        ..waitUntil = '');
      _secret.clear();
      widget.onUnlocked();
      if (_recovery) {
        toast(context, 'Unlocked with your recovery key. You can set a new PIN or password in Settings.');
      }
    } else {
      final wrong = app.settings.wrongInARow + 1;
      final wait = Settings.waitAfter(wrong);
      app.settings.update((x) => x
        ..wrongInARow = wrong
        ..waitUntil = wait == Duration.zero ? '' : DateTime.now().add(wait).toIso8601String());
      final kind = vault.info?.kind == 'Password' ? 'password' : 'PIN';
      setState(() => _error = _recovery ? 'That recovery key doesn\'t open this album.' : 'That $kind isn\'t right.');
      _ticking();
      if (vault.photographsWrongGuesses && isAndroid) {
        takeQuietPhoto().then((jpeg) {
          if (jpeg != null) vault.saveIntruderPhoto(jpeg);
        });
      }
    }
  }

  Future<void> _biometric() async {
    final vault = AppScope.read(context).vault;
    final id = vault.identity;
    if (id == null) return;
    final key = await Biometrics.readKey(id);
    if (key == null) return;
    if (await vault.unlockWithKey(key)) {
      widget.onUnlocked();
    } else {
      await Biometrics.forget(id);
      if (mounted) setState(() => _error = 'Your fingerprint key no longer fits this album. Use your PIN or password.');
    }
  }

  @override
  Widget build(BuildContext context) {
    final vault = AppScope.of(context).vault;
    final p = Palette.of(context);
    final pin = vault.info?.kind != 'Password' && !_recovery;
    return ListView(padding: const EdgeInsets.fromLTRB(28, 32, 28, 32), children: [
      Center(child: Icon(Icons.lock_outline, size: 54, color: p.accent)),
      const SizedBox(height: 14),
      Text('The Locked album is locked', textAlign: TextAlign.center, style: TextStyle(fontSize: 20, fontWeight: FontWeight.w600, color: p.text)),
      const SizedBox(height: 6),
      Text(
        _recovery ? 'Type your recovery key (the 25 letters and numbers you saved when you set it up).' : 'Enter your ${pin ? 'PIN' : 'password'} to open it.',
        textAlign: TextAlign.center,
        style: TextStyle(color: p.text2),
      ),
      const SizedBox(height: 20),
      TextField(
        controller: _secret,
        autofocus: false,
        obscureText: !_recovery,
        enabled: !_busy,
        keyboardType: pin ? TextInputType.number : TextInputType.visiblePassword,
        inputFormatters: pin ? [FilteringTextInputFormatter.digitsOnly] : null,
        textAlign: TextAlign.center,
        textCapitalization: _recovery ? TextCapitalization.characters : TextCapitalization.none,
        style: TextStyle(fontSize: 20, letterSpacing: pin ? 6 : 1, color: p.text),
        decoration: InputDecoration(hintText: _recovery ? 'XXXXX-XXXXX-XXXXX-XXXXX-XXXXX' : (pin ? 'PIN' : 'Password'), errorText: _error),
        onSubmitted: (_) => _unlock(),
      ),
      const SizedBox(height: 14),
      if (_waitLeft > Duration.zero)
        Padding(
          padding: const EdgeInsets.only(bottom: 10),
          child: Text('Too many wrong guesses. Try again in ${formatDuration(_waitLeft.inSeconds.toDouble())}.', textAlign: TextAlign.center, style: TextStyle(color: p.danger)),
        ),
      FilledButton(
        onPressed: _busy || _waitLeft > Duration.zero ? null : _unlock,
        child: _busy
            ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2.4))
            : const Text('Unlock'),
      ),
      if (_bio && !_recovery) ...[
        const SizedBox(height: 10),
        OutlinedButton.icon(onPressed: _busy ? null : _biometric, icon: const Icon(Icons.fingerprint), label: const Text('Use fingerprint or face')),
      ],
      const SizedBox(height: 8),
      TextButton(
        onPressed: () => setState(() {
          _recovery = !_recovery;
          _error = null;
          _secret.clear();
        }),
        child: Text(_recovery ? 'Use my ${vault.info?.kind == 'Password' ? 'password' : 'PIN'}' : 'Forgot it? Use your recovery key'),
      ),
    ]);
  }
}

/// Sets up the Locked album: a PIN or password, the recovery key, then fingerprint or face.
class LockSetupScreen extends StatefulWidget {
  const LockSetupScreen({super.key});

  @override
  State<LockSetupScreen> createState() => _LockSetupScreenState();
}

class _LockSetupScreenState extends State<LockSetupScreen> {
  String _kind = 'PIN';
  final _a = TextEditingController(), _b = TextEditingController();
  String? _error;
  bool _busy = false;
  String? _recoveryKey;
  bool _saved = false;
  String? _bioName;

  @override
  void dispose() {
    _a.dispose();
    _b.dispose();
    super.dispose();
  }

  Future<void> _create() async {
    final vault = AppScope.read(context).vault;
    final problem = Vault.validate(_a.text, _kind);
    if (problem != null) return setState(() => _error = problem);
    if (_a.text != _b.text) return setState(() => _error = 'The two don\'t match.');
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final key = await vault.setUp(_a.text, _kind);
      final bio = await Biometrics.available();
      if (!mounted) return;
      setState(() {
        _recoveryKey = key;
        _bioName = bio;
      });
    } catch (e) {
      setState(() => _error = 'Couldn\'t set it up ($e).');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _finish({required bool biometric}) async {
    final vault = AppScope.read(context).vault;
    if (biometric) {
      final ok = await LockedActions.turnOnBiometrics(context);
      if (!ok) return;
    }
    if (mounted) Navigator.of(context).pop(vault.isUnlocked);
  }

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final key = _recoveryKey;
    return Scaffold(
      appBar: AppBar(title: Text(key == null ? 'Set up the Locked album' : 'Your recovery key')),
      body: key == null
          ? ListView(padding: const EdgeInsets.fromLTRB(20, 8, 20, 32), children: [
              Text('Choose how you\'ll open it.', style: TextStyle(color: p.text2)),
              const SizedBox(height: 14),
              SegmentedButton<String>(
                segments: const [
                  ButtonSegment(value: 'PIN', label: Text('PIN'), icon: Icon(Icons.pin_outlined)),
                  ButtonSegment(value: 'Password', label: Text('Password'), icon: Icon(Icons.password)),
                ],
                selected: {_kind},
                onSelectionChanged: (v) => setState(() {
                  _kind = v.first;
                  _a.clear();
                  _b.clear();
                  _error = null;
                }),
              ),
              const SizedBox(height: 18),
              _SecretField(controller: _a, kind: _kind, label: _kind == 'PIN' ? 'PIN (4 to 12 digits)' : 'Password (at least 6 characters)'),
              const SizedBox(height: 12),
              _SecretField(controller: _b, kind: _kind, label: 'Type it again', onSubmitted: _create),
              if (_error != null) Padding(padding: const EdgeInsets.only(top: 10), child: Text(_error!, style: TextStyle(color: p.danger))),
              const SizedBox(height: 18),
              FilledButton(
                onPressed: _busy ? null : _create,
                child: _busy
                    ? const Row(mainAxisSize: MainAxisSize.min, children: [
                        SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2.2)),
                        SizedBox(width: 12),
                        Text('Setting up…'),
                      ])
                    : const Text('Continue'),
              ),
              const SizedBox(height: 14),
              Text('Nobody, including Bytehawk Studio, can open the Locked album without your PIN or password, your recovery key, or your fingerprint on this phone.',
                  style: TextStyle(color: p.text3, fontSize: 13, height: 1.4)),
            ])
          : ListView(padding: const EdgeInsets.fromLTRB(20, 8, 20, 32), children: [
              Text('If you forget your ${_kind == 'PIN' ? 'PIN' : 'password'}, this key is the only way back in. Write it down or keep it in your password manager. It\'s shown only now.',
                  style: TextStyle(color: p.text2, height: 1.4)),
              const SizedBox(height: 18),
              Container(
                padding: const EdgeInsets.symmetric(vertical: 18, horizontal: 12),
                decoration: BoxDecoration(color: p.card, borderRadius: BorderRadius.circular(14), border: Border.all(color: p.accent.withValues(alpha: 0.5))),
                child: SelectableText(key, textAlign: TextAlign.center, style: TextStyle(fontSize: 19, letterSpacing: 1.5, fontWeight: FontWeight.w600, color: p.text, fontFamily: 'monospace')),
              ),
              const SizedBox(height: 10),
              Align(alignment: Alignment.center, child: TextButton.icon(onPressed: () => copyText(context, key, 'Recovery key'), icon: const Icon(Icons.copy), label: const Text('Copy'))),
              CheckboxListTile(
                value: _saved,
                onChanged: (v) => setState(() => _saved = v ?? false),
                title: const Text('I\'ve saved my recovery key'),
                contentPadding: EdgeInsets.zero,
              ),
              const SizedBox(height: 8),
              if (_bioName != null) ...[
                FilledButton.icon(
                  onPressed: _saved ? () => _finish(biometric: true) : null,
                  icon: const Icon(Icons.fingerprint),
                  label: Text('Also unlock with ${_bioName!.toLowerCase()}'),
                ),
                const SizedBox(height: 8),
                OutlinedButton(onPressed: _saved ? () => _finish(biometric: false) : null, child: const Text('Not now')),
              ] else
                FilledButton(onPressed: _saved ? () => _finish(biometric: false) : null, child: const Text('Done')),
            ]),
    );
  }
}

class _SecretField extends StatelessWidget {
  const _SecretField({required this.controller, required this.kind, required this.label, this.onSubmitted});
  final TextEditingController controller;
  final String kind, label;
  final VoidCallback? onSubmitted;

  @override
  Widget build(BuildContext context) => TextField(
        controller: controller,
        obscureText: true,
        keyboardType: kind == 'PIN' ? TextInputType.number : TextInputType.visiblePassword,
        inputFormatters: kind == 'PIN' ? [FilteringTextInputFormatter.digitsOnly, LengthLimitingTextInputFormatter(12)] : null,
        decoration: InputDecoration(labelText: label),
        onSubmitted: (_) => onSubmitted?.call(),
      );
}

/// Opens the Locked album before something needs it (setting it up first if there isn't one).
class LockedGate {
  static Future<bool> ensureUnlocked(BuildContext context) async {
    final vault = AppScope.read(context).vault;
    if (vault.isUnlocked) return true;
    if (!vault.isSetUp) {
      final ok = await Navigator.of(context).push(MaterialPageRoute<bool>(builder: (_) => const LockSetupScreen()));
      return ok == true && vault.isUnlocked;
    }
    final ok = await Navigator.of(context).push(MaterialPageRoute<bool>(
      builder: (ctx) => Scaffold(
        appBar: AppBar(title: const Text('Unlock the Locked album')),
        body: UnlockView(onUnlocked: () => Navigator.of(ctx).pop(true)),
      ),
    ));
    return ok == true && vault.isUnlocked;
  }
}

/// What you can do with the Locked album and the items in it.
class LockedActions {
  static Future<VaultAlbum?> newAlbum(BuildContext context) async {
    final vault = AppScope.read(context).vault;
    final name = await askText(context, 'New album', hint: 'Album name', action: 'Create');
    if (name == null) return null;
    return vault.createAlbum(name);
  }

  static Future<void> addToAlbum(BuildContext context, List<Item> items) async {
    final vault = AppScope.read(context).vault;
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
              child: Text('Add to an album in the Locked album', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: p.text)),
            ),
            ListTile(leading: Icon(Icons.add, color: p.accent), title: const Text('New album…'), onTap: () => Navigator.pop(ctx, '+new')),
            for (final a in vault.albums.where((a) => !a.isProtected || a.isOpen))
              ListTile(
                leading: Icon(a.isProtected ? Icons.lock_open : Icons.photo_album_outlined),
                title: Text(a.name),
                subtitle: a.isProtected ? const Text('Has its own PIN: items are moved in') : null,
                onTap: () => Navigator.pop(ctx, a.id),
              ),
          ]),
        ),
      ),
    );
    if (pick == null || !context.mounted) return;
    final album = pick == '+new' ? await newAlbum(context) : vault.album(pick);
    if (album == null || !context.mounted) return;
    final list = items.where((i) => i.protectedIn == null).toList();
    final n = album.isProtected ? await withProgress(context, 'Moving into ${album.name}', (_) => vault.addToAlbum(album, list)) : await vault.addToAlbum(album, list);
    if (context.mounted) toast(context, n == 0 ? 'Already in ${album.name}' : '${album.isProtected ? 'Moved' : 'Added'} $n to ${album.name}');
  }

  /// Shares for a moment: decrypted copies are made for the share and deleted when Shutterbox locks or restarts.
  static Future<void> share(BuildContext context, List<Item> items) async {
    final app = AppScope.read(context);
    if (!await confirm(context, 'Share ${items.length == 1 ? 'it' : '${items.length} items'}?',
        'Shutterbox makes temporary decrypted copies for the app you share to, and deletes them when the Locked album locks. The app you share with keeps what it receives.',
        action: 'Share')) {
      return;
    }
    try {
      final files = await app.vault.decryptToShare(items);
      app.expectReturn();
      await SharePlus.instance.share(ShareParams(files: [for (final f in files) XFile(f.path)]));
    } catch (e) {
      if (context.mounted) toast(context, 'Couldn\'t share ($e).');
    }
  }

  /// Gives an album its own PIN or password.
  static Future<void> protectAlbum(BuildContext context, VaultAlbum album) async {
    final vault = AppScope.read(context).vault;
    final r = await showDialog<(String, String)>(
      context: context,
      builder: (_) => SecretDialog(
        title: 'A PIN for "${album.name}"',
        note: 'Its items are re-encrypted with a key only this PIN or password opens, as well as needing the Locked album unlocked. '
            'If you forget it, they can\'t be recovered: the recovery key doesn\'t open it.',
      ),
    );
    if (r == null || !context.mounted) return;
    await withProgress(context, 'Encrypting "${album.name}"', (_) => vault.protectAlbum(album, r.$1, r.$2));
    if (context.mounted) toast(context, '"${album.name}" now has its own ${r.$2 == 'PIN' ? 'PIN' : 'password'}');
  }

  /// Items another app shared to "Lock with Shutterbox": copied into the Locked album (the other app's copy stays).
  static Future<void> lockShared(BuildContext context, List<SharedFile> files) async {
    final app = AppScope.read(context);
    try {
      final media = files.where((f) => Item.isVideoName(f.name) || Item.photoExtensions.contains(Item.extension(f.name)) || f.mime.startsWith('image/') || f.mime.startsWith('video/')).toList();
      if (media.isEmpty) return;
      if (!await confirm(context, 'Lock ${media.length == 1 ? 'this' : '${media.length} items'} with Shutterbox?',
          'Shutterbox puts an encrypted copy in the Locked album. The app you shared from keeps its own copy: delete it there if you want it gone.',
          action: 'Lock')) {
        return;
      }
      if (!context.mounted || !await LockedGate.ensureUnlocked(context) || !context.mounted) return;
      final sources = <LockSource>[];
      for (final f in media) {
        final taken = dateFromName(f.name) ?? await f.file.lastModified();
        final isPhoto = !Item.isVideoName(f.name) && !f.mime.startsWith('video/');
        sources.add(LockSource(
          item: Item(id: 'shared-${f.name}', name: f.name, taken: taken, isVideo: !isPhoto),
          file: f.file,
          thumbnail: isPhoto ? () async => smallPicture(await f.file.readAsBytes()) : null,
        ));
      }
      final r = await withProgress(context, 'Locking', (report) => app.vault.add(sources, progress: report));
      if (context.mounted) {
        toast(context, [
          if (r.added > 0) 'Locked ${r.added}',
          if (r.skipped > 0) '${r.skipped} already in the Locked album',
          if (r.errors.isNotEmpty) '${r.errors.length} couldn\'t be locked: ${r.errors.first}',
        ].join('. '));
      }
    } finally {
      for (final f in files) {
        try {
          await f.file.delete();
        } catch (_) {}
      }
    }
  }

  /// Decrypts items back into your gallery (DCIM/Shutterbox/Restored) and takes them out of the Locked album.
  static Future<bool> restore(BuildContext context, List<Item> items) async {
    final app = AppScope.read(context);
    if (!await confirm(context, 'Restore ${items.length == 1 ? 'it' : '${items.length} items'}?',
        'They\'re decrypted into your gallery (DCIM/Shutterbox/Restored) and taken out of the Locked album.', action: 'Restore')) {
      return false;
    }
    if (!context.mounted) return false;
    final folder = '${app.cacheFolder}/Restoring';
    final r = await withProgress(
      context,
      'Restoring',
      (report) => app.vault.restore(items, folder, progress: report, saved: (file, item) async {
        final ok = await app.source.addToGallery(file, file.uri.pathSegments.last, 'DCIM/Shutterbox/Restored', taken: item.taken);
        try {
          await file.delete();
        } catch (_) {}
        return ok;
      }),
    );
    await app.library.scan();
    if (context.mounted) {
      toast(context, r.errors.isEmpty ? 'Restored ${r.added} to your gallery' : 'Restored ${r.added}. ${r.errors.length} couldn\'t be restored: ${r.errors.first}');
    }
    return r.added > 0;
  }

  static Future<bool> delete(BuildContext context, List<Item> items) async {
    final vault = AppScope.read(context).vault;
    if (!await confirm(context, 'Delete ${items.length == 1 ? 'it' : '${items.length} items'} for good?',
        'Encrypted items can\'t go to a trash. Once deleted they\'re gone.', action: 'Delete', danger: true)) {
      return false;
    }
    await vault.remove(items);
    if (context.mounted) toast(context, 'Deleted ${items.length}');
    return true;
  }

  static Future<bool> turnOnBiometrics(BuildContext context) async {
    final vault = AppScope.read(context).vault;
    final id = vault.identity;
    final key = vault.copyKey();
    if (id == null || key == null) return false;
    if (await Biometrics.available() == null) {
      if (context.mounted) toast(context, 'Set up a fingerprint or face unlock in Android\'s settings first.');
      return false;
    }
    if (!await Biometrics.verify('Use your fingerprint or face for the Locked album')) return false;
    await Biometrics.saveKey(id, key);
    key.fillRange(0, key.length, 0);
    if (context.mounted) toast(context, 'You can now unlock with your fingerprint or face on this phone');
    return true;
  }

  /// Uses a Locked album folder from a PC or another phone (copied over USB, from a cloud folder, an SD card…).
  static Future<void> openOther(BuildContext context) async {
    final app = AppScope.read(context);
    if (!await FilesAccess.ensure(context, 'To open a Locked album folder you copied to this phone, Shutterbox needs to read that folder.')) return;
    if (!context.mounted) return;
    if (app.vault.isSetUp &&
        !await confirm(context, 'Use another Locked album?',
            'The Locked album Shutterbox uses now stays where it is (${friendlyPath(app.vault.folder)}). You can switch back by opening that folder.',
            action: 'Choose folder')) {
      return;
    }
    AppScope.read(context).expectReturn();
    final dir = await FilePicker.platform.getDirectoryPath(dialogTitle: 'Choose the "Shutterbox Locked album" folder');
    if (dir == null || !context.mounted) return;
    final previousBio = app.vault.identity;
    final e = await app.vault.openFolder(dir);
    if (!context.mounted) return;
    if (e != null) return toast(context, e);
    if (previousBio != app.vault.identity) await Biometrics.forget(previousBio);
    toast(context, 'Opened the Locked album in ${friendlyPath(app.vault.folder)}. Unlock it with its PIN, password or recovery key.');
  }

  /// Moves the album into a folder you choose (a synced folder, an SD card…).
  static Future<void> moveTo(BuildContext context) async {
    final app = AppScope.read(context);
    if (!await FilesAccess.ensure(context, 'To move the Locked album to a folder you choose, Shutterbox needs to write there.')) return;
    AppScope.read(context).expectReturn();
    final dir = await FilePicker.platform.getDirectoryPath(dialogTitle: 'Choose where to put the Locked album');
    if (dir == null || !context.mounted) return;
    final e = await withProgress(context, 'Moving the Locked album', (report) => app.vault.moveTo(dir, progress: report));
    if (context.mounted) toast(context, e ?? 'Moved to ${friendlyPath(app.vault.folder)}');
  }
}
