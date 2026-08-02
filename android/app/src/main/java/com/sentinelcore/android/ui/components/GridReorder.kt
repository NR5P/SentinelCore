package com.sentinelcore.android.ui.components

import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.lazy.grid.LazyGridItemInfo
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.zIndex

// Press and hold a grid item, then drag it over another one to swap them around. Written by hand
// because Compose has no built-in reorderable lazy grid - the ingredients are a long-press drag
// detector, a translation applied to the dragged item, and a hit test against the grid's own
// layout information to work out what is underneath it.
//
// The dragged item is only ever *translated*, never re-parented or rebuilt: on the Live grid each
// tile owns a running ExoPlayer, and rebuilding one mid-drag would stop the video the person is
// looking at. Reordering the underlying list is safe for the same reason the grid is keyed by
// camera id - Compose moves the existing node rather than recreating it.
@Stable
class GridReorderState internal constructor(
    private val gridState: LazyGridState,
    private val onMove: (movingKey: String, targetKey: String) -> Unit,
    private val onLift: () -> Unit,
) {
    var draggingKey: String? by mutableStateOf(null)
        private set

    // How far the dragged item has been pulled from the slot it currently occupies. Reset - not
    // accumulated - whenever the item takes over a new slot, so the tile keeps sitting under the
    // finger as the grid rearranges beneath it.
    var offset: Offset by mutableStateOf(Offset.Zero)
        private set

    fun start(key: String) {
        draggingKey = key
        offset = Offset.Zero
        onLift()
    }

    fun stop() {
        draggingKey = null
        offset = Offset.Zero
    }

    fun drag(delta: Offset) {
        val key = draggingKey ?: return
        offset += delta

        val dragged = itemInfo(key) ?: return
        val centre = Offset(
            dragged.offset.x + offset.x + dragged.size.width / 2f,
            dragged.offset.y + offset.y + dragged.size.height / 2f,
        )
        val target = gridState.layoutInfo.visibleItemsInfo.firstOrNull { candidate ->
            candidate.key != key && contains(candidate, centre)
        } ?: return
        val targetKey = target.key as? String ?: return

        // The list is about to reorder, which moves the dragged item into the target's slot.
        // Taking that shift out of the translation is what keeps the tile visually still at the
        // moment of the swap instead of jumping a whole cell.
        offset += Offset(
            (dragged.offset.x - target.offset.x).toFloat(),
            (dragged.offset.y - target.offset.y).toFloat(),
        )
        onMove(key, targetKey)
    }

    private fun itemInfo(key: String): LazyGridItemInfo? =
        gridState.layoutInfo.visibleItemsInfo.firstOrNull { it.key == key }

    private fun contains(item: LazyGridItemInfo, point: Offset): Boolean =
        point.x >= item.offset.x && point.x <= item.offset.x + item.size.width &&
            point.y >= item.offset.y && point.y <= item.offset.y + item.size.height
}

@Composable
fun rememberGridReorderState(
    gridState: LazyGridState,
    onMove: (movingKey: String, targetKey: String) -> Unit,
): GridReorderState {
    val haptics = LocalHapticFeedback.current
    // Through rememberUpdatedState so the long-lived state object always calls the current
    // lambda - the caller's closure captures the latest UI state, and a stale one would move
    // tiles based on an old camera list.
    val currentOnMove by rememberUpdatedState(onMove)
    return remember(gridState) {
        GridReorderState(
            gridState = gridState,
            onMove = { movingKey, targetKey -> currentOnMove(movingKey, targetKey) },
            // The one signal that the long press registered and the item is now being carried -
            // without it a drag that hasn't been held quite long enough is indistinguishable from
            // one that has.
            onLift = { haptics.performHapticFeedback(HapticFeedbackType.LongPress) },
        )
    }
}

fun Modifier.reorderableGridItem(state: GridReorderState, key: String): Modifier = this
    // Above its neighbours, so a tile being carried passes over them rather than under.
    .zIndex(if (state.draggingKey == key) 1f else 0f)
    .graphicsLayer {
        if (state.draggingKey != key) return@graphicsLayer
        translationX = state.offset.x
        translationY = state.offset.y
        // Just enough lift to read as "picked up" without covering the neighbours it's crossing.
        scaleX = 1.03f
        scaleY = 1.03f
    }
    .pointerInput(key) {
        detectDragGesturesAfterLongPress(
            onDragStart = { state.start(key) },
            onDrag = { change, dragAmount ->
                change.consume()
                state.drag(dragAmount)
            },
            onDragEnd = { state.stop() },
            onDragCancel = { state.stop() },
        )
    }
