package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.HubDeviceInput
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.launch

private val DEVICE_KINDS = listOf("light", "switch", "outlet", "fan", "cover", "lock", "sensor")

// Add or edit one Device Hub device. The advanced payload fields are behind a toggle because
// discovery fills them in correctly for the overwhelming majority of devices - someone typing a
// device in by hand usually only needs a name and two topics.
@Composable
fun DeviceEditScreen(
    model: SentinelViewModel,
    deviceId: String?,
    prefill: HubDeviceInput? = null,
    onBack: () -> Unit,
    onSaved: () -> Unit,
) {
    val existing = deviceId?.let { model.device(it) }
    var input by remember {
        mutableStateOf(prefill ?: existing?.let { HubDeviceInput.from(it) } ?: HubDeviceInput())
    }
    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var showAdvanced by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    Column(Modifier.fillMaxSize()) {
        DetailHeader(if (deviceId == null) "Add Device" else "Edit Device", onBack)

        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            errorMessage?.let { Text(it, color = SentinelRed) }

            OutlinedTextField(
                value = input.name,
                onValueChange = { input = input.copy(name = it) },
                label = { Text("Name") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = input.room,
                onValueChange = { input = input.copy(room = it) },
                label = { Text("Room (optional)") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )

            Text("Kind", style = MaterialTheme.typography.labelLarge)
            Row(
                Modifier.fillMaxWidth().verticalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                DEVICE_KINDS.forEach { kind ->
                    FilterChip(
                        selected = input.kind == kind,
                        onClick = { input = input.copy(kind = kind) },
                        label = { Text(kind) },
                    )
                }
            }

            SwitchRow("Enabled", input.enabled) { input = input.copy(enabled = it) }

            TopicField("Command Topic", input.commandTopic) { input = input.copy(commandTopic = it) }
            TopicField("State Topic", input.stateTopic) { input = input.copy(stateTopic = it) }
            Text(
                "The topic this device takes commands on, e.g. cmnd/kitchen/POWER or " +
                    "zigbee2mqtt/kitchen/set, and the one it reports its state on. No + or # wildcards.",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )

            SwitchRow("Supports brightness", input.supportsBrightness) {
                input = input.copy(supportsBrightness = it)
            }
            if (input.supportsBrightness) {
                TopicField("Brightness Command Topic", input.brightnessCommandTopic) {
                    input = input.copy(brightnessCommandTopic = it)
                }
                TopicField("Brightness State Topic", input.brightnessStateTopic) {
                    input = input.copy(brightnessStateTopic = it)
                }
                OutlinedTextField(
                    value = input.brightnessScale.toString(),
                    onValueChange = { value ->
                        input = input.copy(brightnessScale = value.toIntOrNull() ?: input.brightnessScale)
                    },
                    label = { Text("Brightness Scale") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )
                Text(
                    "The device's own value for full brightness - 255 for most Zigbee bulbs, 254 for " +
                        "Zigbee2MQTT, 100 for Tasmota. The slider is always 0-100 and is converted for you.",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                )
            }

            TextButton(onClick = { showAdvanced = !showAdvanced }) {
                Text(if (showAdvanced) "Hide advanced" else "Show advanced")
            }

            if (showAdvanced) {
                Text("Payload Schema", style = MaterialTheme.typography.labelLarge)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("raw" to "Raw (ON / OFF)", "json" to "JSON").forEach { (value, label) ->
                        FilterChip(
                            selected = input.schema == value,
                            onClick = { input = input.copy(schema = value) },
                            label = { Text(label) },
                        )
                    }
                }
                if (input.schema == "json") {
                    TopicField("State Value Key", input.stateValueTemplate) {
                        input = input.copy(stateValueTemplate = it)
                    }
                }
                TopicField("Availability Topic", input.availabilityTopic) {
                    input = input.copy(availabilityTopic = it)
                }
                TopicField("Payload for ON", input.payloadOn) { input = input.copy(payloadOn = it) }
                TopicField("Payload for OFF", input.payloadOff) { input = input.copy(payloadOff = it) }
                TopicField("Payload for online", input.payloadAvailable) {
                    input = input.copy(payloadAvailable = it)
                }
                TopicField("Payload for offline", input.payloadNotAvailable) {
                    input = input.copy(payloadNotAvailable = it)
                }
                SwitchRow("Optimistic", input.optimistic) { input = input.copy(optimistic = it) }
                Text(
                    "Optimistic assumes a command worked instead of waiting for the device to confirm. " +
                        "Needed for devices that never report their state.",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                )
            }

            Button(
                onClick = {
                    scope.launch {
                        isSaving = true
                        errorMessage = null
                        try {
                            if (deviceId == null) model.createDevice(input) else model.updateDevice(deviceId, input)
                            onSaved()
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Could not save this device."
                        } finally {
                            isSaving = false
                        }
                    }
                },
                enabled = !isSaving && input.name.isNotBlank(),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(if (isSaving) "Saving…" else "Save")
            }
        }
    }
}

// Topics and payloads are case-sensitive and never sentences - autocapitalisation and autocorrect
// actively corrupt them.
@Composable
private fun TopicField(label: String, value: String, onValueChange: (String) -> Unit) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        singleLine = true,
        keyboardOptions = KeyboardOptions(
            capitalization = KeyboardCapitalization.None,
            autoCorrectEnabled = false,
            keyboardType = KeyboardType.Uri,
        ),
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
private fun SwitchRow(label: String, checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(label)
        Switch(checked = checked, onCheckedChange = onCheckedChange)
    }
}
