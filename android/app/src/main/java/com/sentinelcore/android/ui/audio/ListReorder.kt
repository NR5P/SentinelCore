package com.sentinelcore.android.ui.audio

import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.lazy.LazyListItemInfo
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.zIndex

// Drag a row by its handle to move it - the queue and the playlist editor. The list itself is
// reordered as the row passes its neighbours (onMove), and the change is committed once, on drop
// (onDrop), so a long drag makes one server call rather than one per row crossed. Same approach
// as GridReorder: the dragged row is only translated, never rebuilt.
@Stable
class ListReorderState internal constructor(
    private val listState: LazyListState,
    private val onMove: (fromKey: Any, toKey: Any) -> Unit,
    private val onDrop: (key: Any) -> Unit,
    private val onLift: () -> Unit,
) {
    var draggingKey: Any? by mutableStateOf(null)
        private set
    var offset by mutableFloatStateOf(0f)
        private set

    fun start(key: Any) {
        draggingKey = key
        offset = 0f
        onLift()
    }

    fun stop() {
        val key = draggingKey
        draggingKey = null
        offset = 0f
        if (key != null) onDrop(key)
    }

    fun drag(delta: Float) {
        val key = draggingKey ?: return
        offset += delta
        val dragged = info(key) ?: return
        val centre = dragged.offset + offset + dragged.size / 2f
        val target = listState.layoutInfo.visibleItemsInfo.firstOrNull { candidate ->
            candidate.key != key && candidate.contentType == dragged.contentType &&
                centre >= candidate.offset && centre <= candidate.offset + candidate.size
        } ?: return
        offset += (dragged.offset - target.offset).toFloat()
        onMove(key, target.key)
    }

    private fun info(key: Any): LazyListItemInfo? = listState.layoutInfo.visibleItemsInfo.firstOrNull { it.key == key }
}

@Composable
fun rememberListReorderState(
    listState: LazyListState,
    onMove: (fromKey: Any, toKey: Any) -> Unit,
    onDrop: (key: Any) -> Unit,
): ListReorderState {
    val haptics = LocalHapticFeedback.current
    val currentMove by rememberUpdatedState(onMove)
    val currentDrop by rememberUpdatedState(onDrop)
    return remember(listState) {
        ListReorderState(
            listState,
            onMove = { from, to -> currentMove(from, to) },
            onDrop = { key -> currentDrop(key) },
            onLift = { haptics.performHapticFeedback(HapticFeedbackType.LongPress) },
        )
    }
}

// On the row: lifts it above its neighbours and follows the finger.
fun Modifier.reorderableRow(state: ListReorderState, key: Any): Modifier = this
    .zIndex(if (state.draggingKey == key) 1f else 0f)
    .graphicsLayer {
        if (state.draggingKey == key) {
            translationY = state.offset
            shadowElevation = 12f
        }
    }

// On the handle: dragging it moves the row straight away (no long press needed).
fun Modifier.reorderHandle(state: ListReorderState, key: Any): Modifier = this.pointerInput(key) {
    detectDragGestures(
        onDragStart = { state.start(key) },
        onDrag = { change, amount ->
            change.consume()
            state.drag(amount.y)
        },
        onDragEnd = { state.stop() },
        onDragCancel = { state.stop() },
    )
}
