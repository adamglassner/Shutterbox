import 'package:flutter/material.dart';

import '../app.dart';
import '../models.dart';
import '../theme.dart';
import '../vision/people.dart';
import '../widgets.dart';
import 'albums.dart';
import 'search.dart';

/// A round crop of a face, from the photo's thumbnail.
class FaceAvatar extends StatelessWidget {
  const FaceAvatar({super.key, required this.item, required this.face, this.size = 96});
  final Item? item;
  final FaceInfo face;
  final double size;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    final i = item;
    if (i == null) return CircleAvatar(radius: size / 2, backgroundColor: p.tile, child: Icon(Icons.person_outline, color: p.text3));
    // the face's box is a fraction of the upright picture; show the picture so that a square around the face fills the circle
    final turned = (i.rotation ~/ 90).isOdd;
    final pw = (turned ? i.height : i.width).toDouble(), ph = (turned ? i.width : i.height).toDouble();
    final aspect = (pw <= 0 || ph <= 0) ? 1.0 : pw / ph; // width / height
    final b = face.box;
    final side = (b[2] * aspect > b[3] ? b[2] * aspect : b[3]) * 1.45; // in picture heights
    final sideW = side / aspect, sideH = side; // as fractions of the picture's width and height
    final cx = b[0] + b[2] / 2, cy = b[1] + b[3] / 2;
    final w = size / sideW, h = size / sideH;
    return ClipOval(
      child: Container(
        width: size,
        height: size,
        color: p.tile,
        child: OverflowBox(
          alignment: Alignment.topLeft,
          minWidth: w,
          maxWidth: w,
          minHeight: h,
          maxHeight: h,
          child: Transform.translate(
            offset: Offset(-(cx - sideW / 2) * w, -(cy - sideH / 2) * h),
            child: SizedBox(
              width: w,
              height: h,
              child: RotatedBox(
                quarterTurns: (i.rotation ~/ 90) % 4,
                child: Image(image: app.thumbnail(i, 512), fit: BoxFit.fill, errorBuilder: (_, __, ___) => const SizedBox()),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// People found in your photos, and the things Shutterbox recognised (as search suggestions).
class PeopleScreen extends StatefulWidget {
  const PeopleScreen({super.key});

  @override
  State<PeopleScreen> createState() => _PeopleScreenState();
}

class _PeopleScreenState extends State<PeopleScreen> {
  bool _showHidden = false;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final people = app.people;
    final analyzer = app.analyzer;
    final p = Palette.of(context);
    if (people == null) return Scaffold(appBar: AppBar(title: const Text('People')));
    return ListenableBuilder(
      listenable: Listenable.merge([people, app.library, if (analyzer != null) analyzer, app.settings]),
      builder: (context, _) {
        bool shown(String id) {
          final i = app.library.byIdFast(id);
          return i != null && !app.library.isTrashed(i);
        }

        final list = people.list(shown, includeHidden: _showHidden);
        final anyHidden = _showHidden || people.list(shown, includeHidden: true).any((v) => v.person.hidden);
        final things = people.things(shown);
        String status;
        Widget? turnOn;
        if (!app.settings.findPeople) {
          status = 'Finding people and things is turned off.';
          turnOn = FilledButton(onPressed: () => app.settings.update((s) => s.findPeople = true), child: const Text('Turn on'));
        } else if (analyzer?.error != null) {
          status = 'Shutterbox couldn\'t look through your photos: ${analyzer!.error}';
        } else if (analyzer != null && analyzer.total > 0 && analyzer.done < analyzer.total) {
          status = 'Looking through your photos: ${analyzer.done} of ${analyzer.total}. People and things appear as they\'re found (while Shutterbox is open).';
        } else {
          status = 'Found on your phone; nothing is uploaded. Tap a person to see their photos and give them a name.';
        }
        return Scaffold(
          appBar: AppBar(title: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Text('People'),
            Text(list.isEmpty ? 'Faces in your photos, grouped by who they are' : '${list.length} ${list.length == 1 ? 'person' : 'people'}',
                style: TextStyle(fontSize: 12.5, color: p.text3, fontWeight: FontWeight.w400)),
          ])),
          body: LayoutBuilder(builder: (context, c) {
            final cols = (c.maxWidth / 116).floor().clamp(3, 8);
            return CustomScrollView(slivers: [
              SliverPadding(
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
                sliver: SliverToBoxAdapter(
                  child: Row(children: [
                    Expanded(child: Text(status, style: TextStyle(color: p.text2, height: 1.35))),
                    if (turnOn != null) ...[const SizedBox(width: 12), turnOn],
                  ]),
                ),
              ),
              if (list.isEmpty)
                SliverToBoxAdapter(
                  child: EmptyState(
                    icon: Icons.face_outlined,
                    title: 'No people yet',
                    body: (analyzer?.finished ?? true) ? 'Someone shows up here once they\'re in two photos or more.' : 'Still looking. Someone shows up here once they\'re in two photos or more.',
                  ),
                )
              else
                SliverPadding(
                  padding: const EdgeInsets.symmetric(horizontal: 12),
                  sliver: SliverGrid.count(
                    crossAxisCount: cols,
                    mainAxisSpacing: 8,
                    crossAxisSpacing: 8,
                    childAspectRatio: 0.74,
                    children: [for (final v in list) _PersonTile(view: v)],
                  ),
                ),
              if (anyHidden)
                SliverToBoxAdapter(
                  child: Align(
                    alignment: Alignment.centerLeft,
                    child: Padding(
                      padding: const EdgeInsets.only(left: 8),
                      child: TextButton(onPressed: () => setState(() => _showHidden = !_showHidden), child: Text(_showHidden ? 'Don\'t show hidden people' : 'Show hidden people')),
                    ),
                  ),
                ),
              if (things.isNotEmpty) ...[
                const SliverToBoxAdapter(child: SectionTitle('Things')),
                SliverPadding(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                  sliver: SliverToBoxAdapter(
                    child: Text('What Shutterbox recognised in your photos. Tap one to search for it, or search for anything (beach, dog, cake…).',
                        style: TextStyle(color: p.text2, height: 1.35)),
                  ),
                ),
                SliverPadding(
                  padding: const EdgeInsets.fromLTRB(16, 4, 16, 32),
                  sliver: SliverToBoxAdapter(
                    child: Wrap(spacing: 8, runSpacing: 8, children: [
                      for (final (word, count) in things)
                        ActionChip(
                          label: Text('${word[0].toUpperCase()}${word.substring(1)} · $count'),
                          onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => SearchScreen(initial: word))),
                        ),
                    ]),
                  ),
                ),
              ],
            ]);
          }),
        );
      },
    );
  }
}

class _PersonTile extends StatelessWidget {
  const _PersonTile({required this.view});
  final PersonView view;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    final named = view.person.named;
    return InkWell(
      borderRadius: BorderRadius.circular(12),
      onTap: () => CollectionScreen.open(context, CollectionKind.person, personId: view.person.id),
      onLongPress: () => personMenu(context, view.person.id),
      child: LayoutBuilder(builder: (context, c) {
        final size = c.maxWidth - 12;
        return Column(children: [
          const SizedBox(height: 6),
          Opacity(opacity: view.person.hidden ? 0.45 : 1, child: FaceAvatar(item: app.library.byIdFast(view.cover.photo), face: view.cover, size: size)),
          const SizedBox(height: 6),
          Text(named ? view.person.name! : 'Add a name',
              maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontWeight: FontWeight.w600, color: named ? p.text : p.accent)),
          Text('${view.photoIds.length} ${view.photoIds.length == 1 ? 'photo' : 'photos'}', style: TextStyle(fontSize: 12, color: p.text3)),
        ]);
      }),
    );
  }
}

