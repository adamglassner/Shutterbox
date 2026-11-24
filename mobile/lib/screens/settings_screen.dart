import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../app.dart';
import '../biometrics.dart';
import '../models.dart';
import '../theme.dart';
import '../updater.dart';
import '../vault/vault.dart';
import '../version.dart';
import '../widgets.dart';
import 'locked.dart';

class SettingsScreen extends StatefulWidget {
  const SettingsScreen({super.key});

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  bool? _bio;
  String? _bioName;

  @override
  void initState() {
    super.initState();
    _refreshBio();
  }

  Future<void> _refreshBio() async {
    final vault = AppScope.read(context).vault;
    final has = await Biometrics.hasKey(vault.identity);
    final name = await Biometrics.available();
    if (mounted) {
      setState(() {
        _bio = has;
        _bioName = name;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final s = app.settings;
    final p = Palette.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([s, app.vault, app.updater, app.library]),
      builder: (context, _) => Scaffold(
        appBar: AppBar(title: const Text('Settings')),
        body: ListView(padding: const EdgeInsets.only(bottom: 40), children: [
          const SectionTitle('Updates'),
          _UpdatesCard(updater: app.updater),
          const SectionTitle('Looks'),
          _Labeled(
            'Theme',
            SegmentedButton<String>(
              segments: const [
                ButtonSegment(value: 'dark', label: Text('Dark')),
                ButtonSegment(value: 'light', label: Text('Light')),
                ButtonSegment(value: 'system', label: Text('System')),
              ],
              selected: {s.theme},
              onSelectionChanged: (v) => s.update((x) => x.theme = v.first),
            ),
          ),
          _Labeled(
            'Accent',
            Wrap(spacing: 12, children: [
              for (final a in accents)
                Tooltip(
                  message: a.$1,
                  child: InkWell(
                    customBorder: const CircleBorder(),
                    onTap: () => s.update((x) => x.accent = a.$1),
                    child: Container(
                      width: 38,
                      height: 38,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: Theme.of(context).brightness == Brightness.dark ? a.$3 : a.$2,
                        border: Border.all(color: s.accent == a.$1 ? p.text : Colors.transparent, width: 3),
                      ),
                    ),
                  ),
                ),
            ]),
          ),
          _Labeled(
            'Thumbnails',
            SegmentedButton<String>(
              segments: const [
                ButtonSegment(value: 'small', label: Text('Small')),
                ButtonSegment(value: 'medium', label: Text('Medium')),
                ButtonSegment(value: 'large', label: Text('Large')),
              ],
              selected: {s.density},
              onSelectionChanged: (v) => s.update((x) => x.density = v.first),
            ),
          ),
          _Labeled(
            'Group the timeline by',
            SegmentedButton<String>(
              segments: const [
                ButtonSegment(value: 'day', label: Text('Day')),
                ButtonSegment(value: 'month', label: Text('Month')),
                ButtonSegment(value: 'year', label: Text('Year')),
              ],
              selected: {s.groupBy},
              onSelectionChanged: (v) => s.update((x) => x.groupBy = v.first),
            ),
          ),
          _Labeled(
            'Slideshow: ${s.slideshowSeconds} seconds a photo',
            Slider(value: s.slideshowSeconds.toDouble(), min: 2, max: 15, divisions: 13, onChanged: (v) => s.update((x) => x.slideshowSeconds = v.round())),
          ),
          const SectionTitle('Library'),
          ListTile(
            leading: const Icon(Icons.folder_outlined),
            title: const Text('Folders'),
            subtitle: Text(s.folders == null ? 'All folders with photos and videos' : '${s.folders!.length} folders chosen'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const LibraryFoldersScreen())),
          ),
          ListTile(
            leading: const Icon(Icons.refresh),
            title: const Text('Look for new photos and videos'),
            subtitle: Text('${app.library.all.length} in your library'),
            onTap: app.library.scan,
          ),
          ListTile(
            leading: const Icon(Icons.cleaning_services_outlined),
            title: const Text('Clear the thumbnail cache'),
            subtitle: const Text('They\'re made again as you scroll'),
            onTap: () async {
              await app.source.clearCache();
              PaintingBinding.instance.imageCache.clear();
              PaintingBinding.instance.imageCache.clearLiveImages();
              if (context.mounted) toast(context, 'Thumbnail cache cleared');
            },
          ),
          const SectionTitle('People and things'),
          SwitchListTile(
            secondary: const Icon(Icons.face_outlined),
            title: const Text('Find people and things in my photos'),
            subtitle: const Text('Faces are grouped in People and you can search for what\'s in a photo ("dog", "beach"). It all happens on your phone while Shutterbox is open; nothing is uploaded. Locked album items are never looked at.'),
            value: s.findPeople,
            onChanged: (v) => s.update((x) => x.findPeople = v),
          ),
          if (app.people != null)
            ListenableBuilder(
              listenable: Listenable.merge([app.people!, if (app.analyzer != null) app.analyzer!]),
              builder: (context, _) => ListTile(
                leading: const Icon(Icons.restart_alt),
                title: const Text('Forget all people'),
                subtitle: Text(app.analyzer != null && app.analyzer!.total > 0 && app.analyzer!.done < app.analyzer!.total
                    ? 'Looked through ${app.analyzer!.done} of ${app.analyzer!.total} photos so far · ${app.people!.faces.length} faces'
                    : 'Looked through ${app.people!.photos.length} photos · ${app.people!.faces.length} faces'),
                onTap: () async {
                  if (!await confirm(context, 'Forget all people?', 'Every face and person goes, including the names you gave them. Shutterbox looks through your photos again and groups faces from scratch. Your photos aren\'t touched.', action: 'Forget', danger: true)) return;
                  app.people!.forgetPeople();
                  await app.people!.save();
                  app.analyzer?.poke();
                },
              ),
            ),
          const SectionTitle('Locked album'),
          ..._locked(context),
          const SectionTitle('About'),
          ListTile(
            leading: const Icon(Icons.info_outline),
            title: Text('Shutterbox $appVersion for Android'),
            subtitle: const Text('© 2026 Bytehawk Studio. Your photos stay on your phone.'),
            onTap: () => launchUrl(Uri.parse('https://github.com/${Updater.repo}'), mode: LaunchMode.externalApplication),
          ),
        ]),
      ),
    );
  }

