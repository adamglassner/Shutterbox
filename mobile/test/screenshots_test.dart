// Renders the main screens to test/screenshots/*.png (run with --update-goldens). CI publishes them to the
// "screenshots-android" branch.
import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:shutterbox/app.dart';
import 'package:shutterbox/library.dart';
import 'package:shutterbox/widgets.dart';
import 'package:shutterbox/main.dart';
import 'package:shutterbox/screens/albums.dart';
import 'package:shutterbox/screens/details.dart';
import 'package:shutterbox/screens/home.dart';
import 'package:shutterbox/screens/import.dart';
import 'package:shutterbox/screens/locked.dart';
import 'package:shutterbox/screens/search.dart';
import 'package:shutterbox/screens/settings_screen.dart';
import 'package:shutterbox/screens/viewer.dart';
import 'package:shutterbox/settings.dart';
import 'package:shutterbox/updater.dart';
import 'package:shutterbox/vault/vault.dart';

import 'fake_source.dart';
import 'fonts.dart';

/// A Locked album with a few photos and two albums, made once for all the screenshots.
late String lockedFolder;
late List<int> lockedKey;

Future<void> warm(ImageProvider p) {
  final done = Completer<void>();
  final stream = p.resolve(ImageConfiguration.empty);
  late ImageStreamListener l;
  l = ImageStreamListener((_, __) {
    if (!done.isCompleted) done.complete();
    stream.removeListener(l);
  }, onError: (_, __) {
    if (!done.isCompleted) done.complete();
    stream.removeListener(l);
  });
  stream.addListener(l);
  return done.future.timeout(const Duration(seconds: 10), onTimeout: () {});
}

