import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

/// An image made from bytes loaded on demand (a gallery thumbnail, a decrypted Locked album photo), cached by [key]
/// in Flutter's image cache like any other image.
@immutable
class BytesImage extends ImageProvider<BytesImage> {
  const BytesImage(this.key, this.load);

  final String key;
  final Future<Uint8List?> Function() load;

  @override
  Future<BytesImage> obtainKey(ImageConfiguration configuration) => SynchronousFuture<BytesImage>(this);

  @override
  ImageStreamCompleter loadImage(BytesImage key, ImageDecoderCallback decode) =>
      MultiFrameImageStreamCompleter(codec: _codec(decode), scale: 1.0, debugLabel: key.key);

  Future<ui.Codec> _codec(ImageDecoderCallback decode) async {
    final bytes = await load();
    if (bytes == null || bytes.isEmpty) {
      PaintingBinding.instance.imageCache.evict(this);
      throw StateError('No picture for $key');
    }
    return decode(await ui.ImmutableBuffer.fromUint8List(bytes));
  }

  @override
  bool operator ==(Object other) => other is BytesImage && other.key == key;

  @override
  int get hashCode => key.hashCode;

  @override
  String toString() => 'BytesImage($key)';
}