  List<Widget> _locked(BuildContext context) {
    final app = AppScope.of(context);
    final vault = app.vault;
    final p = Palette.of(context);
    final info = vault.info;
    if (info == null) {
      return [
        ListTile(
          leading: const Icon(Icons.lock_outline),
          title: const Text('Set up the Locked album'),
          subtitle: const Text('An encrypted album for private photos and videos'),
          onTap: () => Navigator.of(context).push(MaterialPageRoute<bool>(builder: (_) => const LockSetupScreen())),
        ),
        ListTile(
          leading: const Icon(Icons.folder_open_outlined),
          title: const Text('Open a Locked album from a PC or another phone'),
          subtitle: const Text('Choose its "Shutterbox Locked album" folder'),
          onTap: () => LockedActions.openOther(context),
        ),
      ];
    }
    final unlocked = vault.isUnlocked;
    Future<void> needsUnlock(Future<void> Function() then) async {
      if (await LockedGate.ensureUnlocked(context)) await then();
    }

    return [
      ListTile(
        leading: Icon(unlocked ? Icons.lock_open : Icons.lock_outline, color: p.accent),
        title: Text(unlocked ? 'Unlocked: ${vault.count} items' : 'Locked'),
        subtitle: Text('Opens with a ${info.kind == 'Password' ? 'password' : 'PIN'}${_bio == true ? ' or your fingerprint or face' : ''}\n${friendlyPath(vault.folder)}'),
        isThreeLine: true,
        trailing: unlocked ? TextButton(onPressed: vault.lock, child: const Text('Lock now')) : null,
      ),
      ListTile(
        leading: const Icon(Icons.password),
        title: Text('Change ${info.kind == 'Password' ? 'password' : 'PIN'}'),
        subtitle: const Text('Or switch between a PIN and a password'),
        onTap: () => needsUnlock(() async {
          if (!context.mounted) return;
          final r = await showDialog<(String, String)>(context: context, builder: (_) => _ChangeSecretDialog(kind: info.kind));
          if (r == null) return;
          await vault.changeSecret(r.$1, r.$2);
          if (context.mounted) toast(context, r.$2 == 'PIN' ? 'PIN changed' : 'Password changed');
        }),
      ),
      if (_bioName != null)
        SwitchListTile(
          secondary: const Icon(Icons.fingerprint),
          title: Text('Unlock with ${_bioName!.toLowerCase()}'),
          subtitle: const Text('On this phone only'),
          value: _bio == true,
          onChanged: (on) async {
            if (on) {
              await needsUnlock(() async {
                if (context.mounted) await LockedActions.turnOnBiometrics(context);
              });
            } else {
              await Biometrics.forget(vault.identity);
            }
            await _refreshBio();
          },
        ),
      ListTile(
        leading: const Icon(Icons.timer_outlined),
        title: const Text('Lock automatically'),
        subtitle: Text(info.autoLockMinutes == 0 ? 'Only when I lock it or close Shutterbox' : 'After ${info.autoLockMinutes} ${info.autoLockMinutes == 1 ? 'minute' : 'minutes'} without use'),
        trailing: DropdownButton<int>(
          value: const [0, 1, 5, 15, 30].contains(info.autoLockMinutes) ? info.autoLockMinutes : 5,
          underline: const SizedBox(),
          items: const [
            DropdownMenuItem(value: 1, child: Text('1 min')),
            DropdownMenuItem(value: 5, child: Text('5 min')),
            DropdownMenuItem(value: 15, child: Text('15 min')),
            DropdownMenuItem(value: 30, child: Text('30 min')),
            DropdownMenuItem(value: 0, child: Text('Never')),
          ],
          onChanged: (v) => vault.setAutoLock(v ?? 5),
        ),
      ),
      SwitchListTile(
        secondary: const Icon(Icons.screenshot_monitor_outlined),
        title: const Text('Keep it out of screenshots'),
        subtitle: const Text('While it\'s unlocked, screenshots and screen recordings come out black and the recent-apps preview is blank'),
        value: app.settings.protectScreen,
        onChanged: (v) => app.settings.update((x) => x.protectScreen = v),
      ),
      SwitchListTile(
        secondary: const Icon(Icons.exit_to_app),
        title: const Text('Lock as soon as I leave Shutterbox'),
        subtitle: const Text('When you switch apps or the screen turns off'),
        value: app.settings.lockOnLeave,
        onChanged: (v) => app.settings.update((x) => x.lockOnLeave = v),
      ),
      SwitchListTile(
        secondary: const Icon(Icons.flip_to_back),
        title: const Text('Lock when I put the phone face down'),
        subtitle: const Text('And go back to Photos. The lock button in the Locked album does the same.'),
        value: app.settings.lockFaceDown,
        onChanged: (v) => app.settings.update((x) => x.lockFaceDown = v),
      ),
      SwitchListTile(
        secondary: const Icon(Icons.visibility_off_outlined),
        title: const Text('Hide the Locked tab'),
        subtitle: const Text('Open it by pressing and holding "Photos" at the top, or by typing your PIN or password in search'),
        value: app.settings.hideLocked,
        onChanged: (v) => app.settings.update((x) => x.hideLocked = v),
      ),
      SwitchListTile(
        secondary: const Icon(Icons.no_photography_outlined),
        title: const Text('Photograph wrong guesses'),
        subtitle: const Text('The front camera takes a photo after a wrong PIN or password. It\'s encrypted: only someone who can unlock the album can see it.'),
        value: vault.photographsWrongGuesses,
        onChanged: (on) => needsUnlock(() async {
          if (on) {
            if (!context.mounted) return;
            await withProgress(context, 'Making the key for the photos', (_) => vault.enableIntruderPhotos());
          } else {
            await vault.disableIntruderPhotos();
          }
        }),
      ),
      ListTile(
        leading: const Icon(Icons.gpp_maybe_outlined),
        title: const Text('Wrong guesses'),
        subtitle: Text(_guessSummary(vault)),
        trailing: const Icon(Icons.chevron_right),
        onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const WrongGuessesScreen())),
      ),
      ListTile(
        leading: const Icon(Icons.fact_check_outlined),
        title: const Text('Check the album'),
        subtitle: const Text('Makes sure every file is there and decrypts (after copying or syncing it)'),
        onTap: () => needsUnlock(() async {
          if (!context.mounted) return;
          final r = await withProgress(context, 'Checking the Locked album', (report) => vault.check(progress: report));
          if (!context.mounted) return;
          await showDialog<void>(
            context: context,
            builder: (ctx) => AlertDialog(
              title: Text(r.problems.isEmpty ? 'All good' : '${r.problems.length} ${r.problems.length == 1 ? 'problem' : 'problems'}'),
              content: SingleChildScrollView(
                child: Text(r.problems.isEmpty ? 'All ${r.checked} items are there and decrypt correctly.' : '${r.checked} items are fine.\n\n${r.problems.join('\n')}'),
              ),
              actions: [FilledButton(onPressed: () => Navigator.pop(ctx), child: const Text('OK'))],
            ),
          );
        }),
      ),
      ListTile(
        leading: const Icon(Icons.key_outlined),
        title: const Text('Get a new recovery key'),
        subtitle: const Text('The old one stops working'),
        onTap: () => needsUnlock(() async {
          if (!context.mounted) return;
          if (!await confirm(context, 'Get a new recovery key?', 'Your current recovery key will stop working. Save the new one somewhere safe.', action: 'Continue')) return;
          final key = await vault.newRecoveryKey();
          if (!context.mounted) return;
          await showDialog<void>(
            context: context,
            barrierDismissible: false,
            builder: (ctx) => AlertDialog(
              title: const Text('Your new recovery key'),
              content: Column(mainAxisSize: MainAxisSize.min, children: [
                const Text('It\'s shown only now. Write it down or keep it in your password manager.'),
                const SizedBox(height: 14),
                SelectableText(key, style: const TextStyle(fontSize: 18, letterSpacing: 1.2, fontWeight: FontWeight.w600, fontFamily: 'monospace')),
              ]),
              actions: [
                TextButton(onPressed: () => copyText(ctx, key, 'Recovery key'), child: const Text('Copy')),
                FilledButton(onPressed: () => Navigator.pop(ctx), child: const Text('I\'ve saved it')),
              ],
            ),
          );
        }),
      ),
      ListTile(
        leading: const Icon(Icons.drive_file_move_outline),
        title: const Text('Move to another folder'),
        subtitle: const Text('For example a folder your cloud app syncs, or an SD card. To use it on your PC, copy the folder there.'),
        onTap: () => LockedActions.moveTo(context),
      ),
      ListTile(
        leading: const Icon(Icons.folder_open_outlined),
        title: const Text('Use a Locked album from a PC or another phone'),
        subtitle: const Text('Choose its "Shutterbox Locked album" folder'),
        onTap: () => LockedActions.openOther(context),
      ),
      if (vault.folder != app.defaultLockedFolder && Vault.isVaultFolder(app.defaultLockedFolder))
        ListTile(
          leading: const Icon(Icons.undo),
          title: const Text('Go back to this phone\'s own Locked album'),
          onTap: () async {
            final e = await vault.openFolder(app.defaultLockedFolder);
            if (context.mounted) toast(context, e ?? 'Using this phone\'s own Locked album');
          },
        ),
      ListTile(
        leading: Icon(Icons.delete_forever_outlined, color: p.danger),
        title: Text('Delete the Locked album', style: TextStyle(color: p.danger)),
        subtitle: const Text('Everything in it is deleted for good'),
        onTap: () => needsUnlock(() async {
          if (!context.mounted) return;
          if (!await confirm(context, 'Delete the Locked album?', 'All ${vault.count} items in it are deleted for good. Restore anything you want to keep first.',
              action: 'Delete everything', danger: true)) {
            return;
          }
          await Biometrics.forget(vault.identity);
          await vault.deleteAll();
          if (vault.folder != app.defaultLockedFolder) await vault.openFolder(app.defaultLockedFolder);
          if (context.mounted) toast(context, 'The Locked album was deleted');
        }),
      ),
    ];
  }
}

