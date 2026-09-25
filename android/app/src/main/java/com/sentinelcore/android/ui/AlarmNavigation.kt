package com.sentinelcore.android.ui

import kotlinx.coroutines.flow.MutableStateFlow

// Set when an alarm push is tapped (see PushNotifications); SentinelApp navigates to the Alarm
// screen and clears it. Same shape as OrganizerNavigation.
object AlarmNavigation {
    val open = MutableStateFlow(false)
}
