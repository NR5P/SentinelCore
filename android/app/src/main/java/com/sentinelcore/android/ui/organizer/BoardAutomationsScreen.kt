package com.sentinelcore.android.ui.organizer

import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DragIndicator
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.AlertDialog
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.zIndex
import com.sentinelcore.android.organizer.AutomationList
import com.sentinelcore.android.organizer.AutomationRule
import com.sentinelcore.android.organizer.AutomationRun
import com.sentinelcore.android.organizer.AutomationWords
import com.sentinelcore.android.organizer.BoardAutomationClient
import com.sentinelcore.android.organizer.BoardAutomations
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerItem
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.organizer.OrganizerPerson
import com.sentinelcore.android.organizer.OrganizerSpace
import com.sentinelcore.android.organizer.jsNumber
import com.sentinelcore.android.organizer.RuleBlock
import com.sentinelcore.android.organizer.RuleBlockKind
import com.sentinelcore.android.organizer.RuleContext
import com.sentinelcore.android.organizer.RuleZone
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.coroutines.cancellation.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// Board automations on the phone: the board's rules, and a builder for them - the same WHEN /
// ONLY IF / THEN blocks as the dashboard's (organizer-automations.js), filled in with this app's
// menus and pickers. Blocks are added by tapping them in a palette under each part of the rule;
// the THEN steps reorder with the arrows, or by holding the grip and dragging. The rules run on
// the server and never sync, so all of this needs the server; the logic is BoardAutomations.kt.
//
// Opened with no board (from Reminders or Calendar) it's the planner: rules that add reminders and
// events to a space, on a schedule or when the weather turns, built from the no-card blocks.

private val SWATCHES = listOf("#39ff9c", "#4be7ff", "#ffb545", "#c792ea", "#ff7eb6", "#82aaff", "#f78c6c")

// One placed block, with a key that stays with it while the list is reordered - so each block's
// own text boxes (a comma-separated list being typed, say) keep their state.
private data class Placed(val key: Long, val block: RuleBlock)

private class RuleEditorState(val base: AutomationRule) {
    private var nextKey = 0L
    fun newKey() = nextKey++
    var name by mutableStateOf(base.name)
    // Who a planner rule's reminders and events are for. Null on a board rule.
    var spaceId by mutableStateOf(base.spaceId)
    var trigger by mutableStateOf(base.trigger)
    var triggerKey by mutableStateOf(newKey())
    val conditions = mutableStateListOf<Placed>().apply { base.conditions.forEach { add(Placed(newKey(), it)) } }
    val actions = mutableStateListOf<Placed>().apply { base.actions.forEach { add(Placed(newKey(), it)) } }
    // Where a tapped {token} goes: the action's key and the field.
    var tokenTarget by mutableStateOf<Pair<Long, String>?>(null)
    var errors by mutableStateOf<List<String>>(emptyList())
    var saving by mutableStateOf(false)

    fun rule(): AutomationRule = base.copy(name = name, spaceId = spaceId, trigger = trigger, conditions = conditions.map { it.block }, actions = actions.map { it.block })
}

// Everything the editor looks things up in: the board's lists, the people, the words. With no
// board it's the planner's: no lists, and the spaces its reminders can go to.
private class BoardLookup(val engine: OrganizerEngine, val board: OrganizerItem?, val people: List<OrganizerPerson>, val words: AutomationWords, val formatTime: (LocalTime) -> String) {
    val planner: Boolean get() = board == null
    val title: String = board?.title?.ifEmpty { "Board" } ?: BoardAutomations.PLANNER_TITLE
    val spaces: List<OrganizerSpace> = BoardAutomations.plannerSpaces(engine.visibleSpaces)
    val columns: List<OrganizerItem> = board?.let { engine.children(it.id, OrganizerKinds.COLUMN).sortedBy { c -> c.sortPosition } } ?: emptyList()
    val context = RuleContext(columns.firstOrNull()?.id ?: "", columns.lastOrNull()?.id ?: "", people, ZoneId.systemDefault().id)

    fun spaceWords(id: String?): String = BoardAutomations.spaceWords(spaces.firstOrNull { it.id == id })

    // Other boards' lists, for "move it to another board".
    fun otherBoards(): List<Pair<OrganizerItem, List<OrganizerItem>>> =
        engine.items(OrganizerKinds.BOARD).filter { it.id != board?.id && !it.bool("archived") }.sortedBy { it.sortPosition }
            .map { it to engine.children(it.id, OrganizerKinds.COLUMN).sortedBy { c -> c.sortPosition } }
            .filter { it.second.isNotEmpty() }

    val knownLabels: List<String> by lazy {
        columns.flatMap { engine.children(it.id, OrganizerKinds.CARD) }
            .flatMap { card -> card.list("labels").mapNotNull { (it as? Map<*, *>)?.get("name") as? String } }
            .filter { it.isNotEmpty() }.distinct().sorted()
    }

    fun formatWhen(at: Instant): String {
        val local = at.atZone(ZoneId.systemDefault())
        return "${local.format(DateTimeFormatter.ofPattern("EEE, MMM d", Locale.getDefault()))}, ${formatTime(local.toLocalTime())}"
    }
}