String _guessSummary(Vault vault) {
  final a = vault.failedAttempts;
  if (a.isEmpty) return 'None so far';
  return '${a.length} logged, the last ${formatDay(a.first.time)}, ${formatTime(a.first.time)}';
}

class _Labeled extends StatelessWidget {
  const _Labeled(this.label, this.child);
  final String label;
  final Widget child;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 6, 16, 10),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(label, style: TextStyle(color: Palette.of(context).text2)),
          const SizedBox(height: 8),
          child,
        ]),
      );
}

class _UpdatesCard extends StatelessWidget {
  const _UpdatesCard({required this.updater});
  final Updater updater;

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final u = updater.available;
    final last = updater.lastChecked ?? DateTime.tryParse(AppScope.of(context).settings.lastUpdateCheck);
    final String status;
    if (updater.checking) {
      status = 'Checking GitHub…';
    } else if (updater.progress != null) {
      status = 'Downloading ${u?.version}… ${(updater.progress! * 100).round()}%';
    } else if (u != null) {
      status = 'Shutterbox ${u.version} is available${u.size > 0 ? ' (${formatSize(u.size)})' : ''}';
    } else if (updater.problem != null) {
      status = updater.problem!;
    } else {
      status = last == null ? 'Shutterbox $appVersion' : 'You have the latest version ($appVersion). Checked ${formatDateTime(last.toLocal())}.';
    }
    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
            Icon(u != null ? Icons.system_update : Icons.verified_outlined, color: p.accent),
            const SizedBox(width: 12),
            Expanded(child: Text(status, style: TextStyle(color: p.text, fontWeight: FontWeight.w500))),
          ]),
          if (updater.progress != null) ...[const SizedBox(height: 12), LinearProgressIndicator(value: updater.progress)],
          if (u != null && u.notes.trim().isNotEmpty) ...[
            const SizedBox(height: 10),
            Text(u.notes.trim(), maxLines: 6, overflow: TextOverflow.ellipsis, style: TextStyle(color: p.text2, fontSize: 13, height: 1.35)),
          ],
          const SizedBox(height: 12),
          Wrap(spacing: 8, runSpacing: 8, children: [
            if (u != null)
              FilledButton.icon(
                onPressed: updater.progress != null
                    ? null
                    : () async {
                        final e = await updater.install();
                        if (e != null && context.mounted) toast(context, e);
                      },
                icon: const Icon(Icons.download),
                label: Text('Update to ${u.version}'),
              )
            else
              FilledButton.tonalIcon(
                onPressed: updater.checking ? null : () => updater.check(force: true),
                icon: const Icon(Icons.refresh),
                label: const Text('Check for updates'),
              ),
            TextButton(
              onPressed: () => launchUrl(Uri.parse(Updater.releasesPage), mode: LaunchMode.externalApplication),
              child: const Text('Release notes'),
            ),
          ]),
          const SizedBox(height: 4),
          Text('Updates come straight from Shutterbox\'s GitHub releases. Android asks you to confirm the install.', style: TextStyle(color: p.text3, fontSize: 12.5)),
        ]),
      ),
    );
  }
}

