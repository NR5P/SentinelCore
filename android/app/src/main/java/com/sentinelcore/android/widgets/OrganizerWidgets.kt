package com.sentinelcore.android.widgets

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.LocalContext
import androidx.glance.LocalSize
import androidx.glance.action.Action
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.CheckBox
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.appWidgetBackground
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.lazy.LazyColumn
import androidx.glance.appwidget.lazy.items
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.color.ColorProvider
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import com.sentinelcore.android.MainActivity
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.time.temporal.ChronoUnit
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.update

// Home-screen widgets for the Organizer: Calendar, Reminders and Notes. They draw the snapshot
// the app writes (OrganizerWidgetSnapshot) and nothing else, so they work offline. Tapping one
// opens the app on that part of the Organizer; a reminder's checkbox checks it off right there -
// the widget records it and the app makes the real edit (OrganizerWidgetBridge).

object OrganizerWidgets {
    const val EXTRA_PANE = "organizerPane"

    // Bumped when a widget checks off a reminder, so a running app applies it at once. Widgets
    // run in the app's process, so a plain flow reaches the app when it's alive; otherwise the
    // check-off waits in the file for the next launch.
    val actionsPosted = MutableStateFlow(0)

    fun files(context: Context) = OrganizerWidgetFiles(File(context.filesDir, "widgets"))

    suspend fun updateAll(context: Context) {
        OrganizerCalendarWidget().updateAll(context)
        OrganizerRemindersWidget().updateAll(context)
        OrganizerNotesWidget().updateAll(context)
    }

    // Opens the Organizer on one pane: "calendar", "reminders" or "notes". The data Uri keeps
    // each pane's PendingIntent distinct - extras alone don't.
    fun openApp(context: Context, pane: String): Action = actionStartActivity(
        Intent(context, MainActivity::class.java).apply {
            data = Uri.parse("sentinelcore://organizer/$pane")
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            putExtra(EXTRA_PANE, pane)
        },
    )
}

class CompleteReminderAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val id = parameters[ReminderIdKey] ?: return
        OrganizerWidgets.files(context).complete(id)
        OrganizerWidgets.actionsPosted.update { it + 1 }
        OrganizerWidgets.updateAll(context)
    }

    companion object {
        val ReminderIdKey = ActionParameters.Key<String>("reminderId")
    }
}

class OrganizerCalendarWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = OrganizerCalendarWidget()
}

class OrganizerRemindersWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = OrganizerRemindersWidget()
}

class OrganizerNotesWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = OrganizerNotesWidget()
}

// ---- shared look ---------------------------------------------------------------------------

private val SMALL = DpSize(110.dp, 110.dp)
private val WIDE = DpSize(250.dp, 110.dp)
private val TALL = DpSize(250.dp, 250.dp)

// The app's brand colours (ui/theme), light and dark.
private object WidgetColors {
    val green = ColorProvider(day = Color(0xFF0E8C4E), night = Color(0xFF38FF9C))
    val cyan = ColorProvider(day = Color(0xFF00707F), night = Color(0xFF49E8FF))
    val amber = ColorProvider(day = Color(0xFF7A5200), night = Color(0xFFFFB545))
    val red = ColorProvider(day = Color(0xFFA3111F), night = Color(0xFFFF4D5E))

    // The app's space palette (spaceColor in ui/organizer), in the same order.
    private val palette = listOf(Color(0xFF0E8C4E), Color(0xFF00707F), Color(0xFF7A5200), Color(0xFFB388FF), Color(0xFFFF7EB6), Color(0xFF82AAFF))

    fun event(event: OrganizerWidgetSnapshot.Event): ColorProvider {
        val hex = event.color?.removePrefix("#")?.takeIf { it.length == 6 }?.toLongOrNull(16)
        return ColorProvider(if (hex != null) Color(0xFF000000 or hex) else palette[event.paletteIndex % palette.size])
    }
}

private object WidgetText {
    private val time = DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)
    private val weekday = DateTimeFormatter.ofPattern("EEE", Locale.getDefault())
    private val monthDay = DateTimeFormatter.ofPattern("MMM d", Locale.getDefault())

    fun date(millis: Long): LocalDate = Instant.ofEpochMilli(millis).atZone(ZoneId.systemDefault()).toLocalDate()

    // "Today", "Tomorrow", "Yesterday", "Fri", or "Oct 3" further out.
    fun day(millis: Long, now: Long): String {
        val date = date(millis)
        val today = date(now)
        val days = ChronoUnit.DAYS.between(today, date)
        return when {
            days == 0L -> "Today"
            days == 1L -> "Tomorrow"
            days == -1L -> "Yesterday"
            days in 2..6 -> date.format(weekday)
            else -> date.format(monthDay)
        }
    }

    fun time(millis: Long): String = Instant.ofEpochMilli(millis).atZone(ZoneId.systemDefault()).format(time)

    fun event(event: OrganizerWidgetSnapshot.Event, now: Long, withDay: Boolean): String {
        val day = if (withDay) day(maxOf(event.start, now), now) else ""
        return when {
            event.allDay -> if (withDay) day else "All day"
            event.start <= now -> "Now – ${time(event.end)}"
            withDay -> "$day ${time(event.start)}"
            else -> time(event.start)
        }
    }

    fun due(reminder: OrganizerWidgetSnapshot.Reminder, now: Long): String? {
        val due = reminder.due ?: return null
        return if (reminder.allDay) day(due, now) else "${day(due, now)} ${time(due)}"
    }
}