// `board` null: the planner's rules (Reminders & Calendar) instead of one board's.
@Composable
fun BoardAutomationsDialog(engine: OrganizerEngine, board: OrganizerItem?, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val revision = rememberOrganizerRevision(engine)
    var people by remember { mutableStateOf<List<OrganizerPerson>>(emptyList()) }
    var list by remember { mutableStateOf<AutomationList?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var editor by remember { mutableStateOf<RuleEditorState?>(null) }

    val is24 = android.text.format.DateFormat.is24HourFormat(context)
    val formatTime: (LocalTime) -> String = { it.format(DateTimeFormatter.ofPattern(if (is24) "H:mm" else "h:mm a", Locale.getDefault())) }
    val lookup = remember(board?.id, people, revision) {
        val words = AutomationWords(
            columnTitle = { id -> engine.item(id)?.takeIf { it.kind == OrganizerKinds.COLUMN }?.title },
            people = people,
            time = formatTime,
        )
        BoardLookup(engine, board, people, words, formatTime)
    }
    fun toast(message: String) = Toast.makeText(context, message, Toast.LENGTH_LONG).show()

    suspend fun reload() {
        try {
            list = engine.automations.list(board?.id)
            loadError = null
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            loadError = BoardAutomationClient.errors(error).joinToString("\n")
        }
    }

    LaunchedEffect(board?.id) {
        if (engine.status.value?.personalSpaces == true) people = runCatching { engine.people() }.getOrDefault(emptyList())
        reload()
    }

    Dialog(
        onDismissRequest = { if (editor != null) editor = null else onDismiss() },
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            val editing = editor
            if (editing == null) {
                RuleList(
                    lookup = lookup,
                    list = list,
                    loadError = loadError,
                    onClose = onDismiss,
                    onNew = { rule -> editor = RuleEditorState(BoardAutomations.draft(rule, board?.id, lookup.spaces)) },
                    onEdit = { rule -> editor = RuleEditorState(BoardAutomations.draft(rule, board?.id, lookup.spaces)) },
                    onEnabled = { rule, enabled ->
                        scope.launch {
                            try {
                                engine.automations.save(rule.copy(enabled = enabled))
                            } catch (error: CancellationException) {
                                throw error
                            } catch (error: Exception) {
                                toast(BoardAutomationClient.errors(error).joinToString(" "))
                            }
                            reload()
                        }
                    },
                    onRun = { rule ->
                        scope.launch {
                            try {
                                val run = engine.automations.run(rule.id ?: return@launch)
                                toast(run.message.ifEmpty { "Done." })
                                // So the card (or reminder) it made shows now, not at the next sync.
                                runCatching { engine.sync() }
                                reload()
                            } catch (error: CancellationException) {
                                throw error
                            } catch (error: Exception) {
                                toast(BoardAutomationClient.errors(error).joinToString(" "))
                            }
                        }
                    },
                    onDelete = { rule ->
                        scope.launch {
                            try {
                                engine.automations.delete(rule.id ?: return@launch)
                            } catch (error: CancellationException) {
                                throw error
                            } catch (error: Exception) {
                                toast(BoardAutomationClient.errors(error).joinToString(" "))
                            }
                            reload()
                        }
                    },
                )
            } else {
                RuleEditor(
                    state = editing,
                    lookup = lookup,
                    pushReady = list?.pushReady != false,
                    emailReady = list?.emailReady != false,
                    onBack = { editor = null },
                    onToast = ::toast,
                    onSave = {
                        val rule = editing.rule()
                        val local = BoardAutomations.localErrors(rule, lookup.planner)
                        if (local.isNotEmpty()) {
                            editing.errors = local
                        } else if (!editing.saving) {
                            editing.saving = true
                            scope.launch {
                                try {
                                    engine.automations.save(BoardAutomations.forSaving(rule, ZoneId.systemDefault().id))
                                    toast("Rule saved.")
                                    editor = null
                                    reload()
                                } catch (error: CancellationException) {
                                    throw error
                                } catch (error: Exception) {
                                    editing.errors = BoardAutomationClient.errors(error)
                                } finally {
                                    editing.saving = false
                                }
                            }
                        }
                    },
                )
            }
        }
    }
}

