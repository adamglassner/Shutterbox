#!/usr/bin/env python3
"""Adjusts the Android project `flutter create` makes (CI creates it fresh each run).

  python3 tool/patch_native.py android   name, permissions, minSdk 24, desugaring, FragmentActivity (fingerprint prompt)
"""
import glob
import re
import sys


def read(p):
    with open(p, encoding="utf-8") as f:
        return f.read()


def write(p, s):
    with open(p, "w", encoding="utf-8") as f:
        f.write(s)
    print("patched", p)


def android():
    m = "android/app/src/main/AndroidManifest.xml"
    s = read(m)
    s = s.replace('android:label="shutterbox"', 'android:label="Shutterbox"')
    perms = [
        '<uses-permission android:name="android.permission.INTERNET"/>',  # updates from GitHub
        '<uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES"/>',  # installing those updates
        '<uses-permission android:name="android.permission.USE_BIOMETRIC"/>',  # fingerprint or face for the Locked album
        '<uses-permission android:name="android.permission.READ_MEDIA_IMAGES"/>',  # your photos (Android 13+)
        '<uses-permission android:name="android.permission.READ_MEDIA_VIDEO"/>',  # your videos (Android 13+)
        '<uses-permission android:name="android.permission.READ_MEDIA_VISUAL_USER_SELECTED"/>',  # "selected photos" (Android 14+)
        '<uses-permission android:name="android.permission.ACCESS_MEDIA_LOCATION"/>',  # where photos were taken
        '<uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" android:maxSdkVersion="32"/>',
        '<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" android:maxSdkVersion="29"/>',
        # opening a Locked album folder copied from a PC, moving it to a folder you choose, importing a folder
        '<uses-permission android:name="android.permission.MANAGE_EXTERNAL_STORAGE"/>',
    ]
    for p in perms:
        name = re.search(r'android:name="([^"]+)"', p).group(1)
        if f'"{name}"' not in s:
            s = s.replace("<application", p + "\n    <application", 1)
    if "requestLegacyExternalStorage" not in s:
        s = s.replace("<application", '<application android:requestLegacyExternalStorage="true"', 1)
    write(m, s)

    g = "android/app/build.gradle.kts"
    s = read(g)
    s = re.sub(r"minSdk\s*=\s*flutter\.minSdkVersion", "minSdk = 24", s)
    if "isCoreLibraryDesugaringEnabled" not in s:
        s = re.sub(r"compileOptions\s*\{", "compileOptions {\n        isCoreLibraryDesugaringEnabled = true", s, count=1)
    if "desugar_jdk_libs" not in s:
        s += '\ndependencies {\n    coreLibraryDesugaring("com.android.tools:desugar_jdk_libs:2.1.4")\n}\n'
    write(g, s)

    # Keep plugin classes whole: readable stack traces, and nothing a plugin finds by reflection goes missing
    write("android/app/proguard-rules.pro", """-keep class com.fluttercandies.photo_manager.** { *; }
-keep class com.baseflow.permissionhandler.** { *; }
-keep class com.it_nomads.fluttersecurestorage.** { *; }
-keep class com.crazecoder.openfile.** { *; }
-keepattributes SourceFile,LineNumberTable
""")

    # local_auth shows the system fingerprint/face prompt, which needs a FragmentActivity
    for p in glob.glob("android/app/src/main/kotlin/**/MainActivity.kt", recursive=True):
        s = read(p)
        s = s.replace("import io.flutter.embedding.android.FlutterActivity", "import io.flutter.embedding.android.FlutterFragmentActivity")
        s = s.replace(": FlutterActivity()", ": FlutterFragmentActivity()")
        write(p, s)


{"android": android}[sys.argv[1]]()
