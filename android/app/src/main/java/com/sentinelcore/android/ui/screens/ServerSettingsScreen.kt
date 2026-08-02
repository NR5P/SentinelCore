package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MenuAnchorType
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.SettingsField
import com.sentinelcore.android.data.SettingsSection
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

// Every server settings section, rendered generically from GET /api/settings/schema - the same
// definitions the web dashboard and the iOS/macOS app render, so a section added on the server
// appears here with no app update. A renderer, not a settings screen: it knows how to draw a
// "number" or "select" field, and nothing about what any particular section means.
//
// Reached only through the Admin-gated part of SettingsScreen - the whole /api/settings group
// is AdminOnly server-side and would 403 a Viewer anyway.
@Composable
fun ServerSettingsScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    var sections by remember { mutableStateOf<List<SettingsSection>>(emptyList()) }
    var snapshot by remember { mutableStateOf<JSONObject?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var selected by remember { mutableStateOf<SettingsSection?>(null) }

    LaunchedEffect(Unit) {
        try {
            sections = model.settingsSchema()
            snapshot = model.settingsSnapshot()
        } catch (error: Exception) {
            loadError = error.message ?: "Could not load server settings."
        }
    }

    val openSection = selected
    if (openSection != null) {
        ServerSettingsSectionEditor(
            section = openSection,
            initialData = snapshot?.optJSONObject(openSection.key),
            model = model,
            onBack = { selected = null },
        )
        return
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Server Settings", state)
        when {
            loadError != null -> Column(
                Modifier.fillMaxWidth().padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(loadError.orEmpty(), color = MaterialTheme.colorScheme.onSurfaceVariant)
                TextButton(onClick = onBack) { Text("Back") }
            }

            sections.isEmpty() -> Text(
                "Loading…",
                modifier = Modifier.padding(24.dp),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            else -> LazyColumn(
                modifier = Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.spacedBy(10.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                item {
                    TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                        Text("Back to Settings", modifier = Modifier.weight(1f).padding(start = 8.dp))
                    }
                }
                items(sections, key = { it.key }) { section ->
                    Card(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 14.dp)
                            .clickable { selected = section },
                    ) {
                        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(section.title, style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                            Text(
                                section.description,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 2,
                            )
                        }
                    }
                }
                item { Spacer(Modifier.padding(8.dp)) }
            }
        }
    }
}

