package com.sentinelcore.android.ui

import android.content.Context
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AddCircle
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.RemoveCircle
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed

// The person's chosen tabs, as TabBarLayout.encode writes them. Held in Compose state as well as
// SharedPreferences so the bar redraws the moment the editor changes it. AppShell makes the one
// instance and hands it to everything that reads or edits the list.
internal class TabBarPreferences(context: Context) {
    private val preferences = context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE)

    var stored: String by mutableStateOf(preferences.getString(KEY, "").orEmpty())
        private set

    fun layout(visible: List<AppSection>) = TabBarLayout(TabBarLayout.decode(stored), visible)

    fun save(sections: List<AppSection>, visible: List<AppSection>) {
        update(TabBarLayout.encode(sections.take(TabBarLayout.limit(visible))))
    }

    fun reset() = update("")

    private fun update(value: String) {
        stored = value
        preferences.edit().putString(KEY, value).apply()
    }

    private companion object {
        const val KEY = "tab_bar_sections"
    }
}

// Everything without a tab of its own, plus the way into the editor. A sheet rather than a
// screen of its own: More isn't a section, so it has no route for Back to return to.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun MoreSectionsSheet(
    sections: List<AppSection>,
    onSelect: (AppSection) -> Unit,
    onEdit: () -> Unit,
    onDismiss: () -> Unit,
    // The weather-warning dot (see weather/WeatherWarningBanner), per section.
    alertLevel: (AppSection) -> String? = { null },
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.navigationBarsPadding().padding(bottom = 12.dp)) {
            sections.forEach { section ->
                ListItem(
                    headlineContent = { Text(section.title) },
                    leadingContent = { BadgedIcon(alertLevel(section)) { Icon(section.icon, contentDescription = null) } },
                    colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                    modifier = Modifier.clickable { onSelect(section) },
                )
            }
            HorizontalDivider(Modifier.padding(vertical = 4.dp))
            ListItem(
                headlineContent = { Text("Edit tab bar", color = SentinelGreen) },
                supportingContent = {
                    Text("Choose which sections have a tab at the bottom of the screen. The rest stay here.")
                },
                leadingContent = { Icon(Icons.Default.GridView, contentDescription = null, tint = SentinelGreen) },
                colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                modifier = Modifier.clickable(onClick = onEdit),
            )
        }
    }
}

// Two lists: the tabs, in order, and everything else. Removing a tab moves it to More; adding one
// takes it out of More until the bar is full.
@Composable
internal fun TabBarEditorScreen(
    preferences: TabBarPreferences,
    visible: List<AppSection>,
    onBack: () -> Unit,
) {
    // What the person has pinned, which is the whole bar when nothing is left over for More.
    val tabs = preferences.layout(visible).bar
    val others = visible.filter { it !in tabs }
    val limit = TabBarLayout.limit(visible)
    val save = { sections: List<AppSection> -> preferences.save(sections, visible) }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Tab bar", onBack)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                EditorSection(
                    title = "Tab bar",
                    footer = if (visible.size > TabBarLayout.MAX_TABS) {
                        "Up to $limit sections, plus More for the rest."
                    } else {
                        "Up to $limit sections. Anything you remove moves to More."
                    },
                ) {
                    tabs.forEachIndexed { index, section ->
                        EditorRow(section) {
                            IconButton(
                                onClick = { save(tabs.toMutableList().apply { add(index - 1, removeAt(index)) }) },
                                enabled = index > 0,
                            ) {
                                Icon(Icons.Default.ArrowUpward, contentDescription = "Move ${section.title} up")
                            }
                            IconButton(
                                onClick = { save(tabs.toMutableList().apply { add(index + 1, removeAt(index)) }) },
                                enabled = index < tabs.lastIndex,
                            ) {
                                Icon(Icons.Default.ArrowDownward, contentDescription = "Move ${section.title} down")
                            }
                            // The bar always keeps one section - an empty list would read as
                            // "never customized" and quietly bring the defaults back.
                            IconButton(onClick = { save(tabs - section) }, enabled = tabs.size > 1) {
                                Icon(
                                    Icons.Default.RemoveCircle,
                                    contentDescription = "Remove ${section.title} from the tab bar",
                                    tint = if (tabs.size > 1) SentinelRed else MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
            }

            if (others.isNotEmpty()) {
                item {
                    EditorSection(title = "In More") {
                        others.forEach { section ->
                            val canAdd = tabs.size < limit
                            EditorRow(section) {
                                IconButton(onClick = { save(tabs + section) }, enabled = canAdd) {
                                    Icon(
                                        Icons.Default.AddCircle,
                                        contentDescription = "Add ${section.title} to the tab bar",
                                        tint = if (canAdd) SentinelGreen else MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                            }
                        }
                    }
                }
            }

            item {
                OutlinedButton(
                    onClick = preferences::reset,
                    enabled = preferences.stored.isNotBlank(),
                    modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth().widthIn(max = 900.dp),
                ) {
                    Text("Reset to default")
                }
            }
        }
    }
}

// Drawn like SettingsScreen's sections, since this is reached from there.
@Composable
private fun EditorSection(
    title: String,
    footer: String? = null,
    content: @Composable () -> Unit,
) {
    Column(
        modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth().widthIn(max = 900.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(title, color = SentinelCyan)
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(vertical = 6.dp)) { content() }
        }
        if (footer != null) {
            Text(
                footer,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun EditorRow(
    section: AppSection,
    actions: @Composable () -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(start = 14.dp, end = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Icon(section.icon, contentDescription = null)
        Text(section.title, modifier = Modifier.weight(1f))
        actions()
    }
}