class _ChangeSecretDialog extends StatefulWidget {
  const _ChangeSecretDialog({required this.kind});
  final String kind;

  @override
  State<_ChangeSecretDialog> createState() => _ChangeSecretDialogState();
}

class _ChangeSecretDialogState extends State<_ChangeSecretDialog> {
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
        title: const Text('New PIN or password'),
        content: Column(mainAxisSize: MainAxisSize.min, children: [
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
            decoration: InputDecoration(labelText: _kind == 'PIN' ? 'New PIN (4 to 12 digits)' : 'New password (6 or more)'),
          ),
          const SizedBox(height: 10),
          TextField(
            controller: _b,
            obscureText: true,
            keyboardType: _kind == 'PIN' ? TextInputType.number : TextInputType.visiblePassword,
            decoration: InputDecoration(labelText: 'Type it again', errorText: _error),
          ),
        ]),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
          FilledButton(
            onPressed: () {
              final problem = Vault.validate(_a.text, _kind);
              if (problem != null) return setState(() => _error = problem);
              if (_a.text != _b.text) return setState(() => _error = 'The two don\'t match.');
              Navigator.pop(context, (_a.text, _kind));
            },
            child: const Text('Change'),
          ),
        ],
      );
}

/// Which of the phone's folders Shutterbox shows.
class LibraryFoldersScreen extends StatefulWidget {
  const LibraryFoldersScreen({super.key});

