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
        # opening a Locked album folder copied from a PC, moving it to a folder you choose, importing a folder
        '<uses-permission android:name="android.permission.MANAGE_EXTERNAL_STORAGE"/>',
        # taking photos straight into the Locked album, and of wrong guesses (when turned on)
        '<uses-permission android:name="android.permission.CAMERA"/>',
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
-keep class io.flutter.plugins.camera.** { *; }
-keepattributes SourceFile,LineNumberTable
""")

    # "Lock with Shutterbox" in Android's share menu
    s = read(m)
    if "android.intent.action.SEND" not in s:
        share = '''<intent-filter android:label="Lock with Shutterbox">
                <action android:name="android.intent.action.SEND"/>
                <action android:name="android.intent.action.SEND_MULTIPLE"/>
                <category android:name="android.intent.category.DEFAULT"/>
                <data android:mimeType="image/*"/>
                <data android:mimeType="video/*"/>
            </intent-filter>
            '''
        i = s.index("<intent-filter")  # the launcher's
        s = s[:i] + share + s[i:]
        write(m, s)

    # MainActivity: a FragmentActivity (local_auth's fingerprint prompt needs one), keeping the Locked album out of
    # screenshots (FLAG_SECURE), and handing over what's shared to Shutterbox
    for p in glob.glob("android/app/src/main/kotlin/**/MainActivity.kt", recursive=True):
        pkg = re.search(r"^package\s+([\w.]+)", read(p), re.M).group(1)
        write(p, MAIN_ACTIVITY.replace("PACKAGE", pkg))


MAIN_ACTIVITY = '''package PACKAGE

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.OpenableColumns
import android.view.WindowManager
import io.flutter.embedding.android.FlutterFragmentActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.File

class MainActivity : FlutterFragmentActivity() {
    private var channel: MethodChannel? = null
    private val pending = mutableListOf<Map<String, String>>()

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        val ch = MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "shutterbox/native")
        channel = ch
        ch.setMethodCallHandler { call, result ->
            when (call.method) {
                "setSecure" -> {
                    val on = call.arguments as? Boolean ?: false
                    runOnUiThread {
                        if (on) window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
                        else window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
                    }
                    result.success(null)
                }
                "takeShared" -> {
                    val list = ArrayList(pending)
                    pending.clear()
                    result.success(list)
                }
                else -> result.notImplemented()
            }
        }
        collect(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        if (collect(intent)) channel?.invokeMethod("shared", null)
    }

    /** Copies what was shared into the cache (another app's files can't be read later). */
    private fun collect(intent: Intent?): Boolean {
        if (intent == null) return false
        val uris = mutableListOf<Uri>()
        @Suppress("DEPRECATION")
        when (intent.action) {
            Intent.ACTION_SEND -> (intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))?.let { uris.add(it) }
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.let { uris.addAll(it) }
            else -> return false
        }
        intent.action = null
        val dir = File(cacheDir, "shared")
        dir.mkdirs()
        for (uri in uris) {
            try {
                var name = "shared"
                contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
                    if (c.moveToFirst()) name = c.getString(0) ?: name
                }
                name = name.replace('/', '_')
                var out = File(dir, name)
                var n = 2
                while (out.exists()) { out = File(dir, "${n}_$name"); n++ }
                contentResolver.openInputStream(uri)?.use { input -> out.outputStream().use { input.copyTo(it) } }
                pending.add(mapOf("path" to out.absolutePath, "name" to name, "mime" to (contentResolver.getType(uri) ?: "")))
            } catch (e: Exception) {
            }
        }
        return uris.isNotEmpty()
    }
}
'''


{"android": android}[sys.argv[1]]()