// ---- the list of rules -------------------------------------------------------------------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun RuleList(
    lookup: BoardLookup,
    list: AutomationList?,
    loadError: String?,
    onClose: () -> Unit,
    onNew: (AutomationRule) -> Unit,
    onEdit: (AutomationRule) -> Unit,
    onEnabled: (AutomationRule, Boolean) -> Unit,
    onRun: (AutomationRule) -> Unit,
    onDelete: (AutomationRule) -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Automations · ${lookup.title}", onClose)
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            when {
                loadError != null -> {
                    Text(loadError, color = SentinelTextDim)
                    return@Column
                }
                list == null -> {
                    Text("loading…", color = SentinelTextDim)
                    return@Column
                }
            }
            Text(if (lookup.planner) BoardAutomations.PLANNER_INTRO else BoardAutomations.BOARD_INTRO,
                color = SentinelTextDim, style = MaterialTheme.typography.bodySmall)
            Button(onClick = { onNew(AutomationRule()) }) { Text("+ Build a rule") }
            Text("or start from a recipe:", color = SentinelTextDim, style = MaterialTheme.typography.labelMedium)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                val recipes = if (lookup.planner) BoardAutomations.plannerRecipes(lookup.spaces, lookup.context.timeZone)
                    else BoardAutomations.recipes(lookup.context, lookup.words)
                recipes.forEach { recipe ->
                    Column(
                        Modifier.clip(RoundedCornerShape(10.dp)).border(1.dp, SentinelCyan.copy(alpha = 0.4f), RoundedCornerShape(10.dp))
                            .clickable { onNew(recipe.rule) }.padding(horizontal = 10.dp, vertical = 6.dp),
                    ) {
                        Text(recipe.name, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.bodyMedium)
                        Text(recipe.why, color = SentinelTextDim, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
            HorizontalDivider()
            val rules = list!!.rules
            if (rules.isEmpty()) Text("No rules yet. Build one, or pick a recipe above and change it to suit.", color = SentinelTextDim)
            rules.forEach { rule ->
                key(rule.id) { RuleRow(lookup, rule, onEdit, onEnabled, onRun, onDelete) }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun RuleRow(
    lookup: BoardLookup,
    rule: AutomationRule,
    onEdit: (AutomationRule) -> Unit,
    onEnabled: (AutomationRule, Boolean) -> Unit,
    onRun: (AutomationRule) -> Unit,
    onDelete: (AutomationRule) -> Unit,
) {
    val engine = lookup.engine
    var history by remember { mutableStateOf<List<AutomationRun>?>(null) }
    var historyOpen by remember { mutableStateOf(false) }
    var historyError by remember { mutableStateOf<String?>(null) }
    var confirmDelete by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val dim = if (rule.enabled) 1f else 0.55f

    Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Row(Modifier.weight(1f).graphicsLayer { alpha = dim }, verticalAlignment = Alignment.CenterVertically) {
                    Text(rule.name, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f, fill = false))
                    if (lookup.planner) Text(" · for ${lookup.spaceWords(rule.spaceId)}", color = SentinelTextDim, style = MaterialTheme.typography.bodySmall)
                }
                Switch(checked = rule.enabled, onCheckedChange = { onEnabled(rule, it) })
            }
            Text(lookup.words.sentence(rule), style = MaterialTheme.typography.bodySmall, modifier = Modifier.graphicsLayer { alpha = dim })
            FlowRow(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                val next = rule.nextRunAt
                if (rule.isScheduled && rule.enabled && next != null) Text("next: ${lookup.formatWhen(next)}", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                val last = rule.lastRunAt
                if (last != null) {
                    val ok = rule.lastOk == true
                    Text("${if (ok) "✓" else "!"} ${lookup.formatWhen(last)}: ${rule.lastResult.orEmpty()}", style = MaterialTheme.typography.labelSmall,
                        color = if (ok) SentinelGreen else SentinelRed)
                } else {
                    Text("hasn't run yet", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                }
            }
            FlowRow {
                // A weather rule runs its steps as if the weather had turned; ONLY IF tests still apply.
                if (rule.startsWithoutCard) TextButton(onClick = { onRun(rule) }) { Text("Run now") }
                TextButton(onClick = { onEdit(rule) }) { Text("Edit") }
                TextButton(onClick = {
                    historyOpen = !historyOpen
                    if (historyOpen) {
                        history = null
                        historyError = null
                        scope.launch {
                            try {
                                history = engine.automations.runs(rule.id ?: return@launch)
                            } catch (error: CancellationException) {
                                throw error
                            } catch (error: Exception) {
                                historyError = BoardAutomationClient.errors(error).joinToString(" ")
                            }
                        }
                    }
                }) { Text("History") }
                TextButton(onClick = { confirmDelete = true }) { Text("Delete", color = SentinelRed) }
            }
            if (historyOpen) {
                val runs = history
                when {
                    historyError != null -> Text(historyError!!, style = MaterialTheme.typography.bodySmall, color = SentinelRed)
                    runs == null -> Text("loading…", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
                    runs.isEmpty() -> Text("Nothing yet.", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
                    else -> runs.forEach { run ->
                        Text("${if (run.ok) "✓" else "!"} ${run.at?.let(lookup::formatWhen) ?: ""} - ${run.message}",
                            style = MaterialTheme.typography.bodySmall, color = if (run.ok) MaterialTheme.colorScheme.onSurface else SentinelRed)
                    }
                }
            }
        }
    }
    if (confirmDelete) AlertDialog(
        onDismissRequest = { confirmDelete = false },
        title = { Text("Delete the rule \"${rule.name}\"?") },
        text = { Text(if (lookup.planner) BoardAutomations.PLANNER_DELETE_NOTE else BoardAutomations.DELETE_NOTE) },
        confirmButton = { TextButton(onClick = { confirmDelete = false; onDelete(rule) }) { Text("Delete", color = SentinelRed) } },
        dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
    )
}

// ---- the editor --------------------------------------------------------------------------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun RuleEditor(
    state: RuleEditorState,
    lookup: BoardLookup,
    pushReady: Boolean,
    emailReady: Boolean,
    onBack: () -> Unit,
    onToast: (String) -> Unit,
    onSave: () -> Unit,
) {
    val trigger = state.trigger

    fun addBlock(zone: RuleZone, type: String) {
        val kind = BoardAutomations.kind(zone, type) ?: return
        val block = BoardAutomations.make(zone, type, lookup.context, state.trigger) ?: return
        when (zone) {
            RuleZone.TRIGGER -> {
                state.trigger = block
                state.triggerKey = state.newKey()
                if (state.name.isEmpty()) state.name = kind.label
            }
            RuleZone.CONDITIONS -> state.conditions.add(Placed(state.newKey(), block))
            RuleZone.ACTIONS -> state.actions.add(Placed(state.newKey(), block))
        }
    }

    fun insertToken(token: String) {
        val target = state.tokenTarget?.takeIf { (key, _) -> state.actions.any { it.key == key } }
            ?: state.actions.firstOrNull { it.block.type in BoardAutomations.TOKEN_FIELDS }?.let { it.key to BoardAutomations.TOKEN_FIELDS.getValue(it.block.type).first() }
        if (target == null) {
            onToast(if (lookup.planner) "Add a reminder, event or message first, then drop the word into it." else "Add a card title or comment first, then drop the word into it.")
            return
        }
        val index = state.actions.indexOfFirst { it.key == target.first }
        val placed = state.actions[index]
        state.actions[index] = placed.copy(block = placed.block.with(target.second, placed.block.string(target.second).orEmpty() + token))
        state.tokenTarget = target
    }

    Column(Modifier.fillMaxSize().imePadding()) {
        DetailHeader("${if (state.base.id != null) "Edit rule" else "New rule"} · ${lookup.title}", onBack) {
            TextButton(onClick = onSave, enabled = !state.saving) { Text("Save") }
        }
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(state.name, { state.name = it }, label = { Text("Name") },
                placeholder = { Text(if (lookup.planner) "e.g. Water the plants" else "e.g. Eggs every morning") },
                singleLine = true, modifier = Modifier.fillMaxWidth())
            if (lookup.planner) {
                ChoiceMenu("Reminders and events go to", lookup.spaceWords(state.spaceId), lookup.spaces.map { it.id to BoardAutomations.spaceWords(it) }) {
                    state.spaceId = it
                }
                Hint("- everyone and admins reminders show on the wall kiosk when due")
            }

            // WHEN
            SlotTitle("WHEN", SentinelAmber, null)
            if (trigger == null) {
                Placeholder("add a WHEN block - what starts the rule?")
            } else {
                key(state.triggerKey) {
                    BlockCard(BoardAutomations.kind(RuleZone.TRIGGER, trigger.type), trigger.type, SentinelAmber, warning = null,
                        onRemove = { state.trigger = null }) {
                        TriggerFields(trigger, lookup) { state.trigger = it }
                    }
                }
            }
            Palette(RuleZone.TRIGGER, if (trigger == null) "＋ Add a WHEN block" else "Change the WHEN block", lookup, startOpen = trigger == null) {
                addBlock(RuleZone.TRIGGER, it)
            }

            // ONLY IF
            SlotTitle("ONLY IF", SentinelCyan, "(optional)")
            state.conditions.forEachIndexed { index, placed ->
                key(placed.key) {
                    BlockCard(BoardAutomations.kind(RuleZone.CONDITIONS, placed.block.type), placed.block.type, SentinelCyan,
                        warning = BoardAutomations.conditionWarning(trigger, placed.block),
                        onRemove = { state.conditions.removeAt(index) }) {
                        ConditionFields(placed.block, lookup) { state.conditions[index] = placed.copy(block = it) }
                    }
                }
            }
            if (state.conditions.isEmpty()) Placeholder("add ONLY IF blocks here to narrow it down")
            Palette(RuleZone.CONDITIONS, if (state.conditions.isEmpty()) "＋ Add an ONLY IF block" else "＋ Add another to narrow it further", lookup, startOpen = false) {
                addBlock(RuleZone.CONDITIONS, it)
            }

            // THEN - in order; hold the grip and drag, or use the arrows.
            SlotTitle("THEN", SentinelGreen, "(in this order - hold ⋮⋮ and drag, or use the arrows)")
            ActionList(state, lookup, pushReady, emailReady)
            if (state.actions.isEmpty()) Placeholder("add THEN blocks here - what should happen?")
            Palette(RuleZone.ACTIONS, if (state.actions.isEmpty()) "＋ Add a THEN block" else "＋ Add another step", lookup, startOpen = trigger != null && state.actions.isEmpty()) {
                addBlock(RuleZone.ACTIONS, it)
            }

            // {tokens}
            Text("WORDS THAT FILL THEMSELVES IN", color = SentinelTextDim, style = MaterialTheme.typography.labelMedium)
            val tokens = BoardAutomations.tokens(lookup.planner)
            Text("Tap one to add it to the ${if (lookup.planner) "title" else "card title"} or message you were last typing in: " +
                tokens.joinToString(", ") { (token, what) -> "$token is $what" } + ".",
                color = SentinelTextDim, style = MaterialTheme.typography.bodySmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                tokens.forEach { (token, _) ->
                    FilterChip(selected = false, onClick = { insertToken(token) }, label = { Text(token) })
                }
            }

            HorizontalDivider()
            Text("In plain words:", color = SentinelTextDim, style = MaterialTheme.typography.labelMedium)
            Text(lookup.words.draftSentence(state.rule()))
            state.errors.forEach { Text("• $it", color = SentinelRed) }
            Button(onClick = onSave, enabled = !state.saving, modifier = Modifier.fillMaxWidth()) { Text(if (state.saving) "Saving…" else "Save rule") }
            Spacer(Modifier.height(40.dp))
        }
    }
}

// The THEN steps. Long-press the grip and drag to reorder: once the dragged step travels past
// most of its neighbour the two trade places and the offset resets, so it follows the finger
// (the same swap as the device hub's list). The arrows always work too.
@Composable
private fun ActionList(state: RuleEditorState, lookup: BoardLookup, pushReady: Boolean, emailReady: Boolean) {
    val blocks = state.actions.map { it.block }
    val weatherInRule = BoardAutomations.weatherInRule(state.trigger, state.conditions.map { it.block })
    var dragging by remember { mutableStateOf<Long?>(null) }
    var offset by remember { mutableStateOf(0f) }
    val heights = remember { mutableStateMapOf<Long, Int>() }
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        state.actions.forEachIndexed { index, placed ->
            key(placed.key) {
                val isDragging = dragging == placed.key
                val grip = Modifier.pointerInput(placed.key) {
                    detectDragGesturesAfterLongPress(
                        onDragStart = { dragging = placed.key; offset = 0f },
                        onDrag = { change, amount ->
                            change.consume()
                            offset += amount.y
                            val current = state.actions.indexOfFirst { it.key == placed.key }
                            if (current < 0) return@detectDragGesturesAfterLongPress
                            val below = state.actions.getOrNull(current + 1)?.let { heights[it.key] }
                            val above = state.actions.getOrNull(current - 1)?.let { heights[it.key] }
                            if (below != null && offset > below * 0.66f) {
                                state.actions.add(current + 1, state.actions.removeAt(current))
                                offset -= below
                            } else if (above != null && offset < -above * 0.66f) {
                                state.actions.add(current - 1, state.actions.removeAt(current))
                                offset += above
                            }
                        },
                        onDragEnd = { dragging = null; offset = 0f },
                        onDragCancel = { dragging = null; offset = 0f },
                    )
                }
                Box(
                    Modifier.zIndex(if (isDragging) 1f else 0f)
                        .graphicsLayer { translationY = if (isDragging) offset else 0f; shadowElevation = if (isDragging) 12f else 0f }
                        .onGloballyPositioned { heights[placed.key] = it.size.height },
                ) {
                    BlockCard(
                        BoardAutomations.kind(RuleZone.ACTIONS, placed.block.type), placed.block.type, SentinelGreen,
                        warning = BoardAutomations.actionWarning(state.trigger, blocks, index),
                        onRemove = { state.actions.removeAt(index) },
                        onUp = if (index > 0) ({ state.actions.add(index - 1, state.actions.removeAt(index)) }) else null,
                        onDown = if (index < state.actions.lastIndex) ({ state.actions.add(index + 1, state.actions.removeAt(index)) }) else null,
                        grip = grip,
                    ) {
                        ActionFields(placed.block, lookup, pushReady, emailReady, weatherInRule,
                            onTokenFocus = { field -> state.tokenTarget = placed.key to field }) {
                            val at = state.actions.indexOfFirst { p -> p.key == placed.key }
                            if (at >= 0) state.actions[at] = state.actions[at].copy(block = it)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SlotTitle(title: String, color: Color, note: String?) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp)) {
        Text(title, color = color, fontWeight = FontWeight.Bold, style = MaterialTheme.typography.labelLarge)
        if (note != null) Text("  $note", color = SentinelTextDim, style = MaterialTheme.typography.labelSmall)
    }
}

@Composable
private fun Placeholder(text: String) {
    Text(text, color = SentinelTextDim, style = MaterialTheme.typography.bodySmall,
        modifier = Modifier.fillMaxWidth().border(1.dp, SentinelTextDim.copy(alpha = 0.4f), RoundedCornerShape(8.dp)).padding(12.dp))
}

// The blocks for one part of the rule; tap one to put it in.
@Composable
private fun Palette(zone: RuleZone, title: String, lookup: BoardLookup, startOpen: Boolean, onAdd: (String) -> Unit) {
    var open by remember { mutableStateOf(startOpen) }
    TextButton(onClick = { open = !open }) { Text(if (open) "Hide the blocks" else title) }
    if (!open) return
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))) {
        BoardAutomations.palette(zone, lookup.people, lookup.planner).forEach { kind ->
            Row(
                Modifier.fillMaxWidth().clickable { onAdd(kind.type); open = false }.padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(kind.icon, modifier = Modifier.width(32.dp))
                Column {
                    Text(kind.label, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.bodyMedium)
                    kind.hint?.let { Text(it, color = SentinelTextDim, style = MaterialTheme.typography.bodySmall) }
                }
            }
        }
    }
}

// A placed block: its head (grip, icon, name, arrows, remove) over its blanks.
@Composable
private fun BlockCard(
    kind: RuleBlockKind?,
    type: String,
    accent: Color,
    warning: String?,
    onRemove: () -> Unit,
    onUp: (() -> Unit)? = null,
    onDown: (() -> Unit)? = null,
    grip: Modifier? = null,
    content: @Composable () -> Unit,
) {
    val edge = if (warning != null) SentinelAmber else accent.copy(alpha = 0.5f)
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant)
            .border(1.dp, edge, RoundedCornerShape(10.dp)).padding(start = 10.dp, end = 4.dp, top = 4.dp, bottom = 10.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (grip != null) Icon(Icons.Default.DragIndicator, contentDescription = "Hold and drag to reorder", tint = SentinelTextDim,
                modifier = grip.size(28.dp).padding(end = 4.dp))
            Text(kind?.icon ?: "?", modifier = Modifier.width(28.dp))
            Text(kind?.label ?: "“$type”", fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            if (onUp != null) IconButton(onClick = onUp) { Icon(Icons.Default.KeyboardArrowUp, contentDescription = "Move up") }
            if (onDown != null) IconButton(onClick = onDown) { Icon(Icons.Default.KeyboardArrowDown, contentDescription = "Move down") }
            IconButton(onClick = onRemove) { Icon(Icons.Default.Close, contentDescription = "Remove") }
        }
        Column(Modifier.padding(end = 6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            if (kind == null) Text("This phone doesn't know this block yet - it's kept as it is.", color = SentinelTextDim, style = MaterialTheme.typography.bodySmall)
            else content()
            warning?.let { Text(it, color = SentinelAmber, style = MaterialTheme.typography.bodySmall) }
        }
    }
}

// ---- the blanks in each block ------------------------------------------------------------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TriggerFields(t: RuleBlock, lookup: BoardLookup, onChange: (RuleBlock) -> Unit) {
    when (t.type) {
        "schedule" -> {
            val s = t.schedule
            val mode = BoardAutomations.scheduleMode(s)
            ChoiceMenu("Repeat", BoardAutomations.SCHEDULE_MODES.firstOrNull { it.first == mode }?.second ?: mode, BoardAutomations.SCHEDULE_MODES) {
                onChange(t.withSchedule(BoardAutomations.applyScheduleMode(s, it, LocalDate.now())))
            }
            when (mode) {
                "days" -> DaysPicker(s.ints("days")) { onChange(t.withSchedule(s.with("days", it))) }
                "monthly" -> {
                    val day = s.int("dayOfMonth")
                    val options = (1..31).map { it to "day $it" } + (-1 to "the last day")
                    ChoiceMenu("On", if (day == null || day == -1 || day == 0) "the last day" else "day $day", options) {
                        onChange(t.withSchedule(s.with("dayOfMonth", it)))
                    }
                }
                "hourly" -> {
                    val every = s.int("everyHours")?.takeIf { it != 0 } ?: 1
                    ChoiceMenu("Every", "$every hour${if (every == 1) "" else "s"} starting", BoardAutomations.EVERY_HOURS.map { it to "$it hour${if (it == 1) "" else "s"}" }) {
                        onChange(t.withSchedule(s.with("everyHours", it)))
                    }
                }
                "once" -> {
                    val context = LocalContext.current
                    val date = s.string("date")?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("On", modifier = Modifier.weight(1f))
                        TextButton(onClick = { pickDate(context, date ?: LocalDate.now().plusDays(1)) { onChange(t.withSchedule(s.with("date", it.toString()))) } }) {
                            Text(date?.format(DateTimeFormatter.ofPattern("EEE, MMM d, yyyy", Locale.getDefault())) ?: "pick a date")
                        }
                    }
                }
            }
            TimeRow("At", s.string("time")?.ifEmpty { null } ?: "07:00", lookup, allowNone = false) { onChange(t.withSchedule(s.with("time", it))) }
            SchedulePreview(s, lookup)
        }
        "weather" -> {
            WeatherFields(t.weather) { onChange(t.withWeather(it)) }
            Hint(BoardAutomations.WEATHER_TRIGGER_NOTE)
        }
        "cardAdded" -> ColumnPicker("To", t.string("columnId"), allowAny = true, lookup) { onChange(t.with("columnId", it)) }
        "cardMoved" -> ColumnPicker("Into", t.string("columnId"), allowAny = true, lookup) { onChange(t.with("columnId", it)) }
        "cardDue" -> {
            val minutes = t.int("minutesBefore") ?: 0
            ChoiceMenu("When", BoardAutomations.DUE_OFFSETS.firstOrNull { it.first == minutes }?.second ?: "$minutes minutes before", BoardAutomations.DUE_OFFSETS) {
                onChange(t.with("minutesBefore", it))
            }
            ColumnPicker("For cards in", t.string("columnId"), allowAny = true, lookup) { onChange(t.with("columnId", it)) }
            Hint("A card with a date but no time counts as due at 9:00 AM that day. Finished cards are left alone.")
        }
        "labelAdded" -> LabelField("Called", t.string("label"), "any label", lookup) { onChange(t.with("label", it)) }
    }
}

