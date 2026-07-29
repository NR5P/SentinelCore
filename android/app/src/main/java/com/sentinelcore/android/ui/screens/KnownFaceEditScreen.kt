package com.sentinelcore.android.ui.screens

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import com.sentinelcore.android.data.KnownFace
import com.sentinelcore.android.data.KnownFaceEnrollment
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.SentinelRemoteImage
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch
import java.io.File

@Composable
fun KnownFaceEditScreen(
    face: KnownFace?,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val isNew = face == null
    var label by remember { mutableStateOf(face?.label.orEmpty()) }
    var enrollments by remember { mutableStateOf(face?.enrollments ?: emptyList()) }
    // Held only for the "add a new person" flow - createKnownFace needs the label and photo
    // together in one request, so the photo can't be uploaded until Save is tapped.
    var pendingImageBytes by remember { mutableStateOf<ByteArray?>(null) }
    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var pendingCameraUri by remember { mutableStateOf<Uri?>(null) }
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    suspend fun handlePickedPhoto(bytes: ByteArray) {
        if (isNew) {
            pendingImageBytes = bytes
            return
        }
        val currentFace = face ?: return
        errorMessage = null
        isSaving = true
        try {
            val updated = model.addKnownFaceEnrollment(currentFace.id, bytes)
            enrollments = updated.enrollments
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not add that photo."
        } finally {
            isSaving = false
        }
    }

    val galleryLauncher = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            scope.launch {
                val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
                if (bytes != null) handlePickedPhoto(bytes)
            }
        }
    }

    val cameraLauncher = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { success ->
        val uri = pendingCameraUri
        if (success && uri != null) {
            scope.launch {
                val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
                if (bytes != null) handlePickedPhoto(bytes)
            }
        }
    }

    val cameraPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted ->
        if (granted) {
            val uri = createCaptureUri(context)
            pendingCameraUri = uri
            cameraLauncher.launch(uri)
        } else {
            errorMessage = "Camera permission is needed to take a photo."
        }
    }

    fun launchCamera() {
        val hasPermission = ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED
        if (hasPermission) {
            val uri = createCaptureUri(context)
            pendingCameraUri = uri
            cameraLauncher.launch(uri)
        } else {
            cameraPermissionLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    fun launchGallery() {
        galleryLauncher.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
    }

    fun save() {
        val bytes = pendingImageBytes ?: return
        scope.launch {
            isSaving = true
            errorMessage = null
            try {
                model.createKnownFace(label.trim(), bytes)
                onBack()
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not save this person."
            } finally {
                isSaving = false
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader(if (isNew) "Add Person" else label, onBack) {
            if (isNew) {
                if (isSaving) {
                    CircularProgressIndicator(Modifier.size(20.dp))
                } else {
                    TextButton(onClick = ::save, enabled = label.isNotBlank() && pendingImageBytes != null) {
                        Text("Save")
                    }
                }
            }
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        // Renaming an enrolled person isn't supported by the server yet - the
                        // field is read-only once created rather than silently doing nothing.
                        OutlinedTextField(
                            value = label,
                            onValueChange = { label = it },
                            label = { Text("Name") },
                            singleLine = true,
                            enabled = isNew,
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text(if (isNew) "Photo" else "Photos", style = MaterialTheme.typography.titleSmall)

                        if (isNew) {
                            pendingImageBytes?.let { bytes ->
                                val bitmap = remember(bytes) { BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }
                                bitmap?.let {
                                    Image(
                                        bitmap = it.asImageBitmap(),
                                        contentDescription = null,
                                        modifier = Modifier.fillMaxWidth().heightIn(max = 220.dp),
                                        contentScale = ContentScale.Fit,
                                    )
                                }
                            }
                        } else {
                            Row(
                                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                            ) {
                                enrollments.forEach { enrollment ->
                                    EnrollmentThumbnail(
                                        model = model,
                                        face = face!!,
                                        enrollment = enrollment,
                                        onRemoved = { enrollments = enrollments.filterNot { it.id == enrollment.id } },
                                        onError = { errorMessage = it },
                                    )
                                }
                            }
                        }

                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            OutlinedButton(onClick = ::launchCamera, modifier = Modifier.weight(1f)) {
                                Text("Take Photo")
                            }
                            OutlinedButton(onClick = ::launchGallery, modifier = Modifier.weight(1f)) {
                                Text("Choose Photo")
                            }
                        }
                        Text(
                            if (isNew) {
                                "Use a clear, front-facing photo of just this person."
                            } else {
                                "More photos, from different angles or lighting, improve matching."
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            errorMessage?.let { message ->
                item {
                    Text(
                        message,
                        color = SentinelRed,
                        modifier = Modifier.fillMaxWidth().widthIn(max = 900.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun EnrollmentThumbnail(
    model: SentinelViewModel,
    face: KnownFace,
    enrollment: KnownFaceEnrollment,
    onRemoved: () -> Unit,
    onError: (String) -> Unit,
) {
    val scope = rememberCoroutineScope()
    Box(modifier = Modifier.size(72.dp)) {
        SentinelRemoteImage(
            model = model,
            path = enrollment.photoUrl,
            modifier = Modifier.fillMaxSize().clip(RoundedCornerShape(8.dp)),
            description = face.label,
        )
        IconButton(
            onClick = {
                scope.launch {
                    try {
                        model.deleteKnownFaceEnrollment(face, enrollment.id)
                        onRemoved()
                    } catch (error: Exception) {
                        onError(error.message ?: "Could not remove that photo.")
                    }
                }
            },
            modifier = Modifier.align(Alignment.TopEnd).size(22.dp),
        ) {
            Icon(Icons.Default.Cancel, contentDescription = "Remove photo", tint = SentinelRed)
        }
    }
}

private fun createCaptureUri(context: Context): Uri {
    val directory = File(context.externalCacheDir, "captures").apply { mkdirs() }
    val file = File(directory, "capture_${System.currentTimeMillis()}.jpg")
    return FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
}
