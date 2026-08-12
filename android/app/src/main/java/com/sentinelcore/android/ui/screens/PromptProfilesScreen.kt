package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

// The AI prompt profiles - the web dashboard's PROMPT PROFILES section. Each profile is a system
// prompt plus the questions the model is asked about an image; cameras and gate checks pick one by
// its key.
//
// Only the fields worth editing on a phone are shown. Everything else in a profile - most
// importantly its crop region, a rectangle no phone form should be drawing - is kept exactly as
// the server sent it and written back untouched, because the save endpoint replaces the whole
// dictionary. A screen that modelled only what it displayed would erase the rest by saving.
@Composable
fun PromptProfilesScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()

    // The originals, kept verbatim so unedited fields survive the round trip.
    var raw by remember { mutableStateOf(JSONObject()) }
    var keys by remember { mutableStateOf<List<String>>(emptyList()) }
    var loaded by remember { mutableStateOf(false) }
    var selectedKey by remember { mutableStateOf<String?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        runCatching { model.promptProfiles() }
            .onSuccess { profiles ->
                raw = profiles
                // Sorted so the list doesn't reshuffle between visits - a JSON object has no order
                // of its own, and a settings list that reorders itself is disorienting.
                keys = profiles.keys().asSequence().toList().sorted()
                loaded = true
            }
            .onFailure { errorMessage = it.message ?: "Could not load prompt profiles." }
    }

    val openKey = selectedKey
    if (openKey != null) {
        PromptProfileEditor(
            state = state,
            profileKey = openKey,
            profile = raw.optJSONObject(openKey) ?: JSONObject(),
            onBack = { selectedKey = null },
            onSave = { edited ->
                // Merge over the original, so anything this screen never showed is preserved.
                val merged = raw.optJSONObject(openKey) ?: JSONObject()
                for (name in edited.keys()) merged.put(name, edited.get(name))
                raw.put(openKey, merged)
                model.savePromptProfiles(raw)
            },
        )
        return
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Prompt Profiles", state)
        LazyColumn(
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

            if (loaded && keys.isEmpty()) {
                item {
                    Text(
                        "No prompt profiles configured.",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(14.dp),
                    )
                }
            }

            items(keys, key = { it }) { key ->
                val profile = raw.optJSONObject(key) ?: JSONObject()
                val name = profile.optString("displayName").ifEmpty { key }
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 14.dp)
                        .clickable { selectedKey = key },
                ) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(name, style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                        Text(
                            key,
                            style = MaterialTheme.typography.bodySmall,
                            fontFamily = FontFamily.Monospace,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
        }
    }
}

@Composable
private fun PromptProfileEditor(
    state: SentinelUiState,
    profileKey: String,
    profile: JSONObject,
    onBack: () -> Unit,
    onSave: suspend (JSONObject) -> Unit,
) {
    val scope = rememberCoroutineScope()

    var displayName by remember(profileKey) { mutableStateOf(profile.optString("displayName")) }
    var systemPrompt by remember(profileKey) { mutableStateOf(profile.optString("systemPrompt")) }
    val questions = remember(profileKey) {
        mutableStateListOf<String>().apply {
            val array = profile.optJSONArray("questions") ?: JSONArray()
            for (index in 0 until array.length()) add(array.optString(index))
        }
    }
    var newQuestion by remember(profileKey) { mutableStateOf("") }
    var licensePlate by remember(profileKey) {
        mutableStateOf(profile.optBoolean("licensePlateRecognitionEnabled"))
    }
    var faceRecognition by remember(profileKey) {
        mutableStateOf(profile.optBoolean("faceRecognitionEnabled"))
    }
    var isSaving by remember { mutableStateOf(false) }
    var statusMessage by remember { mutableStateOf<String?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader(displayName.ifEmpty { profileKey }, state)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            item {
                TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                    Text("Back to Profiles", modifier = Modifier.weight(1f).padding(start = 8.dp))
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedTextField(
                            value = displayName,
                            onValueChange = { displayName = it },
                            label = { Text("Display name") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Text(
                            "Only what's shown in the UI. Cameras and gate checks point at the key " +
                                "\"$profileKey\", which never changes — so renaming here breaks nothing.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("System Prompt", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                        OutlinedTextField(
                            value = systemPrompt,
                            onValueChange = { systemPrompt = it },
                            minLines = 4,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Text(
                            "How the model is told to behave before it's shown the image.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("Questions", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                        questions.forEachIndexed { index, question ->
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(question, Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                                IconButton(onClick = { questions.removeAt(index) }) {
                                    Icon(Icons.Filled.Delete, contentDescription = "Remove", tint = SentinelRed)
                                }
                            }
                        }
                        OutlinedTextField(
                            value = newQuestion,
                            onValueChange = { newQuestion = it },
                            label = { Text("Add a question") },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedButton(
                            onClick = {
                                questions.add(newQuestion.trim())
                                newQuestion = ""
                            },
                            enabled = newQuestion.isNotBlank(),
                            modifier = Modifier.fillMaxWidth(),
                        ) { Text("Add") }
                        Text(
                            "What the model is asked about each image. At least one is required.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("License Plate Recognition", Modifier.weight(1f))
                            Switch(checked = licensePlate, onCheckedChange = { licensePlate = it })
                        }
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("Face Recognition", Modifier.weight(1f))
                            Switch(checked = faceRecognition, onCheckedChange = { faceRecognition = it })
                        }
                    }
                }
            }

            item {
                Button(
                    onClick = {
                        scope.launch {
                            isSaving = true
                            statusMessage = null
                            errorMessage = null
                            try {
                                // Only the five fields this screen shows; the caller merges them
                                // over the original so everything omitted survives untouched.
                                val edited = JSONObject()
                                    .put("displayName", displayName)
                                    .put("systemPrompt", systemPrompt)
                                    .put("questions", JSONArray().apply { questions.forEach { put(it) } })
                                    .put("licensePlateRecognitionEnabled", licensePlate)
                                    .put("faceRecognitionEnabled", faceRecognition)
                                onSave(edited)
                                statusMessage = "Saved."
                            } catch (error: Exception) {
                                errorMessage = error.message ?: "Save failed."
                            } finally {
                                isSaving = false
                            }
                        }
                    },
                    enabled = !isSaving && questions.isNotEmpty() && systemPrompt.isNotBlank(),
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                ) { Text(if (isSaving) "Saving…" else "Save") }
            }

            item {
                Text(
                    "This profile's crop region and any other settings not shown here are kept exactly as " +
                        "they are.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                )
            }

            statusMessage?.let { message ->
                item { Text(message, color = SentinelGreen, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
        }
    }
}