// The next few times it will run, from the server, so they're what will really happen.
@Composable
private fun SchedulePreview(schedule: RuleBlock, lookup: BoardLookup) {
    var text by remember { mutableStateOf("") }
    var bad by remember { mutableStateOf(false) }
    LaunchedEffect(schedule) {
        delay(250)
        try {
            val runs = lookup.engine.automations.preview(schedule)
            bad = runs.isEmpty()
            text = if (runs.isEmpty()) "this won't run - that time has passed" else "next: ${runs.joinToString("  ·  ") { lookup.formatWhen(it) }}"
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            text = ""
        }
    }
    if (text.isNotEmpty()) Text(text, color = if (bad) SentinelRed else SentinelTextDim, style = MaterialTheme.typography.bodySmall)
}

@Composable
private fun ConditionFields(c: RuleBlock, lookup: BoardLookup, onChange: (RuleBlock) -> Unit) {
    when (c.type) {
        "titleContains" -> BagText("The card's title contains", c.string("text"), "e.g. asap") { onChange(c.with("text", it)) }
        "hasLabel" -> LabelField("The card has the label", c.string("text"), "label", lookup) { onChange(c.with("text", it)) }
        "lacksLabel" -> LabelField("The card does not have the label", c.string("text"), "label", lookup) { onChange(c.with("text", it)) }
        "inColumn" -> ColumnPicker("The card is in", c.string("columnId"), allowAny = false, lookup) { onChange(c.with("columnId", it)) }
        "onDays" -> {
            Text("Today is", style = MaterialTheme.typography.bodySmall)
            DaysPicker(c.ints("days")) { onChange(c.with("days", it)) }
        }
        "weather" -> WeatherFields(c, onChange)
    }
}

