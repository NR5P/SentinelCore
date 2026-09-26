package com.sentinelcore.android.ui

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Lightbulb
import androidx.compose.material.icons.filled.LiveTv
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.Settings
import androidx.compose.ui.graphics.vector.ImageVector

// Internal rather than private so TabBarLayout's unit tests can reach it.
internal enum class AppSection(
    val title: String,
    val route: String,
) {
    EVENTS("Events", "events"),
    TIMELINE("Timeline", "timeline"),
    LIVE("Live", "live"),
    MOVIES("Movies", "movies"),
    ORGANIZER("Organizer", "organizer"),
    DEVICES("Devices", "devices"),
    SETTINGS("Settings", "settings"),
    ;

    // Looked up on use rather than held in the constructor, so building the enum - which the
    // plain-JVM unit tests do - never touches Compose's icon builders.
    val icon: ImageVector
        get() = when (this) {
            EVENTS -> Icons.Default.Security
            TIMELINE -> Icons.Default.History
            LIVE -> Icons.Default.LiveTv
            MOVIES -> Icons.Default.Movie
            ORGANIZER -> Icons.Default.CalendarMonth
            DEVICES -> Icons.Default.Lightbulb
            SETTINGS -> Icons.Default.Settings
        }

    companion object {
        // The Device Hub, the Organizer and Movies & TV can each be switched off server-side.
        fun visible(
            deviceHubEnabled: Boolean,
            organizerEnabled: Boolean,
            moviesEnabled: Boolean = false,
        ): List<AppSection> =
            entries.filter {
                (it != DEVICES || deviceHubEnabled) &&
                    (it != ORGANIZER || organizerEnabled) &&
                    (it != MOVIES || moviesEnabled)
            }
    }
}
