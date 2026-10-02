package com.sentinelcore.android.widgets

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalContext
import androidx.glance.LocalSize
import androidx.glance.action.Action
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
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
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.sentinelcore.android.R
import com.sentinelcore.android.ui.organizer.QuickCaptureActivity

// Quick Add on the home screen: a microphone and a "type it" bar, like the search bar. Either one
// opens QuickCaptureActivity over the home screen - a widget can't listen or take typing itself -
// to say or type "add a reminder to take out the trash at 7am tomorrow" and have it land in the
// Organizer as a reminder, note or event. It reads nothing from the app, so it never goes stale.
class QuickAddWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Responsive(setOf(BAR, CARD))

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        provideContent { GlanceTheme { QuickAddContent() } }
    }

    companion object {
        private val BAR = DpSize(250.dp, 50.dp)
        private val CARD = DpSize(250.dp, 110.dp)

        // A distinct address per button, so Android keeps each one's PendingIntent apart.
        fun open(context: Context, mode: String, kind: String? = null): Action = actionStartActivity(
            Intent(context, QuickCaptureActivity::class.java).apply {
                data = Uri.parse("sentinelcore://capture?mode=$mode" + (kind?.let { "&kind=$it" } ?: ""))
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
            },
        )
    }
}

class QuickAddWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = QuickAddWidget()
}

private object QuickAddColors {
    val green = ColorProvider(day = Color(0xFF0E8C4E), night = Color(0xFF38FF9C))
    val chip = ColorProvider(day = Color(0x220E8C4E), night = Color(0x3338FF9C))
}

@Composable
private fun QuickAddContent() {
    val context = LocalContext.current
    val roomy = LocalSize.current.height >= 100.dp
    Column(
        GlanceModifier.fillMaxSize().cornerRadius(24.dp)
            .background(GlanceTheme.colors.widgetBackground).padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Image(
                ImageProvider(R.drawable.ic_quick_add_mic),
                contentDescription = "Say it",
                modifier = GlanceModifier.size(if (roomy) 48.dp else 36.dp).clickable(QuickAddWidget.open(context, "voice")),
            )
            Spacer(GlanceModifier.width(8.dp))
            Row(
                GlanceModifier.defaultWeight().height(if (roomy) 44.dp else 34.dp).cornerRadius(17.dp)
                    .background(GlanceTheme.colors.secondaryContainer).padding(horizontal = 12.dp)
                    .clickable(QuickAddWidget.open(context, "type")),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    "Reminder, note or event…",
                    style = TextStyle(color = GlanceTheme.colors.onSecondaryContainer, fontSize = 13.sp),
                    maxLines = 1,
                    modifier = GlanceModifier.defaultWeight(),
                )
                Image(
                    ImageProvider(R.drawable.ic_quick_add_keyboard),
                    contentDescription = "Type it",
                    colorFilter = ColorFilter.tint(GlanceTheme.colors.onSecondaryContainer),
                    modifier = GlanceModifier.size(18.dp),
                )
            }
        }
        if (roomy) {
            Spacer(GlanceModifier.height(8.dp))
            // Straight to that kind, so whatever is said is taken as one.
            Row(GlanceModifier.fillMaxWidth()) {
                KindChip("Reminder", QuickAddWidget.open(context, "voice", "reminder"))
                Spacer(GlanceModifier.width(6.dp))
                KindChip("Note", QuickAddWidget.open(context, "voice", "note"))
                Spacer(GlanceModifier.width(6.dp))
                KindChip("Event", QuickAddWidget.open(context, "voice", "event"))
            }
        }
    }
}

@Composable
private fun KindChip(label: String, action: Action) {
    Box(
        GlanceModifier.cornerRadius(14.dp).background(QuickAddColors.chip)
            .padding(horizontal = 12.dp, vertical = 6.dp).clickable(action),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, style = TextStyle(color = QuickAddColors.green, fontSize = 12.sp, fontWeight = FontWeight.Medium))
    }
}
