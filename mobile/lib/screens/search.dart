import 'package:flutter/material.dart';

import '../app.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'home.dart';
import 'viewer.dart';

/// Search names, folders, tags, captions, cameras, albums, months and years. Several words narrow it down.
class SearchScreen extends StatefulWidget {
  const SearchScreen({super.key, this.initial = ''});
  final String initial;

  @override
  State<SearchScreen> createState() => _SearchScreenState();
}

class _SearchScreenState extends State<SearchScreen> {
  late final _text = TextEditingController(text: widget.initial);
  final _sel = Selection();

  @override
  void dispose() {
    _text.dispose();
    _sel.dispose();
    super.dispose();
  }

  List<String> _suggestions() {
    final lib = AppScope.read(context).library;
    final out = <String>[];
    out.addAll(lib.albums.take(4).map((a) => a.name));
    final years = {for (final i in lib.visible) i.taken.year}.toList()..sort((a, b) => b.compareTo(a));
    out.addAll(years.take(3).map((y) => '$y'));
    final cameras = <String, int>{};
    for (final i in lib.visible) {
      if (i.camera.isNotEmpty) cameras[i.camera] = (cameras[i.camera] ?? 0) + 1;
    }
    out.addAll((cameras.entries.toList()..sort((a, b) => b.value.compareTo(a.value))).take(2).map((e) => e.key));
    final tags = <String>{for (final m in lib.meta.values) ...m.tags};
    out.addAll(tags.take(4));
    final people = AppScope.read(context).people;
    if (people != null) {
      bool shown(String id) => lib.byIdFast(id) != null;
      out.addAll(people.list(shown).where((v) => v.person.named).take(4).map((v) => v.person.name!));
      out.addAll(people.things(shown).take(6).map((t) => t.$1));
    }
    out.add('video');
    return out;
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final lib = app.library;
    final p = Palette.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([lib, _sel, _text]),
      builder: (context, _) {
        final q = _text.text.trim();
        final List<Item> results = q.isEmpty ? const [] : lib.search(q);
        return Scaffold(
          appBar: AppBar(
            titleSpacing: 0,
            title: TextField(
              controller: _text,
              autofocus: widget.initial.isEmpty,
              textInputAction: TextInputAction.search,
              onSubmitted: (q) async {
                // with the Locked tab hidden, its PIN or password typed here opens it
                if (!app.settings.hideLocked || !app.vault.isSetUp || q.isEmpty) return;
                if (await app.vault.unlock(q, log: false)) {
                  _text.clear();
                  app.showLocked.value++;
                }
              },
              decoration: InputDecoration(
                hintText: 'Search your photos and videos',
                filled: false,
                border: InputBorder.none,
                enabledBorder: InputBorder.none,
                focusedBorder: InputBorder.none,
                suffixIcon: q.isEmpty ? null : IconButton(icon: const Icon(Icons.clear), onPressed: _text.clear),
              ),
            ),
          ),
          body: q.isEmpty
              ? ListView(padding: const EdgeInsets.all(16), children: [
                  Text('Try', style: TextStyle(color: p.text3, fontWeight: FontWeight.w600)),
                  const SizedBox(height: 10),
                  Wrap(spacing: 8, runSpacing: 8, children: [
                    for (final s in _suggestions())
                      ActionChip(label: Text(s), onPressed: () => _text.text = s),
                  ]),
                  const SizedBox(height: 18),
                  Text('Search looks at file names, folders, tags, captions, cameras, albums, months, years, people's names and what's in each photo. Several words narrow it down, like "dog beach" or "canon october".',
                      style: TextStyle(color: p.text2, height: 1.4)),
                ])
              : MediaGrid(
                  items: results,
                  selection: _sel,
                  onOpen: (i, all) => ViewerScreen.open(context, all, i),
                  empty: EmptyState(icon: Icons.search_off, title: 'Nothing found', body: 'Nothing matches "$q".'),
                ),
          bottomNavigationBar: _sel.active ? SelectionBar(selection: _sel, all: results, actions: libraryActions(context, _sel, results)) : null,
        );
      },
    );
  }
}
