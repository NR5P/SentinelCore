package com.sentinelcore.android.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import androidx.compose.material3.Typography

val SentinelBackground = Color(0xFF05080A)
val SentinelPanel = Color(0xFF091015)
val SentinelCard = Color(0xFF0D131A)
val SentinelGreen = Color(0xFF38FF9C)
val SentinelGreenDim = Color(0xFF1F9C61)
val SentinelCyan = Color(0xFF49E8FF)
val SentinelAmber = Color(0xFFFFB545)
val SentinelRed = Color(0xFFFF4D5E)
val SentinelText = Color(0xFFC7F5D9)
val SentinelTextDim = Color(0xFF6B9482)

private val colors = darkColorScheme(
    primary = SentinelGreen,
    onPrimary = SentinelBackground,
    primaryContainer = Color(0xFF163E2D),
    onPrimaryContainer = SentinelGreen,
    secondary = SentinelCyan,
    onSecondary = SentinelBackground,
    secondaryContainer = Color(0xFF163E2D),
    onSecondaryContainer = SentinelGreen,
    tertiary = SentinelAmber,
    background = SentinelBackground,
    onBackground = SentinelText,
    surface = SentinelPanel,
    onSurface = SentinelText,
    surfaceVariant = SentinelCard,
    onSurfaceVariant = SentinelTextDim,
    error = SentinelRed,
    onError = SentinelBackground,
    outline = SentinelGreenDim,
)

private val typography = Typography().run {
    copy(
        displayLarge = displayLarge.copy(fontFamily = FontFamily.Monospace),
        displayMedium = displayMedium.copy(fontFamily = FontFamily.Monospace),
        displaySmall = displaySmall.copy(fontFamily = FontFamily.Monospace),
        headlineLarge = headlineLarge.copy(fontFamily = FontFamily.Monospace),
        headlineMedium = headlineMedium.copy(fontFamily = FontFamily.Monospace),
        headlineSmall = headlineSmall.copy(fontFamily = FontFamily.Monospace),
        titleLarge = titleLarge.copy(fontFamily = FontFamily.Monospace, fontWeight = FontWeight.Bold),
        titleMedium = titleMedium.copy(fontFamily = FontFamily.Monospace),
        titleSmall = titleSmall.copy(fontFamily = FontFamily.Monospace),
        bodyLarge = bodyLarge.copy(fontFamily = FontFamily.Monospace),
        bodyMedium = bodyMedium.copy(fontFamily = FontFamily.Monospace),
        bodySmall = bodySmall.copy(fontFamily = FontFamily.Monospace),
        labelLarge = labelLarge.copy(fontFamily = FontFamily.Monospace),
        labelMedium = labelMedium.copy(fontFamily = FontFamily.Monospace),
        labelSmall = labelSmall.copy(fontFamily = FontFamily.Monospace, fontSize = 11.sp),
    )
}

@Composable
fun SentinelTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = colors,
        typography = typography,
        content = content,
    )
}
