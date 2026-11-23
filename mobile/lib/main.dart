import 'dart:async';
import 'dart:io';

import 'package:cryptography_flutter/cryptography_flutter.dart';
import 'package:flutter/material.dart';
import 'package:path_provider/path_provider.dart';

import 'app.dart';
import 'files_access.dart';
import 'library.dart';
import 'media_source.dart';
import 'screens/home.dart';
import 'settings.dart';
import 'theme.dart';
import 'updater.dart';
import 'vault/vault.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  try {
    FlutterCryptography.enable(); // AES-GCM through Android's own crypto: much faster for videos
  } catch (_) {}
  final support = await getApplicationSupportDirectory();
  final cache = await getTemporaryDirectory();
  final settings = await Settings.load(File('${support.path}/settings.json'));
  final library = Library(DeviceSource(), settings, file: File('${support.path}/library.json'));
  await library.load();
  final defaultLocked = '${support.path}/Locked';
  final vault = Vault(settings.lockedFolder ?? defaultLocked, tempFolder: '${cache.path}/LockedPlayback');
  vault.cleanTemp(); // in case Shutterbox was closed while a Locked album video played
  final app = AppState(
    settings: settings,
    library: library,
    vault: vault,
    updater: Updater(settings),
    defaultLockedFolder: defaultLocked,
    cacheFolder: cache.path,
  );
  runApp(ShutterboxApp(app: app));
  unawaited(_start(app));
}

Future<void> _start(AppState app) async {
  await app.library.scan(ask: true);
  if (app.library.access == true) {
    app.source.watch(app.library.scan);
    if (!app.settings.askedForAccess) {
      await FilesAccess.askForPhotoLocations();
      app.settings.update((s) => s.askedForAccess = true);
    }
  }
  await app.updater.check();
}

class ShutterboxApp extends StatefulWidget {
  const ShutterboxApp({super.key, required this.app, this.home});
  final AppState app;

  /// Tests and screenshots start elsewhere.
  final Widget? home;

  @override
  State<ShutterboxApp> createState() => _ShutterboxAppState();
}

class _ShutterboxAppState extends State<ShutterboxApp> with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    final app = widget.app;
    switch (state) {
      case AppLifecycleState.resumed:
        app.checkAutoLock();
        if (app.library.access != true) app.library.scan(); // after allowing access in Android's settings
      case AppLifecycleState.detached:
        app.vault.lock(); // always when Shutterbox closes
      default:
        break;
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = widget.app;
    return AppScope(
      app: app,
      child: ListenableBuilder(
        listenable: app.settings,
        builder: (context, _) => MaterialApp(
          title: 'Shutterbox',
          debugShowCheckedModeBanner: false,
          theme: buildTheme(Brightness.light, app.settings.accent),
          darkTheme: buildTheme(Brightness.dark, app.settings.accent),
          themeMode: switch (app.settings.theme) { 'light' => ThemeMode.light, 'system' => ThemeMode.system, _ => ThemeMode.dark },
          builder: (context, child) => Listener(
            behavior: HitTestBehavior.translucent,
            onPointerDown: (_) => app.touch(),
            child: child,
          ),
          home: widget.home ?? const HomeScreen(),
        ),
      ),
    );
  }
}
