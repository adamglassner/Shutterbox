import 'package:flutter/material.dart';
import 'package:permission_handler/permission_handler.dart';

import 'app.dart';

/// "All files access": needed to open a Locked album folder copied from a PC, move the Locked album to a folder you
/// choose (a synced folder, an SD card), and import a whole folder. Android asks once, in its own settings page.
class FilesAccess {
  static Future<bool> granted() async {
    if (!isAndroid) return true;
    try {
      return await Permission.manageExternalStorage.isGranted;
    } catch (_) {
      return false;
    }
  }

  /// Explains why, then asks. True when Shutterbox has it.
  static Future<bool> ensure(BuildContext context, String why) async {
    if (await granted()) return true;
    if (!context.mounted) return false;
    final go = await confirm(
      context,
      'Allow access to your files',
      '$why\n\nAndroid will open a settings page: turn on "Allow access to manage all files" for Shutterbox, then come back.',
      action: 'Continue',
    );
    if (!go) return false;
    try {
      final s = await Permission.manageExternalStorage.request();
      return s.isGranted;
    } catch (_) {
      return false;
    }
  }

  /// Location in photos (Android hides it unless the app asks).
  static Future<void> askForPhotoLocations() async {
    if (!isAndroid) return;
    try {
      await Permission.accessMediaLocation.request();
    } catch (_) {}
  }
}