// The blanks of a weather test: what's measured, over how long, which way, and the number.
@Composable
private fun WeatherFields(w: RuleBlock, onChange: (RuleBlock) -> Unit) {
    val m = BoardAutomations.metric(w.string("metric")) ?: BoardAutomations.METRICS.first()
    ChoiceMenu("The", m.label, BoardAutomations.METRICS.map { it.key to it.label }) { onChange(BoardAutomations.withMetric(w, it)) }
    if (!m.now) {
        val window = w.int("window") ?: 1
        ChoiceMenu("Over", m.windowLabel(window), m.windows.map { it to m.windowLabel(it) }) { onChange(w.with("window", it)) }
    }
    val op = if (w.string("op") == "atMost") "atMost" else "atLeast"
    ChoiceMenu("Is", BoardAutomations.WEATHER_OPS.first { it.first == op }.second, BoardAutomations.WEATHER_OPS) { onChange(w.with("op", it)) }
    // Kept as typed ("0." on the way to "0.5"); a new measure starts the box again from its own number.
    var text by remember(m.key) { mutableStateOf(w.number("value")?.let(::jsNumber).orEmpty()) }
    OutlinedTextField(
        value = text,
        onValueChange = { typed ->
            text = typed
            onChange(w.with("value", typed.trim().replace(',', '.').toDoubleOrNull()))
        },
        label = { Text("Number") },
        trailingIcon = { Text(m.unit, color = SentinelTextDim, modifier = Modifier.padding(end = 12.dp)) },
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
        isError = text.isNotBlank() && text.trim().replace(',', '.').toDoubleOrNull() == null,
        singleLine = true,
        modifier = Modifier.fillMaxWidth(),
    )
    Hint(m.source)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ActionFields(a: RuleBlock, lookup: BoardLookup, pushReady: Boolean, emailReady: Boolean, weatherInRule: Boolean, onTokenFocus: (String) -> Unit, onChange: (RuleBlock) -> Unit) {
    val people = lookup.people
    when (a.type) {
        "createReminder" -> {
            BagText("Called", a.string("title"), "e.g. water the plants", onFocus = { onTokenFocus("title") }) { onChange(a.with("title", it)) }
            val due = a.int("inDays") != null
            CheckRow("Due", due) { on -> onChange(if (on) a.with("inDays", 0) else a.with("inDays", null).with("time", "")) }
            if (due) {
                WhenFields(a, lookup, weatherInRule, onChange)
                AlertMenu(a, onChange)
            }
            ChoiceMenu("Priority", BoardAutomations.PRIORITIES.firstOrNull { it.first == (a.int("priority") ?: 0) }?.second ?: "none", BoardAutomations.PRIORITIES) {
                onChange(a.with("priority", it))
            }
            BagText("Notes", a.string("description"), "optional", onFocus = { onTokenFocus("description") }) { onChange(a.with("description", it)) }
            CheckRow("Skip if an unfinished reminder with this title is already there", a.bool("skipIfExists")) { onChange(a.with("skipIfExists", it)) }
        }
        "createEvent" -> {
            BagText("Called", a.string("title"), "e.g. cover the plants", onFocus = { onTokenFocus("title") }) { onChange(a.with("title", it)) }
            WhenFields(a, lookup, weatherInRule, onChange)
            if (a.string("time").isNullOrEmpty()) Hint("No time makes it an all-day event.")
            else {
                val minutes = a.int("durationMinutes") ?: 60
                ChoiceMenu("For", BoardAutomations.DURATIONS.firstOrNull { it.first == minutes }?.second ?: "$minutes minutes", BoardAutomations.DURATIONS) {
                    onChange(a.with("durationMinutes", it))
                }
            }
            AlertMenu(a, onChange)
            BagText("Where", a.string("location"), "optional") { onChange(a.with("location", it)) }
            BagText("Notes", a.string("description"), "optional", onFocus = { onTokenFocus("description") }) { onChange(a.with("description", it)) }
            CheckRow("Skip if an event with this title is already on that day", a.bool("skipIfExists")) { onChange(a.with("skipIfExists", it)) }
        }
        "createCard" -> {
            BagText("Called", a.string("title"), "e.g. pick up eggs", onFocus = { onTokenFocus("title") }) { onChange(a.with("title", it)) }
            ColumnPicker("In", a.string("columnId"), allowAny = false, lookup) { onChange(a.with("columnId", it)) }
            ChoiceMenu("At the", if (a.string("position") == "top") "top" else "bottom", listOf("bottom" to "bottom", "top" to "top")) { onChange(a.with("position", it)) }
            CheckRow("Skip if an unfinished card with this title is already there", a.bool("skipIfExists")) { onChange(a.with("skipIfExists", it)) }
            DueFields(a, lookup, "Due", onChange)
            CsvField("Labels", a.strings("labels"), "comma separated") { onChange(a.with("labels", it)) }
            LinesField("Checklist", a.strings("items")) { onChange(a.with("items", it)) }
            if (people.isNotEmpty()) {
                val who = a.string("username").orEmpty()
                ChoiceMenu("Assign to", people.firstOrNull { it.username == who }?.label ?: "nobody", listOf("" to "nobody") + people.map { it.username to it.label }) {
                    onChange(a.with("username", it))
                }
            }
        }
        "moveCard" -> {
            ColumnPicker("To", a.string("columnId"), allowAny = false, lookup) { onChange(a.with("columnId", it)) }
            ChoiceMenu("At the", if (a.string("position") == "bottom") "bottom" else "top", listOf("top" to "top", "bottom" to "bottom")) { onChange(a.with("position", it)) }
        }
        "addLabel" -> {
            LabelField("Label", a.string("name"), "label", lookup) { onChange(a.with("name", it)) }
            ColourRow(a.string("color").orEmpty()) { onChange(a.with("color", it)) }
        }
        "removeLabel" -> LabelField("Label", a.string("name"), "label", lookup) { onChange(a.with("name", it)) }
        "setDue" -> DueFields(a, lookup, "Set one (untick to clear it)", onChange)
        "complete" -> ChoiceMenu("As", if (a.bool("done", default = true)) "done" else "not done", listOf(true to "done", false to "not done")) { onChange(a.with("done", it)) }
        "addChecklist" -> LinesField("Items", a.strings("items")) { onChange(a.with("items", it)) }
        "resetChecklist" -> Hint("so it can be done again")
        "addComment" -> BagText("Comment", a.string("text"), "e.g. finished on {date}", onFocus = { onTokenFocus("text") }) { onChange(a.with("text", it)) }
        "assign" -> {
            val who = a.string("username").orEmpty()
            ChoiceMenu("Assign to", people.firstOrNull { it.username == who }?.label ?: who.ifEmpty { "pick someone" }, people.map { it.username to it.label }) {
                onChange(a.with("username", it))
            }
        }
        "deleteCard" -> Hint("for good")
        "notify" -> {
            if (!pushReady) Warning("Push notifications aren't set up on this server yet (Settings → Push), so this won't reach anyone until they are.")
            Text("To", style = MaterialTheme.typography.bodySmall)
            PeopleChips(a, people, allowEveryone = true, onChange)
            BagText("Title", a.string("title"), "optional - the board's name if empty", onFocus = { onTokenFocus("title") }) { onChange(a.with("title", it)) }
            BagText("Message", a.string("text"), "e.g. {card} is due {due}", onFocus = { onTokenFocus("text") }) { onChange(a.with("text", it)) }
        }
        "email" -> {
            if (!emailReady) Warning("Email isn't set up on this server yet (Settings → Email), so nothing will be sent until it is.")
            if (people.isNotEmpty()) {
                Text("To", style = MaterialTheme.typography.bodySmall)
                PeopleChips(a, people, allowEveryone = false, onChange)
            }
            CsvField(if (people.isNotEmpty()) "And other addresses" else "To", a.strings("addresses"), "addresses, comma separated") { onChange(a.with("addresses", it)) }
            BagText("Subject", a.string("title"), "optional", onFocus = { onTokenFocus("title") }) { onChange(a.with("title", it)) }
            BagText("Message", a.string("text"), "e.g. {card} is due {due}", singleLine = false, onFocus = { onTokenFocus("text") }) { onChange(a.with("text", it)) }
        }
        "moveAllCards" -> {
            ColumnPicker("From", a.string("columnId"), allowAny = false, lookup) { onChange(a.with("columnId", it)) }
            ColumnPicker("To", a.string("toColumnId"), allowAny = false, lookup) { onChange(a.with("toColumnId", it)) }
            CheckRow("Only the ones marked done", a.bool("onlyCompleted")) { onChange(a.with("onlyCompleted", it)) }
        }
        "deleteAllCards" -> {
            ColumnPicker("List", a.string("columnId"), allowAny = false, lookup) { onChange(a.with("columnId", it)) }
            CheckRow("Only the cards marked done", a.bool("onlyCompleted")) { onChange(a.with("onlyCompleted", it)) }
        }
    }
}

// ---- small pieces ------------------------------------------------------------------------------

@Composable
private fun Hint(text: String) = Text(text, color = SentinelTextDim, style = MaterialTheme.typography.bodySmall)

@Composable
private fun Warning(text: String) = Text(text, color = SentinelAmber, style = MaterialTheme.typography.bodySmall)

@Composable
private fun CheckRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().clickable { onChange(!checked) }) {
        Checkbox(checked = checked, onCheckedChange = onChange)
        Text(label, style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
private fun BagText(label: String, value: String?, placeholder: String, singleLine: Boolean = true, onFocus: (() -> Unit)? = null, onChange: (String) -> Unit) {
    OutlinedTextField(
        value = value.orEmpty(),
        onValueChange = onChange,
        label = { Text(label) },
        placeholder = { Text(placeholder) },
        singleLine = singleLine,
        modifier = Modifier.fillMaxWidth().then(if (!singleLine) Modifier.heightIn(min = 90.dp) else Modifier)
            .onFocusChanged { if (it.isFocused) onFocus?.invoke() },
    )
}

// A list typed as "a, b, c". The text is kept as typed, so a trailing comma doesn't vanish mid-word.
@Composable
private fun CsvField(label: String, values: List<String>, placeholder: String, onChange: (List<String>) -> Unit) {
    var text by remember { mutableStateOf(values.joinToString(", ")) }
    OutlinedTextField(text, { typed ->
        text = typed
        onChange(typed.split(",").map { it.trim() }.filter { it.isNotEmpty() })
    }, label = { Text(label) }, placeholder = { Text(placeholder) }, singleLine = true, modifier = Modifier.fillMaxWidth())
}

@Composable
private fun LinesField(label: String, values: List<String>, onChange: (List<String>) -> Unit) {
    var text by remember { mutableStateOf(values.joinToString("\n")) }
    OutlinedTextField(text, { typed ->
        text = typed
        onChange(typed.split("\n").map { it.trim() }.filter { it.isNotEmpty() })
    }, label = { Text(label) }, placeholder = { Text("one item per line") }, modifier = Modifier.fillMaxWidth().heightIn(min = 80.dp))
}

// A label name, with the labels already on this board's cards a tap away.
@Composable
private fun LabelField(label: String, value: String?, placeholder: String, lookup: BoardLookup, onChange: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val known = lookup.knownLabels
    OutlinedTextField(
        value = value.orEmpty(), onValueChange = onChange, label = { Text(label) }, placeholder = { Text(placeholder) }, singleLine = true,
        modifier = Modifier.fillMaxWidth(),
        trailingIcon = if (known.isEmpty()) null else ({
            Box {
                IconButton(onClick = { open = true }) { Icon(Icons.Default.ArrowDropDown, contentDescription = "Labels on this board") }
                DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                    known.forEach { name -> DropdownMenuItem(text = { Text(name) }, onClick = { onChange(name); open = false }) }
                }
            }
        }),
    )
}

@Composable
private fun ColourRow(selected: String, onPick: (String) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Colour", style = MaterialTheme.typography.bodySmall)
        SWATCHES.forEach { hex ->
            val on = selected.equals(hex, ignoreCase = true)
            Box(Modifier.size(if (on) 28.dp else 24.dp).clip(CircleShape).background(parseHex(hex) ?: Color.Gray)
                .border(if (on) 2.dp else 0.dp, MaterialTheme.colorScheme.onSurface, CircleShape).clickable { onPick(hex) })
        }
        if (selected.isNotEmpty()) TextButton(onClick = { onPick("") }) { Text("none") }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun DaysPicker(days: List<Int>, onChange: (List<Int>) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        BoardAutomations.DAY_SHORT.forEachIndexed { day, name ->
            FilterChip(selected = day in days, onClick = { BoardAutomations.toggleDay(days, day)?.let(onChange) }, label = { Text(name) })
        }
    }
}

// Chips for choosing people. For a notification there's an "everyone" chip too (the only choice
// with login off); for email, people with no address on their account show but can't be picked.
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PeopleChips(a: RuleBlock, people: List<OrganizerPerson>, allowEveryone: Boolean, onChange: (RuleBlock) -> Unit) {
    val everyone = a.bool("everyone")
    val chosen = a.strings("people")
    FlowRow(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        if (allowEveryone) FilterChip(selected = everyone, enabled = people.isNotEmpty(), onClick = {
            onChange(a.with("everyone", !everyone).let { if (!everyone) it.with("people", emptyList<String>()) else it })
        }, label = { Text("everyone") })
        people.forEach { person ->
            val noEmail = !allowEveryone && !person.hasEmail
            val on = !everyone && person.username in chosen
            FilterChip(selected = on, enabled = !noEmail, onClick = {
                onChange(a.with("people", if (person.username in chosen) chosen - person.username else chosen + person.username).with("everyone", false))
            }, label = { Text(person.label + if (noEmail) " (no email)" else "") })
        }
    }
}

// "HH:mm" as a button that opens the clock; `allowNone` adds a way back to no time ("").
@Composable
private fun TimeRow(label: String, value: String, lookup: BoardLookup, allowNone: Boolean, onChange: (String) -> Unit) {
    val context = LocalContext.current
    val time = value.takeIf { it.isNotEmpty() }?.let { runCatching { LocalTime.parse(if (it.length == 4) "0$it" else it) }.getOrNull() }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        OutlinedButton(onClick = { pickTime(context, time ?: LocalTime.of(9, 0)) { onChange(it.format(DateTimeFormatter.ofPattern("HH:mm"))) } }) {
            Text(time?.let(lookup.formatTime) ?: "no time")
        }
        if (allowNone && time != null) IconButton(onClick = { onChange("") }) { Icon(Icons.Default.Close, contentDescription = "No time") }
    }
}

