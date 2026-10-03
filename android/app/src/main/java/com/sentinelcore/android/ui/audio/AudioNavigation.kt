package com.sentinelcore.android.ui.audio

import kotlinx.coroutines.flow.MutableStateFlow

// Requests from outside the screens (the media notification) to show Now Playing.
object AudioNavigation {
    val openNowPlaying = MutableStateFlow(false)
}
