import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter_map/flutter_map.dart';
import 'package:latlong2/latlong.dart';
import 'package:url_launcher/url_launcher.dart';

import '../app.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'albums.dart';

/// Photos close together on the map at the current zoom.
class Cluster {
  Cluster(this.items);
  final List<Item> items;
  LatLng get center {
    var lat = 0.0, lng = 0.0;
    for (final i in items) {
      lat += i.latitude!;
      lng += i.longitude!;
    }
    return LatLng(lat / items.length, lng / items.length);
  }
}

/// Groups photos into cells about [cellPixels] wide at [zoom] (Web Mercator, 256-pixel tiles).
List<Cluster> clusterItems(List<Item> items, double zoom, {double cellPixels = 72}) {
  final scale = 256 * pow(2, zoom);
  final cells = <String, List<Item>>{};
  for (final i in items) {
    final x = (i.longitude! + 180) / 360 * scale;
    final s = sin(i.latitude!.clamp(-85.0, 85.0) * pi / 180);
    final y = (0.5 - log((1 + s) / (1 - s)) / (4 * pi)) * scale;
    cells.putIfAbsent('${(x / cellPixels).floor()}:${(y / cellPixels).floor()}', () => []).add(i);
  }
  final out = [for (final c in cells.values) Cluster(c..sort((a, b) => b.taken.compareTo(a.taken)))];
  out.sort((a, b) => b.items.length.compareTo(a.items.length));
  return out;
}

/// Every photo with a location, on a map. Pins group photos taken close together; zoom in to split them,
/// tap one to see its photos.
class PlacesScreen extends StatefulWidget {
  const PlacesScreen({super.key});

  @override
  State<PlacesScreen> createState() => _PlacesScreenState();
}

class _PlacesScreenState extends State<PlacesScreen> {
  final _map = MapController();
  double _zoom = 3;

  @override
  void dispose() {
    _map.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    return ListenableBuilder(
      listenable: app.library,
      builder: (context, _) {
        final items = app.library.placed;
        if (items.isEmpty) {
          return Scaffold(
            appBar: AppBar(title: const Text('Places')),
            body: const EmptyState(
              icon: Icons.place_outlined,
              title: 'No places yet',
              body: 'Photos with a location (most phone cameras add one when location is on) show up on a map here. '
                  'Shutterbox reads locations in the background, so give it a moment after it first starts.',
            ),
          );
        }
        final clusters = clusterItems(items, _zoom);
        final pts = [for (final i in items) LatLng(i.latitude!, i.longitude!)];
        return Scaffold(
          appBar: AppBar(title: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Text('Places'),
            Text('${items.length} ${items.length == 1 ? 'photo' : 'photos and videos'} with a location', style: TextStyle(fontSize: 12.5, color: p.text3, fontWeight: FontWeight.w400)),
          ])),
          body: FlutterMap(
            mapController: _map,
            options: MapOptions(
              initialCenter: pts.first,
              initialZoom: 3,
              initialCameraFit: pts.length > 1 ? CameraFit.bounds(bounds: LatLngBounds.fromPoints(pts), padding: const EdgeInsets.all(48), maxZoom: 14) : null,
              minZoom: 2,
              maxZoom: 18,
              onPositionChanged: (camera, _) {
                final z = camera.zoom.roundToDouble();
                if (z != _zoom) setState(() => _zoom = z);
              },
              onMapReady: () => setState(() => _zoom = _map.camera.zoom.roundToDouble()),
            ),
            children: [
              TileLayer(urlTemplate: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png', userAgentPackageName: 'com.bytehawkstudio.shutterbox'),
              MarkerLayer(markers: [
                for (final c in clusters)
                  Marker(
                    point: c.center,
                    width: 64,
                    height: 64,
                    child: GestureDetector(
                      onTap: () => CollectionScreen.open(context, CollectionKind.place, items: c.items, title: _placeTitle(c)),
                      child: _Pin(cluster: c),
                    ),
                  ),
              ]),
              RichAttributionWidget(attributions: [
                TextSourceAttribution('OpenStreetMap contributors', onTap: () => launchUrl(Uri.parse('https://www.openstreetmap.org/copyright'), mode: LaunchMode.externalApplication)),
              ]),
            ],
          ),
        );
      },
    );
  }

  static String _placeTitle(Cluster c) {
    final ll = c.center;
    return 'Near ${ll.latitude.abs().toStringAsFixed(2)}°${ll.latitude >= 0 ? 'N' : 'S'}, ${ll.longitude.abs().toStringAsFixed(2)}°${ll.longitude >= 0 ? 'E' : 'W'}';
  }
}

class _Pin extends StatelessWidget {
  const _Pin({required this.cluster});
  final Cluster cluster;

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final p = Palette.of(context);
    final first = cluster.items.first;
    return Stack(clipBehavior: Clip.none, children: [
      Container(
        width: 60,
        height: 60,
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: Colors.white, width: 2.5),
          boxShadow: const [BoxShadow(blurRadius: 6, color: Colors.black38)],
          color: p.tile,
        ),
        clipBehavior: Clip.antiAlias,
        child: RotatedBox(
          quarterTurns: (first.rotation ~/ 90) % 4,
          child: Image(image: app.thumbnail(first, 256), fit: BoxFit.cover, errorBuilder: (_, __, ___) => Icon(Icons.image_outlined, color: p.text3)),
        ),
      ),
      if (cluster.items.length > 1)
        Positioned(
          right: -6,
          top: -6,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
            decoration: BoxDecoration(color: p.accent, borderRadius: BorderRadius.circular(10)),
            child: Text('${cluster.items.length}', style: TextStyle(color: Theme.of(context).colorScheme.onPrimary, fontSize: 11.5, fontWeight: FontWeight.w700)),
          ),
        ),
    ]);
  }
}
