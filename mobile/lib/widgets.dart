import 'dart:async';
import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'app.dart';
import 'library.dart';
import 'models.dart';
import 'theme.dart';

/// What's selected. Select mode makes every tap add or remove; it's also on while anything is selected.
class Selection extends ChangeNotifier {
  final ids = <String>{};
  bool _mode = false;

  bool get mode => _mode;
  set mode(bool v) {
    _mode = v;
    if (!v) ids.clear();
    notifyListeners();
  }

  bool get active => _mode || ids.isNotEmpty;
  int get count => ids.length;
  bool contains(Item i) => ids.contains(i.id);

  void toggle(Item i) {
    if (!ids.remove(i.id)) ids.add(i.id);
    notifyListeners();
  }

  void set(Iterable<Item> items, bool selected) {
    for (final i in items) {
      selected ? ids.add(i.id) : ids.remove(i.id);
    }
    notifyListeners();
  }

  void replace(Set<String> next) {
    ids
      ..clear()
      ..addAll(next);
    notifyListeners();
  }

  void clear() {
    _mode = false;
    ids.clear();
    notifyListeners();
  }

  List<Item> of(List<Item> items) => items.where((i) => ids.contains(i.id)).toList();
}

int columnsFor(String density, double width) {
  final base = switch (density) { 'small' => 5, 'large' => 3, _ => 4 };
  return width > 700 ? (base * width / 420).round() : base;
}

class _Row {
  _Row.header(this.group, this.top, this.height)
      : items = const [],
        firstIndex = 0;
  _Row.tiles(this.items, this.firstIndex, this.top, this.height) : group = null;
  final Group? group;
  final List<Item> items;
  final int firstIndex;
  final double top, height;
}

/// The timeline grid: headings for each day, month or year, tiles with check circles, and easy selecting:
/// tap a check circle, long-press and drag across tiles, or Select on a heading.
class MediaGrid extends StatefulWidget {
  const MediaGrid({
    super.key,
    required this.items,
    required this.selection,
    required this.onOpen,
    this.groupBy,
    this.empty,
    this.bottomPadding = 96,
    this.header,
    this.headerHeight = 0,
  });

  final List<Item> items;
  final Selection selection;

  /// Overrides the setting ("none" for a flat grid).
  final String? groupBy;
  final void Function(Item item, List<Item> all) onOpen;
  final Widget? empty;
  final double bottomPadding;

  /// Shown above the grid, scrolling with it ([headerHeight] tall).
  final Widget? header;
  final double headerHeight;

  @override
  State<MediaGrid> createState() => _MediaGridState();
}

class _MediaGridState extends State<MediaGrid> {
  final _scroll = ScrollController();
  List<_Row> _rows = const [];
  List<Item> _flat = const [];
  int _columns = 4;
  double _tile = 100;
  static const _gap = 2.0;

  // drag-selecting
  int? _anchor;
  bool _adding = true;
  Set<String> _before = const {};
  Offset? _lastLocal;
  Timer? _autoScroll;
  double _viewport = 0;

  @override
  void dispose() {
    _autoScroll?.cancel();
    _scroll.dispose();
    super.dispose();
  }

  void _layout(double width, String groupBy) {
    final app = AppScope.read(context);
    _columns = columnsFor(app.settings.density, width);
    _tile = (width - _gap * (_columns - 1)) / _columns;
    final rows = <_Row>[];
    var y = widget.headerHeight;
    var index = 0;
    final groups = groupBy == 'none' ? [Group('all', '', '', widget.items)] : groupItems(widget.items, groupBy);
    final flat = <Item>[];
    for (final g in groups) {
      if (groupBy != 'none') {
        const h = 58.0;
        rows.add(_Row.header(g, y, h));
        y += h;
      }
      for (var i = 0; i < g.items.length; i += _columns) {
        final slice = g.items.sublist(i, min(i + _columns, g.items.length));
        rows.add(_Row.tiles(slice, index, y, _tile + _gap));
        y += _tile + _gap;
        index += slice.length;
        flat.addAll(slice);
      }
    }
    _rows = rows;
    _flat = flat;
  }

  /// The index (in [_flat]) of the tile under a point in the list, or null.
  int? _hit(Offset local) {
    final y = local.dy + _scroll.offset;
    var lo = 0, hi = _rows.length - 1;
    while (lo <= hi) {
      final mid = (lo + hi) >> 1;
      final r = _rows[mid];
      if (y < r.top) {
        hi = mid - 1;
      } else if (y >= r.top + r.height) {
        lo = mid + 1;
      } else {
        if (r.group != null) return null;
        final col = (local.dx / (_tile + _gap)).floor().clamp(0, _columns - 1);
        return col < r.items.length ? r.firstIndex + col : r.firstIndex + r.items.length - 1;
      }
    }
    return null;
  }

  void _dragStart(LongPressStartDetails d) {
    final i = _hit(d.localPosition);
    if (i == null) return;
    HapticFeedback.selectionClick();
    final sel = widget.selection;
    _anchor = i;
    _before = {...sel.ids};
    _adding = !sel.contains(_flat[i]);
    _lastLocal = d.localPosition;
    _apply(i);
  }

