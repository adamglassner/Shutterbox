import 'dart:async';
import 'dart:io';

import 'package:cryptography_flutter/cryptography_flutter.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
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
import 'vision/analyzer.dart';
import 'vision/people.dart';
import 'vision/selftest.dart';
import 'vision/vision.dart';

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
  if (settings.deviceId.isEmpty) settings.update((s) => s.deviceId = VaultItem.newId().substring(0, 12));
  final vault = Vault(settings.lockedFolder ?? defaultLocked, tempFolder: '${cache.path}/LockedPlayback', deviceId: settings.deviceId, deviceName: 'Android phone');
  vault.cleanTemp(); // in case Shutterbox was closed while a Locked album video played
  final app = AppState(
    settings: settings,
    library: library,
    vault: vault,
    updater: Updater(settings),
    defaultLockedFolder: defaultLocked,
    cacheFolder: cache.path,
  );
  try {
    final words = VisionWords(await rootBundle.loadString('assets/models/vision_words.txt'));
    final people = PeopleStore(words, file: File('${support.path}/people.json'));
    await people.load();
    app.attachPeople(people, Analyzer(library: library, people: people, vision: Vision(NativeVisionEngine()), settings: settings));
  } catch (_) {}
  runApp(ShutterboxApp(app: app));
  unawaited(_start(app));
}

Future<void> _start(AppState app) async {
  // the gallery plugin needs the activity, which is attached by the first frame
  await WidgetsBinding.instance.endOfFrame;
  final args = await launchArgs();
  if (args['selftest'] == 'vision' && args['dir'] != null) await visionSelfTest(args['dir']!);
  await app.library.scan(ask: true);
  if (app.library.access == true) {
    app.source.watch(app.library.scan);
    if (!app.settings.askedForAccess) {
      await FilesAccess.askForPhotoLocations();
      app.settings.update((s) => s.askedForAccess = true);
    }
  }
  app.analyzer?.poke();
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
      case AppLifecycleState.paused:
        app.left();
      case AppLifecycleState.resumed:
        app.returned();
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