@Composable
private fun WidgetFrame(pane: String, content: @Composable () -> Unit) {
    val context = LocalContext.current
    GlanceTheme {
        Column(
            GlanceModifier.fillMaxSize().appWidgetBackground().cornerRadius(16.dp)
                .background(GlanceTheme.colors.widgetBackground).padding(12.dp)
                .clickable(OrganizerWidgets.openApp(context, pane)),
        ) { content() }
    }
}

@Composable
private fun Header(title: String, color: ColorProvider, trailing: String? = null) {
    Row(GlanceModifier.fillMaxWidth().padding(bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(title, style = TextStyle(color = color, fontSize = 13.sp, fontWeight = FontWeight.Bold), maxLines = 1,
            modifier = GlanceModifier.defaultWeight())
        if (trailing != null) Text(trailing, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
    }
}

@Composable
private fun Message(text: String) {
    Box(GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
    }
}

@Composable
private fun NotSetUp() = Message("Open Sentinel Core to set up the Organizer")

private fun snapshot(context: Context) = OrganizerWidgets.files(context).readSnapshot()

// ---- Calendar -------------------------------------------------------------------------------

class OrganizerCalendarWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Responsive(setOf(SMALL, WIDE, TALL))

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val snapshot = snapshot(context)
        provideContent { WidgetFrame("calendar") { CalendarContent(snapshot, System.currentTimeMillis()) } }
    }
}

@Composable
private fun CalendarContent(snapshot: OrganizerWidgetSnapshot, now: Long) {
    val size = LocalSize.current
    val today = WidgetText.date(now)
    Row(GlanceModifier.fillMaxWidth().padding(bottom = 6.dp), verticalAlignment = Alignment.Bottom) {
        Text(today.format(DateTimeFormatter.ofPattern("EEEE", Locale.getDefault())).uppercase(),
            style = TextStyle(color = WidgetColors.red, fontSize = 11.sp, fontWeight = FontWeight.Bold), maxLines = 1)
        Spacer(GlanceModifier.width(6.dp))
        Text("${today.dayOfMonth}", style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 20.sp, fontWeight = FontWeight.Bold))
        if (size.width >= WIDE.width) {
            Spacer(GlanceModifier.width(6.dp))
            Text(today.format(DateTimeFormatter.ofPattern("MMMM", Locale.getDefault())),
                style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 13.sp), maxLines = 1)
        }
    }
    if (!snapshot.enabled) return NotSetUp()
    val events = snapshot.upcomingEvents(now)
    if (events.isEmpty()) return Message("Nothing in the next two weeks")

    if (size.height < TALL.height) {
        // Compact: the next few, each with its day when it isn't today.
        val count = if (size.width >= WIDE.width) 3 else 2
        Column(GlanceModifier.fillMaxWidth()) {
            events.take(count).forEach { EventRow(it, now, withDay = WidgetText.date(maxOf(it.start, now)) != WidgetText.date(now)) }
        }
    } else {
        // An agenda with a heading per day; scrolls through the whole two weeks.
        val rows = buildList {
            var lastDay: LocalDate? = null
            for (event in events) {
                val day = WidgetText.date(maxOf(event.start, now))
                if (day != lastDay) {
                    add(AgendaRow.Day(maxOf(event.start, now)))
                    lastDay = day
                }
                add(AgendaRow.Item(event))
            }
        }
        LazyColumn(GlanceModifier.fillMaxSize()) {
            items(rows) { row ->
                when (row) {
                    is AgendaRow.Day -> Text(WidgetText.day(row.at, now).uppercase(),
                        style = TextStyle(color = if (WidgetText.date(row.at) == WidgetText.date(now)) WidgetColors.red else GlanceTheme.colors.onSurfaceVariant,
                            fontSize = 11.sp, fontWeight = FontWeight.Bold),
                        modifier = GlanceModifier.padding(top = 4.dp, bottom = 2.dp))
                    is AgendaRow.Item -> EventRow(row.event, now, withDay = false)
                }
            }
        }
    }
}

private sealed interface AgendaRow {
    data class Day(val at: Long) : AgendaRow
    data class Item(val event: OrganizerWidgetSnapshot.Event) : AgendaRow
}