/// Names a person (merging with someone who already has that name, after asking). Returns the person kept.
Future<String?> renamePerson(BuildContext context, String personId) async {
  final app = AppScope.read(context);
  final people = app.people!;
  final p = people.person(personId);
  if (p == null) return null;
  final name = await askText(context, p.named ? 'Rename' : 'Add a name', initial: p.name ?? '', hint: 'Name', action: 'Save');
  if (name == null || !context.mounted) return null;
  final other = people.findByName(name);
  if (other != null && other.id != personId) {
    if (!await confirm(context, 'Same person?', '"${other.name}" is already someone. Put these photos together with theirs?', action: 'Put together')) return null;
  }
  final kept = people.rename(personId, name);
  await people.save();
  return kept?.id;
}

/// Rename, same person as…, hide.
Future<void> personMenu(BuildContext context, String personId, {void Function(String keptId)? onMoved, Item? selectedForCover}) async {
  final app = AppScope.read(context);
  final people = app.people!;
  final p = people.person(personId);
  if (p == null) return;
  final choice = await showModalBottomSheet<String>(
    context: context,
    showDragHandle: true,
    builder: (ctx) => SafeArea(
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        ListTile(leading: const Icon(Icons.edit_outlined), title: Text(p.named ? 'Rename' : 'Add a name'), onTap: () => Navigator.pop(ctx, 'rename')),
        ListTile(leading: const Icon(Icons.merge_type), title: const Text('Same person as…'), onTap: () => Navigator.pop(ctx, 'merge')),
        if (selectedForCover != null)
          ListTile(leading: const Icon(Icons.face_retouching_natural), title: const Text('Use the selected photo for their face'), onTap: () => Navigator.pop(ctx, 'cover')),
        ListTile(leading: Icon(p.hidden ? Icons.visibility_outlined : Icons.visibility_off_outlined), title: Text(p.hidden ? 'Show in People' : 'Hide from People'), onTap: () => Navigator.pop(ctx, 'hide')),
      ]),
    ),
  );
  if (!context.mounted || choice == null) return;
  switch (choice) {
    case 'rename':
      final kept = await renamePerson(context, personId);
      if (kept != null && kept != personId) onMoved?.call(kept);
    case 'merge':
      bool shown(String id) => app.library.byIdFast(id) != null;
      final others = people.list(shown, includeHidden: true).where((v) => v.person.id != personId && v.person.named).toList();
      if (others.isEmpty) {
        toast(context, 'Give someone else a name first, then you can put them together.');
        return;
      }
      final into = await showModalBottomSheet<String>(
        context: context,
        showDragHandle: true,
        builder: (ctx) => SafeArea(
          child: ListView(shrinkWrap: true, children: [
            for (final o in others)
              ListTile(
                leading: FaceAvatar(item: app.library.byIdFast(o.cover.photo), face: o.cover, size: 40),
                title: Text(o.person.name!),
                subtitle: Text('${o.photoIds.length} photos'),
                onTap: () => Navigator.pop(ctx, o.person.id),
              ),
          ]),
        ),
      );
      if (into == null) return;
      people.merge(personId, into);
      await people.save();
      onMoved?.call(into);
    case 'cover':
      people.setCover(personId, selectedForCover!.id);
      await people.save();
      if (context.mounted) toast(context, 'Their face now comes from that photo.');
    case 'hide':
      people.setHidden(personId, !p.hidden);
      await people.save();
  }
}
