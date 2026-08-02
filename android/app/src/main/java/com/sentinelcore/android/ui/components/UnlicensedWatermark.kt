package com.sentinelcore.android.ui.components

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import com.sentinelcore.android.ui.theme.SentinelAmber

// Whether the server this app is talking to has a valid licence file. Provided once at the root
// (see SentinelApp) so the low-level views that draw the mark don't each have to take the whole
// view model just to ask.
//
// The default is "licensed", which is also the right answer when the check itself fails: this is
// visibility, not enforcement, and falsely accusing a paying customer is a worse failure than
// missing a nag.
val LocalIsLicensed = staticCompositionLocalOf { true }

/**
 * Draws the UNLICENSED mark across the [Box] this is called in, when the server has no valid
 * licence. Twin of the dashboard's `body.unlicensed` rules in wwwroot/styles.css - same words,
 * same amber, same rotation - so a customer who sees it in one place recognises it in the others.
 *
 * Deliberately cosmetic, matching the "nag, never gate" rule everywhere else licensing touches:
 * it takes no pointer input, so taps land on whatever is underneath, and nothing about it stops
 * an image loading or a stream playing.
 */
@Composable
fun BoxScope.UnlicensedWatermark() {
    if (LocalIsLicensed.current) return

    BoxWithConstraints(
        modifier = Modifier
            .matchParentSize()
            // Nothing for a screen reader here - the mark is a visual nag, and the licence state
            // is stated in plain words in Settings.
            .clearAndSetSemantics {},
        contentAlignment = Alignment.Center,
    ) {
        // Scales with the tile the way the dashboard's clamp() does, so it reads the same on a
        // thumbnail in a feed and on a full-screen player.
        val fontSize = (maxWidth.value * 0.11f).coerceIn(11f, 48f).sp
        Text(
            text = "UNLICENSED",
            color = SentinelAmber.copy(alpha = 0.4f),
            fontSize = fontSize,
            fontWeight = FontWeight.Bold,
            letterSpacing = fontSize * 0.2f,
            maxLines = 1,
            softWrap = false,
            modifier = Modifier.rotate(-16f),
        )
    }
}