  void _apply(int current) {
    final a = _anchor;
    if (a == null) return;
    final next = {..._before};
    for (var k = min(a, current); k <= max(a, current); k++) {
      _adding ? next.add(_flat[k].id) : next.remove(_flat[k].id);
    }
    widget.selection.replace(next);
  }

  void _dragMove(LongPressMoveUpdateDetails d) {
    if (_anchor == null) return;
    _lastLocal = d.localPosition;
    final i = _hit(d.localPosition);
    if (i != null) _apply(i);
    final edge = 72.0;
    double speed = 0;
    if (d.localPosition.dy < edge) speed = -(edge - d.localPosition.dy) / 3;
    if (d.localPosition.dy > _viewport - edge) speed = (d.localPosition.dy - (_viewport - edge)) / 3;
    _autoScroll?.cancel();
    if (speed != 0) {
      _autoScroll = Timer.periodic(const Duration(milliseconds: 16), (_) {
        final to = (_scroll.offset + speed).clamp(0.0, _scroll.position.maxScrollExtent);
        _scroll.jumpTo(to);
        final i = _lastLocal == null ? null : _hit(_lastLocal!);
        if (i != null) _apply(i);
      });
    }
  }

  void _dragEnd() {
    _autoScroll?.cancel();
    _anchor = null;
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    if (widget.items.isEmpty) {
      return ListView(padding: EdgeInsets.only(bottom: widget.bottomPadding), children: [
        if (widget.header != null) SizedBox(height: widget.headerHeight, child: widget.header),
        if (widget.empty != null) widget.empty!,
      ]);
    }
    return ListenableBuilder(
      listenable: Listenable.merge([widget.selection, app.settings]),
      builder: (context, _) => LayoutBuilder(builder: (context, c) {
        _viewport = c.maxHeight;
        _layout(c.maxWidth, widget.groupBy ?? app.settings.groupBy);
        final px = app.settings.density == 'large' ? 384 : 256;
        return GestureDetector(
          onLongPressStart: _dragStart,
          onLongPressMoveUpdate: _dragMove,
          onLongPressEnd: (_) => _dragEnd(),
          onLongPressCancel: _dragEnd,
          child: Scrollbar(
            controller: _scroll,
            interactive: true,
            thickness: 6,
            radius: const Radius.circular(3),
            child: ListView.builder(
              controller: _scroll,
              padding: EdgeInsets.only(bottom: widget.bottomPadding),
              itemCount: _rows.length + (widget.header != null ? 1 : 0),
              itemBuilder: (context, index) {
                if (widget.header != null) {
                  if (index == 0) return SizedBox(height: widget.headerHeight, child: widget.header);
                  index--;
                }
                final r = _rows[index];
                if (r.group != null) return _header(r.group!, p);
                return SizedBox(
                  height: r.height,
                  child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    for (var k = 0; k < r.items.length; k++) ...[
                      if (k > 0) const SizedBox(width: _gap),
                      SizedBox(
                        width: _tile,
                        height: _tile,
                        child: ItemTile(
                          item: r.items[k],
                          pixels: px,
                          selected: widget.selection.contains(r.items[k]),
                          selecting: widget.selection.active,
                          onTap: () => widget.selection.active ? widget.selection.toggle(r.items[k]) : widget.onOpen(r.items[k], _flat),
                          onCheck: () => widget.selection.toggle(r.items[k]),
                        ),
                      ),
                    ],
                  ]),
                );
              },
            ),
          ),
        );
      }),
    );
  }

  Widget _header(Group g, Palette p) {
    final sel = widget.selection;
    final all = g.items.every(sel.contains);
    return SizedBox(
      height: 58,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(14, 10, 4, 4),
        child: Row(children: [
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisAlignment: MainAxisAlignment.center, children: [
              Text(g.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600, color: p.text)),
              if (g.subtitle.isNotEmpty)
                Text(g.subtitle, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12.5, color: p.text3)),
            ]),
          ),
          TextButton.icon(
            onPressed: () => sel.set(g.items, !all),
            icon: Icon(all ? Icons.check_circle : Icons.check_circle_outline, size: 18),
            label: Text(all ? 'Selected' : 'Select'),
            style: TextButton.styleFrom(foregroundColor: all ? p.accent : p.text2, visualDensity: VisualDensity.compact),
          ),
        ]),
      ),
    );
  }
}

