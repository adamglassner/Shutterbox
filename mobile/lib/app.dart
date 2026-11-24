import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:sensors_plus/sensors_plus.dart';

import 'bytes_image.dart';
import 'library.dart';
import 'media_source.dart';
import 'native.dart';
import 'models.dart';
import 'settings.dart';
import 'updater.dart';
import 'vault/vault.dart';
import 'vision/analyzer.dart';
import 'vision/people.dart';

/// Everything the screens share: settings, the library, the Locked album and the updater.
class AppState {
  AppState({required this.settings, required this.library, required this.vault, required this.updater, required this.defaultLockedFolder, required this.cacheFolder, bool autoLock = true, this.native = true}) {
    if (autoLock) _autoLock = Timer.periodic(const Duration(seconds: 15), (_) => checkAutoLock());
    vault.addListener(_vaultChanged);
    settings.addListener(_settingsChanged);
  }

  /// False in tests: no Android-only calls (secure window, sensors).
  final bool native;

  /// Asks Home to show the Locked tab (even when it's hidden).
  final showLocked = ValueNotifier<int>(0);

  /// Asks Home to switch to Photos (after a panic lock).
  final showPhotos = ValueNotifier<int>(0);

  bool _expectingReturn = false;

  /// Shutterbox is about to open another app's screen (share sheet, file picker, camera): coming back isn't "leaving".
  void expectReturn() => _expectingReturn = true;

  /// Android says Shutterbox went to the background.
  void left() {
    analyzer?.pause();
    if (_expectingReturn) return;
    if (settings.lockOnLeave) vault.lock();
  }

  void returned() {
    analyzer?.resume();
    _expectingReturn = false;
    checkAutoLock();
    if (vault.isUnlocked) vault.reload();
  }

  /// Locks everything and goes to Photos.
  void panicLock() {
    vault.lock();
    showPhotos.value++;
  }

  StreamSubscription<AccelerometerEvent>? _tilt;
  DateTime? _faceDownSince;

  void _settingsChanged() => _syncNative();

  void _syncNative() {
    if (!native) return;
    final open = vault.isUnlocked;
    Native.setSecure(open && settings.protectScreen);
    final watch = open && settings.lockFaceDown;
    if (watch && _tilt == null) {
      try {
        _tilt = accelerometerEventStream(samplingPeriod: SensorInterval.uiInterval).listen((e) {
          // face down: gravity points out of the screen's back
          if (e.z < -8.5) {
            _faceDownSince ??= DateTime.now();
            if (DateTime.now().difference(_faceDownSince!).inMilliseconds > 800) {
              _faceDownSince = null;
              panicLock();
            }
          } else {
            _faceDownSince = null;
          }
        }, onError: (_) {});
      } catch (_) {}
    } else if (!watch && _tilt != null) {
      _tilt?.cancel();
      _tilt = null;
    }
  }

  /// People and what's in each photo (null where the models aren't available, as in tests).
  PeopleStore? people;
  Analyzer? analyzer;

  /// Connects People to the library's search and starts looking through photos.
  void attachPeople(PeopleStore store, Analyzer? a) {
    people = store;
    analyzer = a;
    library.extraMatch = (i, w) => store.matches(i.id, w);
    if (a != null) {
      library.addListener(a.poke);
      settings.addListener(a.poke);
    }
  }

  final Settings settings;
  final Library library;
  final Vault vault;
  final Updater updater;
  final String defaultLockedFolder;

  /// Temporary files (restoring from the Locked album).
  final String cacheFolder;
  Timer? _autoLock;
  bool _wasUnlocked = false;

  MediaSource get source => library.source;

  void _vaultChanged() {
    if (_wasUnlocked && !vault.isUnlocked) {
      // decrypted pictures leave memory with the key
      PaintingBinding.instance.imageCache.clear();
      PaintingBinding.instance.imageCache.clearLiveImages();
    }
    _wasUnlocked = vault.isUnlocked;
    _syncNative();
    // remember where the album is (moved, or opened from another device)
    final f = vault.folder == defaultLockedFolder ? null : vault.folder;
    if (f != settings.lockedFolder) settings.update((s) => s.lockedFolder = f);
  }

  /// Locks the Locked album after the minutes set in it without use.
  void checkAutoLock() {
    if (!vault.isUnlocked || vault.isBusy) return;
    final minutes = vault.info?.autoLockMinutes ?? 5;
    if (minutes > 0 && DateTime.now().difference(vault.lastActivity).inMinutes >= minutes) vault.lock();
  }

