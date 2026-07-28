package com.sentinelcore.android.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

// Brand accent colors only - kept as named, theme-aware properties (not plain constants) so
// every existing call site (Text(color = SentinelGreen), NavigationBar(containerColor =
// SentinelPanel), etc.) picks up the right light/dark value automatically with no changes.
// Everything that isn't a deliberate brand accent (backgrounds, primary/secondary text) should
// prefer MaterialTheme.colorScheme's own semantics instead of reaching for one of these.

private val GreenDark = Color(0xFF38FF9C)
private val GreenLight = Color(0xFF0E8C4E)
private val GreenDimDark = Color(0xFF1F9C61)
private val GreenDimLight = Color(0xFF0B5C38)
private val CyanDark = Color(0xFF49E8FF)
private val CyanLight = Color(0xFF00707F)
private val AmberDark = Color(0xFFFFB545)
private val AmberLight = Color(0xFF7A5200)
private val RedDark = Color(0xFFFF4D5E)
private val RedLight = Color(0xFFA3111F)
private val BackgroundDark = Color(0xFF05080A)
private val BackgroundLight = Color(0xFFFAFAF8)
private val PanelDark = Color(0xFF091015)
private val PanelLight = Color(0xFFF0F1EC)
private val CardDark = Color(0xFF0D131A)
private val CardLight = Color(0xFFFFFFFF)
private val TextDark = Color(0xFFC7F5D9)
private val TextLight = Color(0xFF13201A)
private val TextDimDark = Color(0xFF6B9482)
private val TextDimLight = Color(0xFF57685F)

// Timeline event-category colors that aren't part of the Material scheme (person/vehicle/plate
// already map to primary/secondary/tertiary below) - kept here so their light/dark variants live
// with the rest of the palette instead of as magic numbers in RecordingTimelineScreen.kt.
private val CategoryPackageDark = Color(0xFFB388FF)
private val CategoryPackageLight = Color(0xFF5E35B1)
private val CategoryAnimalDark = Color(0xFFFF8A50)
private val CategoryAnimalLight = Color(0xFFB8541F)

val SentinelGreen: Color @Composable get() = if (isSystemInDarkTheme()) GreenDark else GreenLight
val SentinelGreenDim: Color @Composable get() = if (isSystemInDarkTheme()) GreenDimDark else GreenDimLight
val SentinelCyan: Color @Composable get() = if (isSystemInDarkTheme()) CyanDark else CyanLight
val SentinelAmber: Color @Composable get() = if (isSystemInDarkTheme()) AmberDark else AmberLight
val SentinelRed: Color @Composable get() = if (isSystemInDarkTheme()) RedDark else RedLight
val SentinelBackground: Color @Composable get() = if (isSystemInDarkTheme()) BackgroundDark else BackgroundLight
val SentinelPanel: Color @Composable get() = if (isSystemInDarkTheme()) PanelDark else PanelLight
val SentinelCard: Color @Composable get() = if (isSystemInDarkTheme()) CardDark else CardLight
val SentinelText: Color @Composable get() = if (isSystemInDarkTheme()) TextDark else TextLight
val SentinelTextDim: Color @Composable get() = if (isSystemInDarkTheme()) TextDimDark else TextDimLight
val SentinelCategoryPackage: Color @Composable get() = if (isSystemInDarkTheme()) CategoryPackageDark else CategoryPackageLight
val SentinelCategoryAnimal: Color @Composable get() = if (isSystemInDarkTheme()) CategoryAnimalDark else CategoryAnimalLight

private val darkColors = darkColorScheme(
    primary = GreenDark,
    onPrimary = BackgroundDark,
    primaryContainer = Color(0xFF163E2D),
    onPrimaryContainer = GreenDark,
    secondary = CyanDark,
    onSecondary = BackgroundDark,
    secondaryContainer = Color(0xFF163E2D),
    onSecondaryContainer = GreenDark,
    tertiary = AmberDark,
    background = BackgroundDark,
    onBackground = TextDark,
    surface = PanelDark,
    onSurface = TextDark,
    surfaceVariant = CardDark,
    onSurfaceVariant = TextDimDark,
    error = RedDark,
    onError = BackgroundDark,
    outline = GreenDimDark,
)

private val lightColors = lightColorScheme(
    primary = GreenLight,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFC8F5D9),
    onPrimaryContainer = GreenLight,
    secondary = CyanLight,
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFC8ECF0),
    onSecondaryContainer = CyanLight,
    tertiary = AmberLight,
    background = BackgroundLight,
    onBackground = TextLight,
    surface = PanelLight,
    onSurface = TextLight,
    surfaceVariant = CardLight,
    onSurfaceVariant = TextDimLight,
    error = RedLight,
    onError = Color.White,
    outline = GreenDimLight,
)

// Monospace was previously applied to every text style, which is where the app's "terminal"
// look mostly came from. Kept narrowly (SettingsScreen's diagnostics log, Timeline's scrubber
// labels) at those specific call sites instead of globally - everything else now falls back to
// the Material 3 default (Roboto/system font).
private val typography = Typography()

@Composable
fun SentinelTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    // On by default (Android 12+ only, falling back below that): most modern Android apps pick
    // up the user's Material You wallpaper palette unless there's a strong reason not to. Not
    // exposed as a user-facing toggle yet - would need a small persisted preference outside the
    // existing SentinelViewModel, deliberately left for later rather than done half-way here.
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit,
) {
    val context = LocalContext.current
    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        darkTheme -> darkColors
        else -> lightColors
    }
    MaterialTheme(
        colorScheme = colorScheme,
        typography = typography,
        content = content,
    )
}