@Composable
private fun ServerSettingsSectionEditor(
    section: SettingsSection,
    initialData: JSONObject?,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()

    // Field values keyed by schema field name (dotted paths like "apple.teamId" stay flat here;
    // nesting is reassembled when building the save payload).
    val text = remember(section.key) {
        mutableStateMapOf<String, String>().apply {
            for (field in section.fields) {
                if (field.type == "checkbox") continue
                val value = readPath(initialData, field.name)
                this[field.name] = when {
                    field.type == "select" ->
                        (value as? String) ?: section.fields.first { it.name == field.name }
                            .options.firstOrNull()?.value.orEmpty()
                    value is JSONArray -> buildList {
                        for (index in 0 until value.length()) add(value.optString(index))
                    }.joinToString(", ")
                    value == null || value == JSONObject.NULL -> ""
                    value is Double -> if (value == value.toLong().toDouble()) value.toLong().toString() else value.toString()
                    else -> value.toString()
                }
            }
        }
    }
    val flags = remember(section.key) {
        mutableStateMapOf<String, Boolean>().apply {
            for (field in section.fields) {
                if (field.type == "checkbox") this[field.name] = readPath(initialData, field.name) == true
            }
        }
    }

    var isSaving by remember { mutableStateOf(false) }
    var statusMessage by remember { mutableStateOf<String?>(null) }
    var errors by remember { mutableStateOf<List<String>>(emptyList()) }
    var confirmRestart by remember { mutableStateOf(false) }
    var confirmAction by remember { mutableStateOf(false) }

    fun save() {
        val payload = JSONObject()
        val problems = mutableListOf<String>()
        for (field in section.fields) {
            val value: Any = when (field.type) {
                "checkbox" -> flags[field.name] ?: false
                "number" -> {
                    val raw = (text[field.name] ?: "").trim()
                    val number = raw.toDoubleOrNull()
                    if (number == null) {
                        problems.add("${field.label} must be a number.")
                        continue
                    }
                    // org.json would serialize 30.0 as "30.0", which the server's int binding
                    // refuses - so integral values go as longs.
                    if (number == number.toLong().toDouble()) number.toLong() else number
                }
                "csv" -> JSONArray((text[field.name] ?: "").split(",").map { it.trim() }.filter { it.isNotEmpty() })
                else -> text[field.name] ?: ""
            }
            writePath(payload, field.name, value)
        }
        if (problems.isNotEmpty()) {
            errors = problems
            return
        }

        scope.launch {
            isSaving = true
            statusMessage = null
            errors = emptyList()
            try {
                val restartRequired = model.saveSettingsSection(section.endpoint, payload)
                statusMessage = if (restartRequired) {
                    "Saved. Takes effect after the bridge service restarts - use the restart button below."
                } else {
                    "Saved."
                }
            } catch (error: Exception) {
                errors = (error.message ?: "Save failed.").split("\n")
            } finally {
                isSaving = false
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                    Text(section.title, modifier = Modifier.weight(1f).padding(start = 8.dp), color = SentinelGreen)
                }
            }
            item {
                Text(
                    section.description,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 16.dp),
                )
            }
            items(section.fields, key = { it.name }) { field ->
                Column(
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    SettingsFieldControl(
                        field = field,
                        textValue = text[field.name] ?: "",
                        onText = { text[field.name] = it },
                        flagValue = flags[field.name] ?: false,
                        onFlag = { flags[field.name] = it },
                    )
                    Text(
                        if (field.requiresRestart) "${field.tip} (Restart required.)" else field.tip,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            item {
                Column(
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    statusMessage?.let { Text(it, color = SentinelGreen, style = MaterialTheme.typography.bodySmall) }
                    errors.forEach { Text(it, color = SentinelRed, style = MaterialTheme.typography.bodySmall) }
                    Button(onClick = ::save, enabled = !isSaving, modifier = Modifier.fillMaxWidth()) {
                        Text(if (isSaving) "Saving…" else "Save")
                    }
                    section.extraAction?.let { action ->
                        OutlinedButton(onClick = { confirmAction = true }, modifier = Modifier.fillMaxWidth()) {
                            Text(action.label.replaceFirstChar(Char::uppercase))
                        }
                    }
                    if (section.needsRestart) {
                        OutlinedButton(onClick = { confirmRestart = true }, modifier = Modifier.fillMaxWidth()) {
                            Text("Restart Bridge Service", color = SentinelRed)
                        }
                    }
                }
            }
            item { Spacer(Modifier.padding(12.dp)) }
        }
    }

    if (confirmRestart) {
        AlertDialog(
            onDismissRequest = { confirmRestart = false },
            title = { Text("Restart bridge service?") },
            text = {
                Text(
                    "Saved restart-required settings take effect after the restart. Alert processing " +
                        "pauses for a few seconds; running as a service, it comes back on its own.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    confirmRestart = false
                    scope.launch {
                        statusMessage = "Restarting - the server should be back in a few seconds…"
                        errors = emptyList()
                        try {
                            model.restartServer("bridge")
                        } catch (error: Exception) {
                            statusMessage = null
                            // The refusal for an unsupervised process is worth showing verbatim -
                            // it explains the systemd situation better than a generic error.
                            errors = listOf(error.message ?: "Restart failed.")
                        }
                    }
                }) { Text("Restart", color = SentinelRed) }
            },
            dismissButton = { TextButton(onClick = { confirmRestart = false }) { Text("Cancel") } },
        )
    }

    val action = section.extraAction
    if (confirmAction && action != null) {
        AlertDialog(
            onDismissRequest = { confirmAction = false },
            title = { Text(action.label.replaceFirstChar(Char::uppercase)) },
            text = { Text(action.tip) },
            confirmButton = {
                TextButton(onClick = {
                    confirmAction = false
                    scope.launch {
                        try {
                            model.runSettingsAction(action)
                            statusMessage = "Done."
                        } catch (error: Exception) {
                            errors = listOf(error.message ?: "Action failed.")
                        }
                    }
                }) { Text("Continue") }
            },
            dismissButton = { TextButton(onClick = { confirmAction = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun SettingsFieldControl(
    field: SettingsField,
    textValue: String,
    onText: (String) -> Unit,
    flagValue: Boolean,
    onFlag: (Boolean) -> Unit,
) {
    when (field.type) {
        "checkbox" -> Row(
            Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(field.label, modifier = Modifier.weight(1f))
            Switch(checked = flagValue, onCheckedChange = onFlag)
        }

        "select" -> SettingsSelectField(field, textValue, onText)

        "password" -> OutlinedTextField(
            value = textValue,
            onValueChange = onText,
            modifier = Modifier.fillMaxWidth(),
            label = { Text(field.label) },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
        )

        "number" -> OutlinedTextField(
            value = textValue,
            onValueChange = onText,
            modifier = Modifier.fillMaxWidth(),
            label = { Text(field.label) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
        )

        "text", "csv" -> OutlinedTextField(
            value = textValue,
            onValueChange = onText,
            modifier = Modifier.fillMaxWidth(),
            label = { Text(field.label) },
            singleLine = true,
            placeholder = if (field.type == "csv") {
                { Text("comma, separated") }
            } else {
                null
            },
        )

        // A field type this app version doesn't know - shown read-only rather than guessed at.
        else -> Row(Modifier.fillMaxWidth()) {
            Text(field.label, modifier = Modifier.weight(1f))
            Text(textValue, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun SettingsSelectField(field: SettingsField, value: String, onValue: (String) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    val selectedLabel = field.options.firstOrNull { it.value == value }?.label ?: value

    ExposedDropdownMenuBox(expanded = expanded, onExpandedChange = { expanded = it }) {
        OutlinedTextField(
            value = selectedLabel,
            onValueChange = {},
            readOnly = true,
            label = { Text(field.label) },
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
            modifier = Modifier.fillMaxWidth().menuAnchor(MenuAnchorType.PrimaryNotEditable),
        )
        ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            for (option in field.options) {
                DropdownMenuItem(
                    text = { Text(option.label) },
                    onClick = {
                        onValue(option.value)
                        expanded = false
                    },
                )
            }
        }
    }
}

// Schema field names can be dotted paths into nested objects ("apple.teamId") - the same
// convention the dashboard's readPath/writePath and the iOS SettingsValue helpers use.
private fun readPath(data: JSONObject?, path: String): Any? {
    var node: Any? = data ?: return null
    for (segment in path.split(".")) {
        node = (node as? JSONObject)?.opt(segment) ?: return null
    }
    return node
}

private fun writePath(target: JSONObject, path: String, value: Any) {
    val segments = path.split(".")
    var node = target
    for (segment in segments.dropLast(1)) {
        node = node.optJSONObject(segment) ?: JSONObject().also { node.put(segment, it) }
    }
    node.put(segments.last(), value)
}