  void touch() => vault.lastActivity = DateTime.now();

  ImageProvider thumbnail(Item i, int pixels) =>
      i.locked ? BytesImage('locked-thumb:${i.vaultId}', () => vault.readThumb(i.vaultId!, i.protectedIn)) : source.thumbnail(i, pixels);

  ImageProvider full(Item i) => i.locked ? BytesImage('locked-full:${i.vaultId}', () => vault.readBytes(i.vaultId!, i.protectedIn)) : source.full(i);

  bool isFavorite(Item i) => i.locked ? i.favorite : library.isFavorite(i);

  void dispose() {
    _autoLock?.cancel();
    _tilt?.cancel();
    vault.removeListener(_vaultChanged);
    settings.removeListener(_settingsChanged);
  }

  /// Drops decrypted pictures from memory (after locking a protected album).
  void forgetPictures() {
    PaintingBinding.instance.imageCache.clear();
    PaintingBinding.instance.imageCache.clearLiveImages();
  }
}

class AppScope extends InheritedWidget {
  const AppScope({super.key, required this.app, required super.child});
  final AppState app;

  static AppState of(BuildContext context) => context.dependOnInheritedWidgetOfExactType<AppScope>()!.app;
  static AppState read(BuildContext context) => context.getInheritedWidgetOfExactType<AppScope>()!.app;

  @override
  bool updateShouldNotify(AppScope oldWidget) => app != oldWidget.app;
}

// ------------------------------------------------------------------ small helpers used everywhere

void toast(BuildContext context, String message) {
  final m = ScaffoldMessenger.maybeOf(context);
  m?.hideCurrentSnackBar();
  m?.showSnackBar(SnackBar(content: Text(message), duration: Duration(milliseconds: message.length > 80 ? 6000 : 3000)));
}

Future<String?> askText(BuildContext context, String title, {String initial = '', String hint = '', String action = 'OK'}) {
  final c = TextEditingController(text: initial);
  return showDialog<String>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: TextField(
        controller: c,
        autofocus: true,
        decoration: InputDecoration(hintText: hint),
        textCapitalization: TextCapitalization.sentences,
        onSubmitted: (v) => Navigator.pop(ctx, v),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
        FilledButton(onPressed: () => Navigator.pop(ctx, c.text), child: Text(action)),
      ],
    ),
  );
}

Future<bool> confirm(BuildContext context, String title, String message, {String action = 'OK', bool danger = false}) async {
  final r = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')),
        FilledButton(
          style: danger ? FilledButton.styleFrom(backgroundColor: Theme.of(ctx).colorScheme.error, foregroundColor: Theme.of(ctx).colorScheme.onError) : null,
          onPressed: () => Navigator.pop(ctx, true),
          child: Text(action),
        ),
      ],
    ),
  );
  return r == true;
}

/// Runs [work] with a progress dialog; [work] reports (done, total).
Future<T> withProgress<T>(BuildContext context, String title, Future<T> Function(void Function(int done, int total) report) work) async {
  final progress = ValueNotifier<(int, int)>((0, 0));
  unawaited(showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (ctx) => PopScope(
      canPop: false,
      child: AlertDialog(
        title: Text(title),
        content: ValueListenableBuilder<(int, int)>(
          valueListenable: progress,
          builder: (_, v, __) => Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            LinearProgressIndicator(value: v.$2 == 0 ? null : v.$1 / v.$2),
            const SizedBox(height: 12),
            Text(v.$2 == 0 ? 'Starting…' : '${v.$1} of ${v.$2}'),
          ]),
        ),
      ),
    ),
  ));
  try {
    return await work((d, t) => progress.value = (d, t));
  } finally {
    if (context.mounted) Navigator.of(context, rootNavigator: true).pop();
  }
}

Future<void> copyText(BuildContext context, String text, String what) async {
  await Clipboard.setData(ClipboardData(text: text));
  if (context.mounted) toast(context, '$what copied');
}

/// The phone's main storage (for showing folders in plain words).
String friendlyPath(String path) {
  const root = '/storage/emulated/0';
  if (path.startsWith(root)) return 'Phone storage${path.substring(root.length)}';
  if (path.contains('/data/') && path.contains('com.bytehawkstudio')) return 'Shutterbox\'s private storage';
  return path;
}

bool get isAndroid {
  try {
    return Platform.isAndroid;
  } catch (_) {
    return false;
  }
}
