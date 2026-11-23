import 'package:flutter/material.dart';

/// The five accent colours, as on Windows: (name, colour on light, colour on dark).
const accents = <(String, Color, Color)>[
  ('Amber', Color(0xFFA35E0A), Color(0xFFE8A33D)),
  ('Sky', Color(0xFF0B6FB8), Color(0xFF5FB3E8)),
  ('Sage', Color(0xFF2E7D45), Color(0xFF7FCB8E)),
  ('Coral', Color(0xFFB3412C), Color(0xFFE57B6B)),
  ('Iris', Color(0xFF6D4AD6), Color(0xFFB3A1FA)),
];

/// Shutterbox's own colours (the darkroom look), next to Material's.
@immutable
class Palette extends ThemeExtension<Palette> {
  const Palette({
    required this.bg,
    required this.panel,
    required this.card,
    required this.tile,
    required this.chip,
    required this.divider,
    required this.text,
    required this.text2,
    required this.text3,
    required this.accent,
    required this.danger,
    required this.success,
  });

  final Color bg, panel, card, tile, chip, divider, text, text2, text3, accent, danger, success;

  static const _dark = Palette(
    bg: Color(0xFF111213),
    panel: Color(0xFF17191B),
    card: Color(0xFF1F2224),
    tile: Color(0xFF1C1E20),
    chip: Color(0xFF26292C),
    divider: Color(0xFF26292C),
    text: Color(0xFFECEAE6),
    text2: Color(0xFFA4A8AC),
    text3: Color(0xFF8D9196),
    accent: Color(0xFFE8A33D),
    danger: Color(0xFFFF8F86),
    success: Color(0xFF5FCB8B),
  );

  static const _light = Palette(
    bg: Color(0xFFF6F4F0),
    panel: Color(0xFFEDEAE4),
    card: Color(0xFFFFFFFF),
    tile: Color(0xFFE6E2DA),
    chip: Color(0xFFECE8E1),
    divider: Color(0xFFE2DDD4),
    text: Color(0xFF1D1C1A),
    text2: Color(0xFF5B5852),
    text3: Color(0xFF6E6A63),
    accent: Color(0xFFA35E0A),
    danger: Color(0xFFB3261E),
    success: Color(0xFF2E7D45),
  );

  static Palette of(BuildContext context) => Theme.of(context).extension<Palette>()!;

  @override
  Palette copyWith({Color? accent}) => Palette(
      bg: bg,
      panel: panel,
      card: card,
      tile: tile,
      chip: chip,
      divider: divider,
      text: text,
      text2: text2,
      text3: text3,
      accent: accent ?? this.accent,
      danger: danger,
      success: success);

  @override
  Palette lerp(Palette? other, double t) => t < 0.5 || other == null ? this : other;
}

ThemeData buildTheme(Brightness brightness, String accentName) {
  final dark = brightness == Brightness.dark;
  final a = accents.firstWhere((x) => x.$1 == accentName, orElse: () => accents.first);
  final p = (dark ? Palette._dark : Palette._light).copyWith(accent: dark ? a.$3 : a.$2);
  final scheme = ColorScheme.fromSeed(seedColor: a.$3, brightness: brightness).copyWith(
    primary: p.accent,
    onPrimary: dark ? const Color(0xFF1A1206) : Colors.white,
    surface: p.bg,
    onSurface: p.text,
    onSurfaceVariant: p.text2,
    surfaceContainerLowest: p.bg,
    surfaceContainerLow: p.panel,
    surfaceContainer: p.panel,
    surfaceContainerHigh: p.card,
    surfaceContainerHighest: p.chip,
    outlineVariant: p.divider,
    error: p.danger,
    secondaryContainer: p.accent.withValues(alpha: 0.22),
    onSecondaryContainer: p.text,
  );
  final base = ThemeData(useMaterial3: true, colorScheme: scheme, brightness: brightness, fontFamily: 'InstrumentSans');
  return base.copyWith(
    scaffoldBackgroundColor: p.bg,
    extensions: [p],
    appBarTheme: AppBarTheme(
      backgroundColor: p.bg,
      foregroundColor: p.text,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      scrolledUnderElevation: 0,
      titleTextStyle: TextStyle(fontFamily: 'InstrumentSans', fontSize: 21, fontWeight: FontWeight.w600, color: p.text),
    ),
    navigationBarTheme: NavigationBarThemeData(
      backgroundColor: p.panel,
      indicatorColor: p.accent.withValues(alpha: 0.22),
      surfaceTintColor: Colors.transparent,
      height: 66,
      labelTextStyle: WidgetStatePropertyAll(TextStyle(fontFamily: 'InstrumentSans', fontSize: 12, fontWeight: FontWeight.w500, color: p.text2)),
    ),
    dividerTheme: DividerThemeData(color: p.divider, thickness: 1, space: 1),
    cardTheme: CardThemeData(color: p.card, surfaceTintColor: Colors.transparent, elevation: 0, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
    bottomSheetTheme: BottomSheetThemeData(backgroundColor: p.panel, surfaceTintColor: Colors.transparent, showDragHandle: true),
    dialogTheme: DialogThemeData(backgroundColor: p.panel, surfaceTintColor: Colors.transparent),
    popupMenuTheme: PopupMenuThemeData(color: p.card, surfaceTintColor: Colors.transparent),
    snackBarTheme: SnackBarThemeData(behavior: SnackBarBehavior.floating, backgroundColor: p.card, contentTextStyle: TextStyle(color: p.text, fontFamily: 'InstrumentSans')),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: dark ? const Color(0xFF1C1E20) : Colors.white,
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(10), borderSide: BorderSide(color: p.divider)),
      enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(10), borderSide: BorderSide(color: dark ? const Color(0xFF34383C) : const Color(0xFFCFC9BE))),
      focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(10), borderSide: BorderSide(color: p.accent, width: 1.6)),
      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
    ),
    chipTheme: ChipThemeData(backgroundColor: p.chip, side: BorderSide.none, shape: const StadiumBorder(), labelStyle: TextStyle(color: p.text, fontFamily: 'InstrumentSans')),
    listTileTheme: ListTileThemeData(iconColor: p.text2, textColor: p.text),
  );
}