void main() {
  setUpAll(() async {
    await loadAppFonts();
    final source = FakeSource();
    final root = Directory.systemTemp.createTempSync('sb-shots-');
    lockedFolder = '${root.path}/Locked';
    final v = Vault(lockedFolder, tempFolder: '${root.path}/tmp', deviceId: 'pc', deviceName: 'Desktop PC');
    await v.setUp('2468', 'PIN');
    final items = source.sample(count: 14).where((i) => !i.isVideo).toList();
    final family = await v.createAlbum('Family');
    await v.add([for (final i in items.take(9)) LockSource(item: i, file: (await source.file(i))!, thumbnail: () => source.thumbnailJpeg(i))], albumId: family.id);
    await v.createAlbum('Documents');
    final vp = await v.createAlbum('Very private');
    await v.protectAlbum(vp, '9999', 'PIN');
    v.lockAlbum(vp);
    final first = v.items.first;
    await v.setDetails(first, caption: 'Sunset from the pier', tags: ['Beach'], favorite: true);
    lockedKey = v.copyKey()!;
    await v.recordFailure('PIN');
    await v.recordFailure('PIN');
  });

  Future<void> shot(
    WidgetTester tester,
    String name, {
    String theme = 'dark',
    int tab = 0,
    bool locked = false,
    bool unlocked = false,
    UpdateInfo? update,
    Future<void> Function(BuildContext context, AppState app)? then,
  }) async {
    debugDisableShadows = false;
    EditableText.debugDeterministicCursor = true;
    try {
      tester.view.physicalSize = const Size(1170, 2532); // 390 × 844 at 3×
      tester.view.devicePixelRatio = 3;
      addTearDown(tester.view.reset);
      final source = FakeSource();
      final settings = Settings()..theme = theme;
      final lib = Library(source, settings);
      lib.setItems(source.sample());
      final a1 = lib.createAlbum('Road trip', lib.photos.skip(2).take(9));
      lib.setCover(a1, lib.photos[4]);
      lib.createAlbum('Family', lib.photos.skip(12).take(6));
      lib.createAlbum('Best of 2026', lib.photos.skip(1).take(14));
      lib.setFavorite(lib.photos.take(3), true);
      lib.addTag(lib.photos.first, 'Portland');
      lib.setCaption(lib.photos.first, 'First evening on the coast');
      final tmp = Directory.systemTemp.createTempSync('sb-shot-');
      final vault = Vault(locked ? lockedFolder : '${tmp.path}/Locked', tempFolder: '${tmp.path}/tmp');
      final updater = Updater(settings, client: MockClient((_) async => http.Response('[]', 200)));
      updater.available = update;
      final app = AppState(
        settings: settings,
        library: lib,
        vault: vault,
        updater: updater,
        defaultLockedFolder: '${tmp.path}/Locked',
        cacheFolder: tmp.path,
        autoLock: false,
      );
      await tester.runAsync(() async {
        if (unlocked) await vault.unlockWithKey(lockedKey);
        for (final i in [...lib.all, ...vault.items]) {
          await warm(app.thumbnail(i, 256));
        }
        for (final i in vault.items.take(2)) {
          await warm(app.full(i));
        }
      });
      await tester.pumpWidget(ShutterboxApp(app: app, home: HomeScreen(initialTab: tab)));
      await tester.pumpAndSettle();
      if (then != null) {
        await then(tester.element(find.byType(HomeScreen)), app);
        await tester.pumpAndSettle();
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 300)));
        await tester.pumpAndSettle();
      }
      await expectLater(find.byType(ShutterboxApp), matchesGoldenFile('screenshots/$name.png'));
      app.dispose();
    } finally {
      debugDisableShadows = true;
      EditableText.debugDeterministicCursor = false;
    }
  }

  void push(BuildContext context, Widget page) => unawaited(Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => page)));

  testWidgets('photos', (t) => shot(t, '01-photos'));
  testWidgets('selecting', (t) => shot(t, '02-photos-selecting', then: (c, app) async {
        await t.tap(find.byTooltip('Select'));
        await t.pumpAndSettle();
        for (final i in [0, 1, 3, 4]) {
          await t.tap(find.byType(ItemTile).at(i));
        }
      }));
  testWidgets('videos', (t) => shot(t, '03-videos', tab: 1));
  testWidgets('albums', (t) => shot(t, '04-albums', tab: 2));
  testWidgets('album', (t) => shot(t, '05-album', tab: 2, then: (c, app) async => push(c, CollectionScreen(kind: CollectionKind.album, album: app.library.albums.first))));
  testWidgets('viewer', (t) => shot(t, '06-viewer', then: (c, app) async => ViewerScreen.open(c, app.library.photos, app.library.photos.first).ignore()));
  testWidgets('details', (t) => shot(t, '07-details', then: (c, app) async {
        ViewerScreen.open(c, app.library.photos, app.library.photos.first).ignore();
        await t.pumpAndSettle();
        DetailsSheet.show(t.element(find.byType(ViewerScreen)), app.library.photos.first).ignore();
      }));
  testWidgets('search', (t) => shot(t, '08-search', then: (c, app) async => push(c, const SearchScreen(initial: 'canon'))));
  testWidgets('locked intro', (t) => shot(t, '09-locked-setup', tab: 3));
  testWidgets('locked', (t) => shot(t, '10-locked-unlock', tab: 3, locked: true));
  testWidgets('locked album', (t) => shot(t, '11-locked-album', tab: 3, locked: true, unlocked: true));
  testWidgets('lock menu', (t) => shot(t, '12-lock-menu', then: (c, app) async {
        await t.tap(find.byTooltip('Select'));
        await t.pumpAndSettle();
        await t.tap(find.byType(ItemTile).at(0));
        await t.tap(find.byType(ItemTile).at(2));
        await t.pumpAndSettle();
        await t.tap(find.text('Lock'));
      }));
  testWidgets('settings', (t) => shot(t, '13-settings', locked: true, then: (c, app) async => push(c, const SettingsScreen())));
  testWidgets('update', (t) => shot(t, '14-update-available',
      update: UpdateInfo('0.4.0', 'https://example/0.4.0.apk', 'Shutterbox-Android-0.4.0.apk', 31 * 1024 * 1024, '', Updater.releasesPage)));
  testWidgets('light', (t) => shot(t, '15-photos-light', theme: 'light'));
  testWidgets('import', (t) => shot(t, '16-import', then: (c, app) async => push(c, const ImportScreen())));
  testWidgets('folders', (t) => shot(t, '17-folders', tab: 2, then: (c, app) async => push(c, const FoldersScreen())));
  testWidgets('albums light', (t) => shot(t, '18-albums-light', tab: 2, theme: 'light'));
  testWidgets('wrong guesses', (t) => shot(t, '19-wrong-guesses', tab: 3, locked: true, unlocked: true, then: (c, app) async => push(c, const WrongGuessesScreen())));
  testWidgets('settings locked', (t) => shot(t, '20-settings-locked', locked: true, then: (c, app) async {
        push(c, const SettingsScreen());
        await t.pumpAndSettle();
        await t.scrollUntilVisible(find.text('Photograph wrong guesses'), 300, scrollable: find.byType(Scrollable).last);
      }));
}
