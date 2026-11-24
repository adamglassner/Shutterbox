import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:camera/camera.dart';
import 'package:flutter/material.dart';

import '../app.dart';
import '../models.dart';
import '../vault/vault.dart';

/// A small upright PNG of a photo (for the Locked album's grid), made without leaving a copy anywhere.
Future<Uint8List?> smallPicture(List<int> bytes, {int size = 400}) async {
  try {
    final probe = await ui.instantiateImageCodec(Uint8List.fromList(bytes));
    final first = await probe.getNextFrame();
    final w = first.image.width, h = first.image.height;
    first.image.dispose();
    final codec = await ui.instantiateImageCodec(Uint8List.fromList(bytes), targetWidth: w >= h ? size : null, targetHeight: h > w ? size : null);
    final frame = await codec.getNextFrame();
    final data = await frame.image.toByteData(format: ui.ImageByteFormat.png);
    frame.image.dispose();
    return data?.buffer.asUint8List();
  } catch (_) {
    return null;
  }
}

/// Takes a photo with the front camera without showing anything (for "Photograph wrong guesses").
Future<Uint8List?> takeQuietPhoto() async {
  CameraController? c;
  try {
    final cams = await availableCameras();
    final front = cams.where((x) => x.lensDirection == CameraLensDirection.front).firstOrNull ?? cams.firstOrNull;
    if (front == null) return null;
    c = CameraController(front, ResolutionPreset.medium, enableAudio: false);
    await c.initialize();
    final shot = await c.takePicture();
    final bytes = await shot.readAsBytes();
    try {
      await File(shot.path).delete();
    } catch (_) {}
    return bytes;
  } catch (_) {
    return null;
  } finally {
    await c?.dispose();
  }
}

/// A camera whose photos go straight into the Locked album: each is encrypted as it's taken and the camera's
/// temporary file deleted, so it never reaches the gallery.
class LockedCameraScreen extends StatefulWidget {
  const LockedCameraScreen({super.key, this.albumId});
  final String? albumId;

  @override
  State<LockedCameraScreen> createState() => _LockedCameraScreenState();
}

class _LockedCameraScreenState extends State<LockedCameraScreen> {
  List<CameraDescription> _cams = const [];
  CameraController? _c;
  int _which = 0;
  String? _error;
  int _taken = 0;
  bool _busy = false;
  Uint8List? _last;

  @override
  void initState() {
    super.initState();
    AppScope.read(context).expectReturn();
    _start();
  }

  Future<void> _start() async {
    try {
      _cams = await availableCameras();
      if (_cams.isEmpty) throw StateError('This phone has no camera Shutterbox can use.');
      await _open(0);
    } catch (e) {
      if (mounted) setState(() => _error = '$e');
    }
  }

  Future<void> _open(int i) async {
    final old = _c;
    setState(() => _c = null);
    await old?.dispose();
    final c = CameraController(_cams[i], ResolutionPreset.veryHigh, enableAudio: false);
    await c.initialize();
    if (!mounted) {
      await c.dispose();
      return;
    }
    setState(() {
      _c = c;
      _which = i;
    });
  }

  @override
  void dispose() {
    _c?.dispose();
    super.dispose();
  }

  Future<void> _shoot() async {
    final c = _c;
    if (c == null || _busy) return;
    setState(() => _busy = true);
    final app = AppScope.read(context);
    File? tmp;
    try {
      final shot = await c.takePicture();
      tmp = File(shot.path);
      final bytes = await tmp.readAsBytes();
      final now = DateTime.now();
      String two(int n) => n.toString().padLeft(2, '0');
      final name = 'IMG_${now.year}${two(now.month)}${two(now.day)}_${two(now.hour)}${two(now.minute)}${two(now.second)}.jpg';
      final thumb = await smallPicture(bytes);
      final r = await app.vault.add([
        LockSource(item: Item(id: 'camera-$name', name: name, taken: now, takenSource: 'exif'), file: tmp, thumbnail: () async => thumb),
      ], albumId: widget.albumId);
      if (r.errors.isNotEmpty) throw StateError(r.errors.first);
      setState(() {
        _taken++;
        _last = thumb;
      });
    } catch (e) {
      if (mounted) toast(context, 'Couldn\'t save the photo ($e).');
    } finally {
      try {
        if (tmp != null && await tmp.exists()) await tmp.delete();
      } catch (_) {}
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = _c;
    return Scaffold(
      backgroundColor: Colors.black,
      body: Stack(children: [
        if (_error != null)
          Center(child: Padding(padding: const EdgeInsets.all(24), child: Text(_error!, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70))))
        else if (c == null || !c.value.isInitialized)
          const Center(child: CircularProgressIndicator())
        else
          Center(child: CameraPreview(c)),
        SafeArea(
          child: Align(
            alignment: Alignment.topLeft,
            child: Row(children: [
              const BackButton(color: Colors.white),
              const Icon(Icons.lock, color: Colors.white70, size: 18),
              const SizedBox(width: 6),
              Text(_taken == 0 ? 'Photos go straight into the Locked album' : '$_taken saved to the Locked album',
                  style: const TextStyle(color: Colors.white, shadows: [Shadow(blurRadius: 6)])),
            ]),
          ),
        ),
        Positioned(
          left: 0,
          right: 0,
          bottom: 0,
          child: SafeArea(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(24, 0, 24, 24),
              child: Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
                SizedBox(
                  width: 56,
                  height: 56,
                  child: _last == null
                      ? null
                      : ClipRRect(borderRadius: BorderRadius.circular(10), child: Image.memory(_last!, fit: BoxFit.cover, gaplessPlayback: true)),
                ),
                GestureDetector(
                  onTap: _shoot,
                  child: Container(
                    width: 76,
                    height: 76,
                    decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: Colors.white, width: 5)),
                    padding: const EdgeInsets.all(5),
                    child: Container(decoration: BoxDecoration(shape: BoxShape.circle, color: _busy ? Colors.white38 : Colors.white)),
                  ),
                ),
                IconButton(
                  iconSize: 32,
                  color: Colors.white,
                  tooltip: 'Switch camera',
                  onPressed: _cams.length < 2 ? null : () => _open((_which + 1) % _cams.length),
                  icon: const Icon(Icons.cameraswitch_outlined),
                ),
              ]),
            ),
          ),
        ),
      ]),
    );
  }
}