  @override
  State<LibraryFoldersScreen> createState() => _LibraryFoldersScreenState();
}

class _LibraryFoldersScreenState extends State<LibraryFoldersScreen> {
  List<DeviceFolder>? _folders;
  Set<String>? _chosen;

  @override
  void initState() {
    super.initState();
    final app = AppScope.read(context);
    _chosen = app.settings.folders?.toSet();
    app.source.folders().then((f) {
      if (mounted) setState(() => _folders = f);
    });
  }

  void _save() {
    final app = AppScope.read(context);
    final all = _folders?.map((f) => f.id).toSet() ?? {};
    final chosen = _chosen;
    app.settings.update((s) => s.folders = chosen == null || chosen.containsAll(all) ? null : chosen.toList());
    app.library.scan();
  }

  @override
  Widget build(BuildContext context) {
    final f = _folders;
    final p = Palette.of(context);
    return PopScope(
      onPopInvokedWithResult: (didPop, _) {
        if (didPop) _save();
      },
      child: Scaffold(
        appBar: AppBar(title: const Text('Folders')),
        body: f == null
            ? const Center(child: CircularProgressIndicator())
            : ListView(children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                  child: Text('Shutterbox shows the photos and videos in these folders. Unchecking one only hides it here; nothing is deleted.', style: TextStyle(color: p.text2)),
                ),
                SwitchListTile(
                  title: const Text('All folders'),
                  subtitle: const Text('Including new ones'),
                  value: _chosen == null,
                  onChanged: (v) => setState(() => _chosen = v ? null : {for (final x in f) x.id}),
                ),
                const Divider(),
                for (final x in f)
                  CheckboxListTile(
                    title: Text(x.name),
                    subtitle: Text('${x.count} items'),
                    value: _chosen == null || _chosen!.contains(x.id),
                    onChanged: (v) => setState(() {
                      _chosen ??= {for (final y in f) y.id};
                      v == true ? _chosen!.add(x.id) : _chosen!.remove(x.id);
                    }),
                  ),
              ]),
      ),
    );
  }
}