@Composable
private fun EventRow(event: OrganizerWidgetSnapshot.Event, now: Long, withDay: Boolean) {
    Row(GlanceModifier.fillMaxWidth().padding(vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(GlanceModifier.width(3.dp).height(30.dp).cornerRadius(2.dp).background(WidgetColors.event(event))) {}
        Spacer(GlanceModifier.width(7.dp))
        Column(GlanceModifier.defaultWeight()) {
            Text(event.title, style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 13.sp, fontWeight = FontWeight.Medium), maxLines = 1)
            Text(WidgetText.event(event, now, withDay), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 11.sp), maxLines = 1)
        }
    }
}

// ---- Reminders ------------------------------------------------------------------------------

class OrganizerRemindersWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Responsive(setOf(SMALL, WIDE, TALL))

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val snapshot = snapshot(context)
        provideContent { WidgetFrame("reminders") { RemindersContent(snapshot, System.currentTimeMillis()) } }
    }
}

@Composable
private fun RemindersContent(snapshot: OrganizerWidgetSnapshot, now: Long) {
    val reminders = snapshot.reminders
    val due = reminders.count { it.isOverdue(now) || it.isDueToday(now) }
    Header("Reminders", WidgetColors.amber, trailing = when {
        !snapshot.enabled || reminders.isEmpty() -> null
        due > 0 -> "$due due"
        else -> "${reminders.size}"
    })
    if (!snapshot.enabled) return NotSetUp()
    if (reminders.isEmpty()) return Message("All done")
    val compact = LocalSize.current.width < WIDE.width
    LazyColumn(GlanceModifier.fillMaxSize()) {
        items(reminders, itemId = { it.id.hashCode().toLong() }) { reminder -> ReminderRow(reminder, now, compact) }
    }
}

@Composable
private fun ReminderRow(reminder: OrganizerWidgetSnapshot.Reminder, now: Long, compact: Boolean) {
    Row(GlanceModifier.fillMaxWidth().padding(vertical = 1.dp), verticalAlignment = Alignment.CenterVertically) {
        CheckBox(
            checked = false,
            onCheckedChange = actionRunCallback<CompleteReminderAction>(actionParametersOf(CompleteReminderAction.ReminderIdKey to reminder.id)),
        )
        Column(GlanceModifier.defaultWeight()) {
            val marks = buildString {
                if (reminder.priority > 0) append("!".repeat(reminder.priority.coerceAtMost(3))).append(' ')
            }
            Text(marks + reminder.title + if (reminder.flagged) " ⚑" else "",
                style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 13.sp), maxLines = if (compact) 2 else 1)
            val dueText = WidgetText.due(reminder, now)
            if (!compact && dueText != null) {
                Text(dueText + if (reminder.recurring) "  ↻" else "",
                    style = TextStyle(color = if (reminder.isOverdue(now)) WidgetColors.red else GlanceTheme.colors.onSurfaceVariant, fontSize = 11.sp),
                    maxLines = 1)
            }
        }
    }
}

// ---- Notes ----------------------------------------------------------------------------------

class OrganizerNotesWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Responsive(setOf(SMALL, WIDE, TALL))

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val snapshot = snapshot(context)
        provideContent { WidgetFrame("notes") { NotesContent(snapshot, System.currentTimeMillis()) } }
    }
}

@Composable
private fun NotesContent(snapshot: OrganizerWidgetSnapshot, now: Long) {
    Header("Notes", WidgetColors.green)
    if (!snapshot.enabled) return NotSetUp()
    val notes = snapshot.notes
    if (notes.isEmpty()) return Message("No notes yet")
    val size = LocalSize.current
    if (size.width < WIDE.width) {
        // One note, as much of it as fits.
        val note = notes.first()
        Text((if (note.pinned) "📌 " else "") + noteTitle(note),
            style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 14.sp, fontWeight = FontWeight.Bold), maxLines = 2)
        Text(note.preview, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp), maxLines = 5)
        return
    }
    val previewLines = if (size.height >= TALL.height) 2 else 1
    LazyColumn(GlanceModifier.fillMaxSize()) {
        items(notes, itemId = { it.id.hashCode().toLong() }) { note ->
            Column(GlanceModifier.fillMaxWidth().padding(vertical = 4.dp)) {
                Row(GlanceModifier.fillMaxWidth()) {
                    Text((if (note.pinned) "📌 " else "") + noteTitle(note),
                        style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 13.sp, fontWeight = FontWeight.Medium),
                        maxLines = 1, modifier = GlanceModifier.defaultWeight())
                    note.updatedAt?.let {
                        Text(WidgetText.day(it, now), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 11.sp))
                    }
                }
                if (note.preview.isNotEmpty()) {
                    Text(note.preview.replace("\n", " · "),
                        style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp), maxLines = previewLines)
                }
            }
        }
    }
}

private fun noteTitle(note: OrganizerWidgetSnapshot.Note) =
    note.title.ifEmpty { note.preview.lineSequence().firstOrNull()?.takeIf { it.isNotEmpty() } ?: "Untitled" }