// The due toggle and, when it's on, how many days out and an optional time.
@Composable
private fun DueFields(a: RuleBlock, lookup: BoardLookup, toggleLabel: String, onChange: (RuleBlock) -> Unit) {
    val inDays = a.int("inDays")
    CheckRow(toggleLabel, inDays != null) { on -> onChange(if (on) a.with("inDays", 1) else a.with("inDays", null).with("time", "")) }
    if (inDays == null) return
    ChoiceMenu("Due", BoardAutomations.DUE_DAYS.firstOrNull { it.first == inDays }?.second ?: "in $inDays days", BoardAutomations.DUE_DAYS) {
        onChange(a.with("inDays", it))
    }
    TimeRow("At", a.string("time").orEmpty(), lookup, allowNone = true) { onChange(a.with("time", it)) }
}

// When a made reminder or event lands: days from today, or - in a rule with weather - from the
// weather's day ("the day before" a freeze), and an optional time.
@Composable
private fun WhenFields(a: RuleBlock, lookup: BoardLookup, weatherInRule: Boolean, onChange: (RuleBlock) -> Unit) {
    val inDays = a.int("inDays") ?: 0
    val fromWeather = a.bool("fromWeatherDay")
    val offsets = if (fromWeather) BoardAutomations.WEATHER_DAYS else BoardAutomations.DUE_DAYS
    ChoiceMenu("When", offsets.firstOrNull { it.first == inDays }?.second ?: lookup.words.reminderWhen(a.with("time", "")), offsets) {
        onChange(a.with("inDays", it))
    }
    if (weatherInRule) CheckRow("Counted from the day of the weather", fromWeather) { on ->
        onChange(a.with("fromWeatherDay", on).with("inDays", if (on) -1 else 0))
    }
    TimeRow("At", a.string("time").orEmpty(), lookup, allowNone = true) { onChange(a.with("time", it)) }
}