/// A thumbnail: the picture, a video's length, a heart for favorites and a check circle for selecting.
class ItemTile extends StatelessWidget {
  const ItemTile({super.key, required this.item, required this.pixels, required this.selected, required this.selecting, required this.onTap, required this.onCheck});
  final Item item;
  final int pixels;
  final bool selected, selecting;
  final VoidCallback onTap, onCheck;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    return GestureDetector(
      onTap: onTap,
      child: Stack(fit: StackFit.expand, children: [
        ColoredBox(color: p.tile),
        AnimatedPadding(
          duration: const Duration(milliseconds: 120),
          padding: EdgeInsets.all(selected ? 7 : 0),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(selected ? 6 : 0),
            child: RotatedBox(
              quarterTurns: (item.rotation ~/ 90) % 4,
              child: Image(
                image: app.thumbnail(item, pixels),
                fit: BoxFit.cover,
                gaplessPlayback: true,
                filterQuality: FilterQuality.medium,
                errorBuilder: (_, __, ___) => Center(child: Icon(item.isVideo ? Icons.movie_outlined : Icons.image_outlined, color: p.text3)),
              ),
            ),
          ),
        ),
        if (item.isVideo)
          Positioned(
            right: 5,
            bottom: 5,
            child: _Badge(child: Row(mainAxisSize: MainAxisSize.min, children: [
              const Icon(Icons.play_arrow_rounded, size: 14, color: Colors.white),
              if (item.duration > 0) Text(formatDuration(item.duration), style: const TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.w600)),
            ])),
          ),
        if (app.isFavorite(item))
          const Positioned(left: 5, bottom: 5, child: Icon(Icons.favorite, size: 16, color: Colors.white, shadows: [Shadow(blurRadius: 4, color: Colors.black54)])),
        if (selecting || selected)
          Positioned(
            left: 0,
            top: 0,
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTap: onCheck,
              child: Padding(
                padding: const EdgeInsets.all(5),
                child: Container(
                  width: 22,
                  height: 22,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: selected ? p.accent : Colors.black26,
                    border: Border.all(color: Colors.white, width: 1.6),
                  ),
                  child: selected ? Icon(Icons.check, size: 15, color: Theme.of(context).colorScheme.onPrimary) : null,
                ),
              ),
            ),
          ),
      ]),
    );
  }
}

class _Badge extends StatelessWidget {
  const _Badge({required this.child});
  final Widget child;
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 2),
        decoration: BoxDecoration(color: Colors.black54, borderRadius: BorderRadius.circular(6)),
        child: child,
      );
}

/// A friendly empty screen.
class EmptyState extends StatelessWidget {
  const EmptyState({super.key, required this.icon, required this.title, required this.body, this.action});
  final IconData icon;
  final String title, body;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(32, 72, 32, 32),
      child: Column(children: [
        Container(
          width: 76,
          height: 76,
          decoration: BoxDecoration(color: p.accent.withValues(alpha: 0.14), shape: BoxShape.circle),
          child: Icon(icon, size: 36, color: p.accent),
        ),
        const SizedBox(height: 18),
        Text(title, textAlign: TextAlign.center, style: TextStyle(fontSize: 19, fontWeight: FontWeight.w600, color: p.text)),
        const SizedBox(height: 8),
        Text(body, textAlign: TextAlign.center, style: TextStyle(fontSize: 14.5, height: 1.4, color: p.text2)),
        if (action != null) ...[const SizedBox(height: 20), action!],
      ]),
    );
  }
}

/// The bar that replaces the navigation while things are selected.
class SelectionBar extends StatelessWidget {
  const SelectionBar({super.key, required this.selection, required this.all, required this.actions});
  final Selection selection;
  final List<Item> all;
  final List<Widget> actions;

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    return Material(
      color: p.panel,
      child: SafeArea(
        top: false,
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(4, 4, 8, 0),
            child: Row(children: [
              IconButton(tooltip: 'Done', onPressed: selection.clear, icon: const Icon(Icons.close)),
              Expanded(
                child: Text(selection.count == 0 ? 'Tap or drag to select' : '${selection.count} selected',
                    style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600, color: p.text)),
              ),
              TextButton(
                onPressed: () => selection.count == all.length ? selection.set(all, false) : selection.set(all, true),
                child: Text(selection.count == all.length && all.isNotEmpty ? 'Select none' : 'Select all'),
              ),
            ]),
          ),
          SizedBox(
            height: 64,
            child: Row(mainAxisAlignment: MainAxisAlignment.spaceAround, children: actions),
          ),
        ]),
      ),
    );
  }
}

/// An icon with a label under it, for the selection bar and the viewer.
class ActionButton extends StatelessWidget {
  const ActionButton({super.key, required this.icon, required this.label, required this.onPressed, this.color, this.passive = false});
  final IconData icon;
  final String label;
  final VoidCallback? onPressed;
  final Color? color;

  /// Only the look (inside a PopupMenuButton, which handles the tap).
  final bool passive;

  @override
  Widget build(BuildContext context) {
    final p = Palette.of(context);
    final c = onPressed == null && !passive ? p.text3.withValues(alpha: 0.5) : (color ?? p.text);
    final body = Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Icon(icon, color: c, size: 23),
          const SizedBox(height: 3),
          Text(label, style: TextStyle(fontSize: 11.5, color: c, fontWeight: FontWeight.w500)),
        ]),
      );
    if (passive) return body;
    return InkWell(borderRadius: BorderRadius.circular(12), onTap: onPressed, child: body);
  }
}

/// A section title in lists and settings.
class SectionTitle extends StatelessWidget {
  const SectionTitle(this.text, {super.key});
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 20, 16, 8),
        child: Text(text.toUpperCase(), style: TextStyle(fontSize: 12, letterSpacing: 0.8, fontWeight: FontWeight.w600, color: Palette.of(context).accent)),
      );
}
