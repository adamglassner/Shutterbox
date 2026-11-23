import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../app.dart';
import '../models.dart';

/// Photos one after another, oldest first, with crossfades (videos are skipped). Tap for controls.
class SlideshowScreen extends StatefulWidget {
  const SlideshowScreen({super.key, required this.items});
  final List<Item> items;

  @override
  State<SlideshowScreen> createState() => _SlideshowScreenState();
}

class _SlideshowScreenState extends State<SlideshowScreen> {
  int _i = 0;
  Timer? _timer;
  bool _paused = false, _controls = false;

  @override
  void initState() {
    super.initState();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
    WidgetsBinding.instance.addPostFrameCallback((_) => _schedule());
  }

  void _schedule() {
    _timer?.cancel();
    if (_paused || !mounted) return;
    final seconds = AppScope.read(context).settings.slideshowSeconds;
    _timer = Timer(Duration(seconds: seconds), () => _go(1));
  }

  void _go(int step) {
    if (!mounted || widget.items.isEmpty) return;
    final app = AppScope.read(context);
    app.touch();
    setState(() => _i = (_i + step) % widget.items.length);
    // load the next one ahead of time
    final next = widget.items[(_i + 1) % widget.items.length];
    precacheImage(app.full(next), context).ignore();
    _schedule();
  }

  @override
  void dispose() {
    _timer?.cancel();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final item = widget.items[_i];
    return Scaffold(
      backgroundColor: Colors.black,
      body: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: () => setState(() => _controls = !_controls),
        onHorizontalDragEnd: (d) => _go((d.primaryVelocity ?? 0) > 0 ? -1 : 1),
        child: Stack(fit: StackFit.expand, children: [
          AnimatedSwitcher(
            duration: const Duration(milliseconds: 900),
            child: RotatedBox(
              key: ValueKey('${item.id}:$_i'),
              quarterTurns: (item.rotation ~/ 90) % 4,
              child: Image(image: app.full(item), fit: BoxFit.contain, gaplessPlayback: true, errorBuilder: (_, __, ___) => const SizedBox()),
            ),
          ),
          if (_controls)
            Positioned(
              left: 0,
              right: 0,
              bottom: 0,
              child: Container(
                color: Colors.black54,
                child: SafeArea(
                  top: false,
                  child: Row(mainAxisAlignment: MainAxisAlignment.spaceEvenly, children: [
                    IconButton(color: Colors.white, icon: const Icon(Icons.close), tooltip: 'Stop', onPressed: () => Navigator.of(context).pop()),
                    IconButton(color: Colors.white, icon: const Icon(Icons.skip_previous), tooltip: 'Previous', onPressed: () => _go(-1)),
                    IconButton(
                      color: Colors.white,
                      icon: Icon(_paused ? Icons.play_arrow : Icons.pause),
                      tooltip: _paused ? 'Play' : 'Pause',
                      onPressed: () {
                        setState(() => _paused = !_paused);
                        _paused ? _timer?.cancel() : _schedule();
                      },
                    ),
                    IconButton(color: Colors.white, icon: const Icon(Icons.skip_next), tooltip: 'Next', onPressed: () => _go(1)),
                    Text('${_i + 1} / ${widget.items.length}', style: const TextStyle(color: Colors.white70)),
                  ]),
                ),
              ),
            ),
          if (_controls)
            Positioned(
              left: 16,
              top: MediaQuery.of(context).padding.top + 12,
              child: Text('${formatDay(item.taken)}  ·  ${item.name}', style: const TextStyle(color: Colors.white, shadows: [Shadow(blurRadius: 6)])),
            ),
        ]),
      ),
    );
  }
}