// The heads-up a made reminder or event gives before it's due.
@Composable
private fun AlertMenu(a: RuleBlock, onChange: (RuleBlock) -> Unit) {
    val minutes = a.int("alertMinutes")
    ChoiceMenu("Heads-up", BoardAutomations.ALERTS.firstOrNull { it.first == minutes }?.second ?: "$minutes minutes before", BoardAutomations.ALERTS) {
        onChange(a.with("alertMinutes", it))
    }
}

// A list picker: this board's lists first, then - for the pickers that can't be "any list" -
// other boards' lists, for "move it to another board".
@Composable
private fun ColumnPicker(label: String, selected: String?, allowAny: Boolean, lookup: BoardLookup, onPick: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val current = when {
        selected.isNullOrEmpty() -> if (allowAny) "any list" else "pick a list"
        else -> lookup.words.columnName(selected)
    }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Box {
            TextButton(onClick = { open = true }) { Text(current, color = if (selected.isNullOrEmpty() && !allowAny) SentinelAmber else Color.Unspecified) }
            DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                if (allowAny) DropdownMenuItem(text = { Text("any list") }, onClick = { onPick(""); open = false })
                lookup.columns.forEach { column ->
                    DropdownMenuItem(text = { Text(column.title.ifEmpty { "list" }) }, onClick = { onPick(column.id); open = false })
                }
                if (!allowAny) lookup.otherBoards().forEach { (board, columns) ->
                    DropdownMenuItem(text = { Text("on ${board.title.ifEmpty { "board" }}", color = SentinelCyan, style = MaterialTheme.typography.labelMedium) },
                        onClick = {}, enabled = false)
                    columns.forEach { column ->
                        DropdownMenuItem(text = { Text("   ${column.title.ifEmpty { "list" }}") }, onClick = { onPick(column.id); open = false })
                    }
                }
            }
        }
    }
}
